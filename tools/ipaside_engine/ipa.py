"""IPA inspection and preparation.

Everything that happens to an ``.ipa`` *before* code signing:

- ``inspect`` - read an IPA's identity and contents without extracting it.
- ``prepare`` - extract, strip app extensions (a free Apple ID cannot register the
  many App IDs a big app's extensions require), patch ``Info.plist`` (bundle id,
  display name, version, device restrictions), detect FairPlay encryption (a
  still-encrypted binary is AMFI-killed on launch even if signed), and repack.

None of this needs an Apple ID or a certificate; signing (with zsign) is a later
step that consumes the prepared bundle.
"""

from __future__ import annotations

import base64
import plistlib
import re
import shutil
import struct
import tempfile
import xml.parsers.expat
import zipfile
import zlib
from pathlib import Path
from typing import Any

from . import macho_inject, zip_copy
from .errors import EngineError


class IpaError(EngineError):
    """An ``.ipa`` that cannot be read, said the way a person would say it.

    For the situations rather than the bugs: a path that no longer exists, a file
    that is not a zip, a zip with no app inside. Every sideload and every unattended
    refresh begins by reading the source IPA, so this is what stands between a file
    the user moved and a stack trace in an error banner.
    """


# Mach-O magics.
_FAT_MAGICS = {0xCAFEBABE, 0xBEBAFECA, 0xCAFEBABF, 0xBFBAFECA}
# Read as a big-endian integer from disk: 32-bit big-endian, 32-bit little-endian, 64-bit
# big-endian, 64-bit little-endian. The 32-bit little-endian one is 0xCEFAEDFE (the bytes
# CE FA ED FE); it used to read 0xECFAEDFE, so thin 32-bit apps - the only kind an old
# iPad or iPhone can run - were seen as "not a Mach-O" and their architecture was lost.
_THIN_MAGICS = {0xFEEDFACE, 0xCEFAEDFE, 0xFEEDFACF, 0xCFFAEDFE}
_LC_ENCRYPTION_INFO = 0x21
_LC_ENCRYPTION_INFO_64 = 0x2C


def _arch_name(cputype: int, cpusubtype: int) -> str:
    sub = cpusubtype & 0x00FFFFFF  # drop capability bits
    if cputype == 0x0100000C:  # CPU_TYPE_ARM64
        return "arm64e" if sub == 2 else "arm64"
    if cputype == 0x0200000C:  # CPU_TYPE_ARM64_32
        return "arm64_32"
    if cputype == 0x0000000C:  # CPU_TYPE_ARM
        return {11: "armv7s"}.get(sub, "armv7")
    return f"cpu{cputype}"


def macho_arches(path: str | Path) -> set[str]:
    """Return the CPU architectures in a Mach-O file (e.g. {'arm64', 'arm64e'})."""
    try:
        with open(path, "rb") as fh:
            data = fh.read(1 << 16)
    except OSError:
        return set()
    return macho_arches_from_header(data)


def macho_arches_from_header(data: bytes) -> set[str]:
    """The CPU architectures named by the first bytes of a Mach-O (thin or universal) file."""
    if len(data) < 8:
        return set()
    magic = struct.unpack_from(">I", data, 0)[0]
    arches: set[str] = set()
    if magic in _FAT_MAGICS:
        is64 = magic in (0xCAFEBABF, 0xBFBAFECA)
        nfat = struct.unpack_from(">I", data, 4)[0]
        entry_size = 32 if is64 else 20
        for i in range(nfat):
            entry = 8 + i * entry_size
            if entry + 8 > len(data):
                break
            cputype, cpusubtype = struct.unpack_from(">II", data, entry)  # fat headers are big-endian
            arches.add(_arch_name(cputype, cpusubtype))
    elif magic in _THIN_MAGICS:
        endian = ">" if magic in (0xFEEDFACE, 0xFEEDFACF) else "<"
        cputype, cpusubtype = struct.unpack_from(endian + "II", data, 4)
        arches.add(_arch_name(cputype, cpusubtype))
    return arches

_INFO_PLIST_RE = re.compile(r"^Payload/[^/]+\.app/Info\.plist$")


def missing_ipa_error(ipa_path: str) -> IpaError:
    """The words for "that file is not there any more", in one place.

    Two callers reach this situation: reading an IPA, and the refresh guard that
    checks the recorded source before it spends a lockdown round trip finding out.
    One phrasing for one situation, so the app never says it two ways.
    """
    name = Path(ipa_path).name or ipa_path
    return IpaError(f"Could not find {name}. It may have been moved, renamed or deleted.")


def deepest_entry(ipa_path: str) -> int:
    """Length of the longest path inside an IPA, relative to its root.

    Signing writes the whole bundle out to a scratch directory, so this plus that
    directory's own path is what a signer has to fit inside the platform's path limit.
    Apps built with Swift package dependencies nest frameworks inside frameworks and
    get surprisingly deep - a package product's name appears twice, once as the
    ``.framework`` folder and once as the binary inside it.

    A file that cannot be read is 0 rather than an error: the caller is sizing a
    directory name, and reading the IPA properly is the next thing it will do anyway.
    """
    try:
        with zipfile.ZipFile(ipa_path) as archive:
            return max((len(name) for name in archive.namelist()), default=0)
    except (OSError, zipfile.BadZipFile):
        return 0


def _app_relpath(names: list[str], filename: str) -> str:
    """Return the 'Payload/<Name>.app' prefix from a zip's entry list."""
    for name in names:
        match = _INFO_PLIST_RE.match(name)
        if match:
            return name[: -len("/Info.plist")]
    raise IpaError(
        f"{filename} is a zip archive, but not an .ipa: it has no Payload/<App>.app "
        "inside."
    )


def _immediate_children(names: list[str], prefix: str) -> list[str]:
    """Distinct first-level entry names directly under ``prefix/``."""
    seen: dict[str, None] = {}
    base = prefix.rstrip("/") + "/"
    for name in names:
        if name.startswith(base) and len(name) > len(base):
            child = name[len(base):].split("/", 1)[0]
            seen.setdefault(child, None)
    return list(seen)


def _icon_basenames(info: dict[str, Any]) -> list[str]:
    """Home-screen icon file base names from Info.plist, primary icon first."""
    names: list[str] = []
    for key in ("CFBundleIcons", "CFBundleIcons~ipad"):
        icons = info.get(key)
        if isinstance(icons, dict):
            primary = icons.get("CFBundlePrimaryIcon")
            if isinstance(primary, dict):
                files = primary.get("CFBundleIconFiles")
                if isinstance(files, list):
                    names.extend(str(f) for f in files)
    legacy = info.get("CFBundleIconFiles")
    if isinstance(legacy, list):
        names.extend(str(f) for f in legacy)
    single = info.get("CFBundleIconFile")
    if isinstance(single, str):
        names.append(single)

    out: list[str] = []
    for n in names:
        stem = n[:-4] if n.lower().endswith(".png") else n
        if stem and stem not in out:
            out.append(stem)
    return out


def _paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def _png_chunk(ctype: bytes, data: bytes) -> bytes:
    return (
        struct.pack(">I", len(data))
        + ctype
        + data
        + struct.pack(">I", zlib.crc32(ctype + data) & 0xFFFFFFFF)
    )


def _normalize_png(data: bytes) -> bytes:
    """Return a browser-displayable PNG.

    Apple's build tools rewrite icon PNGs into an *iOS-optimized* (CgBI) form: a
    ``CgBI`` chunk, raw-DEFLATE ``IDAT``, byte-swapped BGRA channels, and
    premultiplied alpha. Chromium cannot decode that, so CgBI icons are converted
    back to a standard 8-bit truecolor-alpha PNG. Standard PNGs pass through.
    """
    sig = b"\x89PNG\r\n\x1a\n"
    if data[:8] != sig or b"CgBI" not in data[:64]:
        return data

    ihdr = b""
    idat = bytearray()
    off = 8
    while off + 12 <= len(data):
        length = struct.unpack_from(">I", data, off)[0]
        ctype = data[off + 4 : off + 8]
        body = data[off + 8 : off + 8 + length]
        off += 12 + length
        if ctype == b"IHDR":
            ihdr = body
        elif ctype == b"IDAT":
            idat += body
        elif ctype == b"IEND":
            break

    if len(ihdr) < 13:
        raise ValueError("bad IHDR")
    width, height = struct.unpack_from(">II", ihdr, 0)
    bit_depth, color_type, interlace = ihdr[8], ihdr[9], ihdr[12]
    if bit_depth != 8 or color_type != 6 or interlace != 0:
        raise ValueError("unsupported CgBI variant")

    bpp = 4
    stride = width * bpp
    raw = zlib.decompress(bytes(idat), -15)  # CgBI IDAT is headerless DEFLATE

    pixels = bytearray(stride * height)
    prev = bytearray(stride)
    pos = 0
    for row in range(height):
        ft = raw[pos]
        pos += 1
        line = bytearray(raw[pos : pos + stride])
        pos += stride
        if ft == 1:  # Sub
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 0xFF
        elif ft == 2:  # Up
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:  # Average
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ft == 4:  # Paeth
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                c = prev[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + _paeth(a, prev[i], c)) & 0xFF
        elif ft != 0:
            raise ValueError("bad PNG filter")
        pixels[row * stride : (row + 1) * stride] = line
        prev = line

    # BGRA + premultiplied alpha -> straight RGBA.
    for i in range(0, len(pixels), 4):
        b, g, r, a = pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]
        if a and a != 255:
            r = min(255, (r * 255 + a // 2) // a)
            g = min(255, (g * 255 + a // 2) // a)
            b = min(255, (b * 255 + a // 2) // a)
        pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3] = r, g, b, a

    refiltered = bytearray()
    for row in range(height):
        refiltered.append(0)  # filter type: None
        refiltered += pixels[row * stride : (row + 1) * stride]

    out = bytearray(sig)
    out += _png_chunk(b"IHDR", ihdr)
    out += _png_chunk(b"IDAT", zlib.compress(bytes(refiltered), 9))
    out += _png_chunk(b"IEND", b"")
    return bytes(out)


def _extract_icon(
    zf: zipfile.ZipFile, names: list[str], app: str, info: dict[str, Any]
) -> str | None:
    """Return the app's home-screen icon as a ``data:image/png;base64,...`` URI."""
    try:
        basenames = _icon_basenames(info) or ["AppIcon", "Icon"]
        root = app + "/"
        sizes = {zi.filename: zi.file_size for zi in zf.infolist()}
        best: tuple[int, str] | None = None
        for base in basenames:  # honor primary-icon ordering
            for name in names:
                if not name.startswith(root):
                    continue
                rel = name[len(root):]
                if "/" in rel or not rel.lower().endswith(".png"):
                    continue
                stem = rel[:-4]
                if stem == base or stem.startswith(base):
                    size = sizes.get(name, 0)
                    if best is None or size > best[0]:
                        best = (size, name)
            if best is not None:
                break
        if best is None:
            return None
        png = _normalize_png(zf.read(best[1]))
        if len(png) > 900_000:  # keep the JSON payload sane
            return None
        return "data:image/png;base64," + base64.b64encode(png).decode("ascii")
    except Exception:
        return None


#: The ARM architectures an iPhone, iPad, Apple TV or Watch binary can have. Anything else a
#: header seems to name (a damaged header decodes to nonsense like ``cpu1667590255``, a
#: Simulator build is x86_64) is not reported, so it can never be mistaken for a real slice.
_DEVICE_ARCHES = {"armv7", "armv7s", "arm64", "arm64e", "arm64_32"}


def _executable_arches(zf: zipfile.ZipFile, names: list[str], app: str, info: dict[str, Any]) -> list[str]:
    """CPU architectures of the app's main executable, read from its header only.

    Empty when the executable is missing, is not a Mach-O file, or names nothing recognisable:
    "could not tell" must stay distinguishable from "has none", because callers use this to
    explain a failure and must not accuse an app they could not read.
    """
    executable = info.get("CFBundleExecutable")
    entry = f"{app}/{executable}" if executable else ""
    if not entry or entry not in names:
        return []
    try:
        with zf.open(entry) as handle:
            head = handle.read(1 << 16)
    except Exception:  # noqa: BLE001 - an unreadable member is simply "unknown"
        return []
    return sorted(a for a in macho_arches_from_header(head) if a in _DEVICE_ARCHES)


def inspect(ipa_path: str) -> dict[str, Any]:
    """Read an IPA's identity and structure without extracting it.

    Raises :class:`IpaError` for anything wrong with the file itself. Doing that here
    rather than at each call site is what lets a sideload, a background refresh and
    the CLI all report the same sentence: they every one begin by reading the IPA, and
    the file can have been moved since it was chosen.
    """
    name = Path(ipa_path).name or ipa_path
    try:
        with zipfile.ZipFile(ipa_path) as zf:
            names = zf.namelist()
            app = _app_relpath(names, name)
            info = plistlib.loads(zf.read(f"{app}/Info.plist"))
            icon = _extract_icon(zf, names, app, info)
            architectures = _executable_arches(zf, names, app, info)
    except IpaError:
        raise  # already phrased for a person
    except FileNotFoundError:
        raise missing_ipa_error(ipa_path) from None
    except zipfile.BadZipFile:
        raise IpaError(f"{name} is not an .ipa file (it is not a zip archive).") from None
    except zlib.error:
        raise IpaError(f"{name} is damaged and could not be unpacked.") from None
    except plistlib.InvalidFileException:
        raise IpaError(f"{name} has a damaged Info.plist and cannot be read.") from None
    except OSError as exc:
        raise IpaError(f"Could not read {name}: {exc.strerror or exc}.") from None

    frameworks = sorted(_immediate_children(names, f"{app}/Frameworks"))
    extensions = sorted(
        c for c in _immediate_children(names, f"{app}/PlugIns") if c.endswith(".appex")
    )
    has_watch = any(n.startswith(f"{app}/Watch/") for n in names)
    has_sc_info = any("/SC_Info/" in n for n in names)

    return {
        "app_bundle": app.split("/", 1)[1],
        "bundle_id": info.get("CFBundleIdentifier"),
        "display_name": info.get("CFBundleDisplayName") or info.get("CFBundleName"),
        "icon": icon,
        "executable": info.get("CFBundleExecutable"),
        "version": info.get("CFBundleShortVersionString"),
        "build": info.get("CFBundleVersion"),
        "minimum_os": info.get("MinimumOSVersion"),
        "architectures": architectures,
        "platform_sdk": info.get("DTPlatformVersion"),
        "device_family": info.get("UIDeviceFamily"),
        "platform": _platform(info),
        "has_embedded_provision": f"{app}/embedded.mobileprovision" in names,
        "has_sc_info": has_sc_info,
        "frameworks": frameworks,
        "extensions": extensions,
        "has_watch_app": has_watch,
    }


def extension_identities(ipa_path: str) -> list[tuple[str, str]]:
    """``(appex folder, bundle id)`` for every extension, at any depth."""
    name = Path(ipa_path).name or ipa_path
    found: list[tuple[str, str]] = []
    with zipfile.ZipFile(ipa_path) as zf:
        names = zf.namelist()
        app = _app_relpath(names, name)
        prefix = f"{app}/"
        for entry in names:
            if not entry.startswith(prefix) or "/EmbeddedPayload/" in entry:
                continue
            if entry == f"{app}/Info.plist" or not entry.endswith((".appex/Info.plist", ".app/Info.plist")):
                continue
            folder = entry[: -len("/Info.plist")].rsplit("/", 1)[-1]
            info = plistlib.loads(zf.read(entry))
            identifier = info.get("CFBundleIdentifier")
            if isinstance(identifier, str) and identifier:
                found.append((folder, identifier))
    return found


_APP_GROUPS_KEY = "com.apple.security.application-groups"


def app_group_identifiers(ipa_path: str) -> list[str]:
    """App Group identifiers embedded in the main executable's signature.

    A free provisioning profile only grants a group after that group is registered
    on the App ID. The signer has to read these before it asks Apple for the
    profile, or the signed app loses them.
    """
    name = Path(ipa_path).name or ipa_path
    try:
        with zipfile.ZipFile(ipa_path) as zf:
            names = zf.namelist()
            app = _app_relpath(names, name)
            info = plistlib.loads(zf.read(f"{app}/Info.plist"))
            executable = info.get("CFBundleExecutable")
            if not isinstance(executable, str) or "/" in executable or "\\" in executable:
                return []
            entry = f"{app}/{executable}"
            if entry not in names:
                return []
            data = zf.read(entry)
    except (IpaError, FileNotFoundError, zipfile.BadZipFile, plistlib.InvalidFileException, zlib.error, OSError):
        return []
    return _groups_in_signature(data)


def _groups_in_signature(data: bytes) -> list[str]:
    """Pull `group.*` identifiers out of an entitlements plist stored in a binary.

    The signature also contains other plists, so the blob is the XML document that
    actually holds the App Group key, not the first `<?xml` in the file.
    """
    groups: list[str] = []
    needle = _APP_GROUPS_KEY.encode()
    pos = 0
    while True:
        at = data.find(needle, pos)
        if at < 0:
            break
        pos = at + len(needle)
        start = data.rfind(b"<?xml", 0, at)
        end = data.find(b"</plist>", at)
        if start < 0 or end < 0:
            continue
        blob = data[start:end + len(b"</plist>")]
        try:
            parsed = plistlib.loads(blob)
        except (plistlib.InvalidFileException, ValueError, xml.parsers.expat.ExpatError):
            continue
        raw = parsed.get(_APP_GROUPS_KEY) if isinstance(parsed, dict) else None
        if not isinstance(raw, list):
            continue
        for item in raw:
            if isinstance(item, str) and item.startswith("group.") and item not in groups:
                groups.append(item)
    return groups


# What an app says it runs on. `CFBundleSupportedPlatforms` is the direct statement;
# `UIDeviceFamily` is the fallback, where 1 is iPhone, 2 iPad, 3 Apple TV, 4 Watch.
_PLATFORM_NAMES = {
    "iphoneos": "ios",
    "appletvos": "tvos",
    "watchos": "watchos",
    "macosx": "macos",
    "xros": "visionos",
}
_FAMILY_NAMES = {1: "ios", 2: "ios", 3: "tvos", 4: "watchos", 7: "visionos"}

# How to name each one when telling somebody iPASide cannot install it.
PLATFORM_LABELS = {
    "tvos": "an Apple TV (tvOS)",
    "watchos": "an Apple Watch (watchOS)",
    "macos": "a Mac",
    "visionos": "an Apple Vision Pro (visionOS)",
}


def _platform(info: dict[str, Any]) -> str | None:
    """Which Apple platform this app is built for, or None if it will not say.

    Worth knowing because a tvOS or watchOS `.ipa` looks exactly like an iOS one from
    the outside: same zip, same `Payload/<App>.app`, same Info.plist keys. Provisioning
    one as iOS succeeds all the way to the device, which then rejects it — so the useful
    place to notice is before any of that happens.
    """
    supported = info.get("CFBundleSupportedPlatforms")
    if isinstance(supported, list):
        for entry in supported:
            name = _PLATFORM_NAMES.get(str(entry).strip().lower())
            if name:
                return name

    family = info.get("UIDeviceFamily")
    families = family if isinstance(family, list) else [family]
    names = {_FAMILY_NAMES.get(f) for f in families if isinstance(f, int)}
    names.discard(None)
    # A universal iPhone/iPad build is [1, 2]; anything that also claims iOS is iOS.
    if "ios" in names:
        return "ios"
    if len(names) == 1:
        return names.pop()
    return None


def _read_thin_cryptid(data: bytes, base: int) -> bool | None:
    """Return True if the Mach-O slice at ``base`` has a non-zero cryptid."""
    magic = struct.unpack_from(">I", data, base)[0]
    if magic in (0xFEEDFACF, 0xFEEDFACE):  # big-endian
        endian = ">"
        is64 = magic == 0xFEEDFACF
    elif magic in (0xCFFAEDFE, 0xCEFAEDFE):  # little-endian
        endian = "<"
        is64 = magic == 0xCFFAEDFE
    else:
        return None

    header_size = 32 if is64 else 28
    ncmds = struct.unpack_from(endian + "I", data, base + 16)[0]
    offset = base + header_size
    for _ in range(ncmds):
        if offset + 8 > len(data):
            break
        cmd, cmdsize = struct.unpack_from(endian + "II", data, offset)
        if cmd in (_LC_ENCRYPTION_INFO, _LC_ENCRYPTION_INFO_64):
            cryptid = struct.unpack_from(endian + "I", data, offset + 16)[0]
            return cryptid != 0
        if cmdsize == 0:
            break
        offset += cmdsize
    return False


def is_encrypted(macho_path: Path) -> bool | None:
    """Return True/False if a Mach-O is FairPlay-encrypted, or None if unknown.

    Reads the header + load commands (bounded), handling fat and thin binaries.
    """
    try:
        with open(macho_path, "rb") as fh:
            data = fh.read(1 << 20)  # 1 MiB is far more than header + load commands
    except OSError:
        return None
    if len(data) < 8:
        return None

    magic = struct.unpack_from(">I", data, 0)[0]
    if magic in _FAT_MAGICS:
        is64 = magic in (0xCAFEBABF, 0xBFBAFECA)
        nfat = struct.unpack_from(">I", data, 4)[0]
        entry_size = 32 if is64 else 20
        results: list[bool] = []
        for i in range(nfat):
            entry = 8 + i * entry_size
            if is64:
                slice_off = struct.unpack_from(">Q", data, entry + 8)[0]
            else:
                slice_off = struct.unpack_from(">I", data, entry + 8)[0]
            if slice_off + 8 <= len(data):
                res = _read_thin_cryptid(data, slice_off)
                if res is not None:
                    results.append(res)
        if not results:
            return None
        return any(results)
    if magic in _THIN_MAGICS:
        return _read_thin_cryptid(data, 0)
    return None


def _zip_dir(source: Path, output: str, compresslevel: int) -> None:
    compression = zipfile.ZIP_STORED if compresslevel <= 0 else zipfile.ZIP_DEFLATED
    with zipfile.ZipFile(
        output, "w", compression=compression, compresslevel=compresslevel or None
    ) as zf:
        for path in sorted(source.rglob("*")):
            if path.is_file():
                zf.write(path, path.relative_to(source).as_posix())


def prepare(
    ipa_path: str,
    output_path: str | None = None,
    *,
    strip_extensions: bool = True,
    remove_device_restrictions: bool = True,
    bundle_id: str | None = None,
    display_name: str | None = None,
    bundle_version: str | None = None,
    compresslevel: int = 0,
    keep_workdir: bool = False,
) -> dict[str, Any]:
    """Extract, patch, and (optionally) repack an IPA for sideloading.

    Returns a report describing what changed and whether the app is encrypted.
    """
    work = Path(tempfile.mkdtemp(prefix="ipaside_prep_"))
    try:
        with zipfile.ZipFile(ipa_path) as zf:
            zf.extractall(work)

        payload = work / "Payload"
        app = next(payload.glob("*.app"))

        removed: list[str] = []
        if strip_extensions:
            for sub in ("PlugIns", "Watch"):
                target = app / sub
                if target.exists():
                    shutil.rmtree(target)
                    removed.append(sub)

        info_path = app / "Info.plist"
        info = plistlib.loads(info_path.read_bytes())
        changes: dict[str, Any] = {}
        if bundle_id:
            info["CFBundleIdentifier"] = bundle_id
            changes["CFBundleIdentifier"] = bundle_id
        if display_name:
            info["CFBundleDisplayName"] = display_name
            changes["CFBundleDisplayName"] = display_name
        if bundle_version:
            info["CFBundleShortVersionString"] = bundle_version
            changes["CFBundleShortVersionString"] = bundle_version
        if remove_device_restrictions and "UISupportedDevices" in info:
            del info["UISupportedDevices"]
            changes["UISupportedDevices"] = "removed"
        info_path.write_bytes(plistlib.dumps(info, fmt=plistlib.FMT_BINARY))

        main_exe = app / info.get("CFBundleExecutable", "")
        encrypted = is_encrypted(main_exe) if main_exe.exists() else None

        remaining_frameworks = (
            sorted(p.name for p in (app / "Frameworks").iterdir())
            if (app / "Frameworks").is_dir()
            else []
        )

        if output_path:
            _zip_dir(work, output_path, compresslevel)

        return {
            "input": ipa_path,
            "output": output_path,
            "app_bundle": app.name,
            "removed": removed,
            "info_plist_changes": changes,
            "encrypted": encrypted,
            "remaining_frameworks": remaining_frameworks,
            "workdir": str(work) if keep_workdir else None,
            "payload_dir": str(payload) if keep_workdir else None,
        }
    finally:
        if not keep_workdir:
            shutil.rmtree(work, ignore_errors=True)


def export_unsigned(
    ipa_path: str,
    output_path: str,
    *,
    strip_extensions: bool = True,
    keep_extensions: list[str] | None = None,
    remove_device_restrictions: bool = True,
    bundle_id: str | None = None,
    display_name: str | None = None,
    bundle_version: str | None = None,
    enable_file_sharing: bool = False,
    dylibs: list[str] | None = None,
    weak_dylibs: bool = False,
    compresslevel: int = 1,
) -> dict[str, Any]:
    """Apply sideload options and tweaks, then write an IPA with no signature.

    The same edits a signed sideload makes to Info.plist, extensions, and
    injected libraries. Nothing is signed and no Apple ID is contacted. The
    file is meant to be signed later by another tool.

    ``keep_extensions`` names ``PlugIns`` folders to leave in place, such as
    ``Widget.appex``. It only applies when ``strip_extensions`` is false. An
    empty list keeps every extension. The watch app is still removed.
    """
    with zipfile.ZipFile(ipa_path) as zf:
        names = zf.namelist()
        app = _app_relpath(names, Path(ipa_path).name)
        info_name = f"{app}/Info.plist"
        if info_name not in names:
            raise IpaError("This IPA's app has no Info.plist.")
        info = plistlib.loads(zf.read(info_name))
        exe_name = info.get("CFBundleExecutable")
        if not isinstance(exe_name, str) or not exe_name:
            raise IpaError("This IPA's app has no main executable.")
        exe_path = f"{app}/{exe_name}"
        if exe_path not in names:
            raise IpaError("This IPA's app has no main executable.")
        executable = bytearray(zf.read(exe_path))

    changes: dict[str, Any] = {}
    if bundle_id:
        info["CFBundleIdentifier"] = bundle_id
        changes["CFBundleIdentifier"] = bundle_id
    if display_name:
        info["CFBundleDisplayName"] = display_name
        changes["CFBundleDisplayName"] = display_name
    if bundle_version:
        info["CFBundleShortVersionString"] = bundle_version
        changes["CFBundleShortVersionString"] = bundle_version
    if remove_device_restrictions and "UISupportedDevices" in info:
        del info["UISupportedDevices"]
        changes["UISupportedDevices"] = "removed"
    if enable_file_sharing:
        info["UIFileSharingEnabled"] = True
        info["UISupportsDocumentBrowser"] = True
        changes["UIFileSharingEnabled"] = True

    removed = _extension_prefixes(names, app, strip_extensions, keep_extensions)
    injected: list[str] = []
    extras: dict[str, bytes] = {}
    seen_names: set[str] = set()
    for entry in dylibs or []:
        source = Path(entry)
        if not source.is_file():
            raise IpaError(f"Tweak not found: {source.name or entry}")
        if source.name in seen_names:
            raise IpaError(f"Two tweaks are both named {source.name}.")
        seen_names.add(source.name)
        if not macho_inject.is_macho(source):
            raise IpaError(f"{source.name} is not a Mach-O library.")
        load_path = f"@executable_path/{source.name}"
        macho_inject.inject_load_dylib(executable, load_path, weak=weak_dylibs)
        extras[f"{app}/{source.name}"] = source.read_bytes()
        injected.append(load_path)

    def skip(name: str) -> bool:
        return any(name.startswith(prefix) for prefix in removed)

    zip_copy.repack(
        ipa_path,
        output_path,
        skip=skip,
        replace={
            info_name: plistlib.dumps(info, fmt=plistlib.FMT_BINARY),
            exe_path: bytes(executable),
        },
        extras=extras,
        compresslevel=compresslevel,
    )
    return {
        "input": ipa_path,
        "output": str(output_path),
        "app_bundle": app.split("/", 1)[1],
        "name": info.get("CFBundleDisplayName") or info.get("CFBundleName") or Path(app).name,
        "bundle_id": info.get("CFBundleIdentifier"),
        "removed": sorted({prefix.rstrip("/").rsplit("/", 1)[-1] for prefix in removed}),
        "info_plist_changes": changes,
        "injected": injected,
    }


def _extension_prefixes(
    names: list[str],
    app: str,
    strip_extensions: bool,
    keep_extensions: list[str] | None,
) -> list[str]:
    """Directory prefixes to leave out of the repacked IPA."""
    removed: list[str] = []
    watch = f"{app}/Watch/"
    if any(name.startswith(watch) for name in names):
        removed.append(watch)
    plugins = f"{app}/PlugIns/"
    plugin_names = sorted(
        child for child in _immediate_children(names, f"{app}/PlugIns") if child.endswith(".appex")
    )
    if not plugin_names:
        return removed
    if strip_extensions:
        removed.append(plugins)
        return removed
    if keep_extensions is None:
        return removed
    keep = set(keep_extensions)
    for child in plugin_names:
        if child not in keep:
            removed.append(f"{plugins}{child}/")
    return removed
