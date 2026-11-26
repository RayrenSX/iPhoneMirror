"""Sign an IPA the way Tetherless signs one.

Files that this run does not change are copied out of the original zip still
compressed. Everything that changes — the executables, Info.plist, the
provisioning profile, CodeResources, and injected libraries — is written again
and is what the signature hashes.
"""

from __future__ import annotations

import hashlib
import plistlib
import shutil
import tempfile
import zipfile
from contextvars import ContextVar
from pathlib import Path

from .. import zip_copy
from . import bundle as bundles
from . import tweaks
from .binary import SignError, sign_macho
from .code_resources import FileEntry, build as build_code_resources
from .macho import MachOError, is_macho
from .profile import ProfileError, read_plist

_changed: ContextVar[set[str] | None] = ContextVar("ipaside_sign_changed", default=None)
_paths: ContextVar[set[str] | None] = ContextVar("ipaside_sign_paths", default=None)
_hashes: ContextVar[dict[str, tuple[bytes, bytes]] | None] = ContextVar("ipaside_sign_hashes", default=None)
_machos: ContextVar[set[str] | None] = ContextVar("ipaside_sign_machos", default=None)


def sign_package(
    ipa_path: str,
    output_path: str,
    *,
    certificate_der: bytes,
    private_key_der: bytes,
    profile: bytes,
    wwdr_der: bytes,
    bundle_id: str | None = None,
    display_name: str | None = None,
    bundle_version: str | None = None,
    remove_extensions: bool = True,
    keep_extensions: list[str] | None = None,
    remove_watch: bool = True,
    remove_uisd: bool = True,
    enable_file_sharing: bool = False,
    dylibs: list[str] | None = None,
    weak_dylibs: bool = False,
    inject_into_extensions: bool = False,
    entitlements_override: dict | None = None,
    extension_profiles: dict[str, bytes] | None = None,
    substrate_provider: str = "legacy",
    zip_level: int = 1,
    work_dir: str | None = None,
) -> dict:
    source = Path(ipa_path)
    if not source.is_file():
        raise SignError(f"IPA not found: {source}")
    profile_plist = read_plist(profile)
    entitlements = entitlements_override
    if entitlements is None:
        raw = profile_plist.get("Entitlements")
        if not isinstance(raw, dict):
            raise SignError("The provisioning profile has no entitlements.")
        entitlements = raw
    team_id = _team_id(profile_plist)

    parent = Path(work_dir) if work_dir else None
    if parent is not None and not parent.is_dir():
        raise SignError(f"The signing work folder does not exist: {parent}")
    scratch = Path(tempfile.mkdtemp(prefix="ipaside-sign-", dir=parent))
    token = _changed.set(set())
    paths_token = _paths.set(set())
    hashes_token = _hashes.set({})
    machos_token = _machos.set(set())
    try:
        with zipfile.ZipFile(source) as archive:
            original_names = [info.filename.replace("\\", "/") for info in archive.infolist()]
            app_root = _extract(archive, scratch)
        return _sign_tree(
            scratch, source, output_path, original_names, app_root,
            certificate_der=certificate_der, private_key_der=private_key_der,
            profile=profile, wwdr_der=wwdr_der, team_id=team_id, entitlements=entitlements,
            bundle_id=bundle_id, display_name=display_name, bundle_version=bundle_version,
            remove_extensions=remove_extensions, keep_extensions=keep_extensions,
            remove_watch=remove_watch, remove_uisd=remove_uisd,
            enable_file_sharing=enable_file_sharing, dylibs=dylibs or [],
            weak_dylibs=weak_dylibs, inject_into_extensions=inject_into_extensions,
            extension_profiles=extension_profiles or {},
            substrate_provider=substrate_provider,
            zip_level=zip_level,
        )
    except (OSError, plistlib.InvalidFileException, zipfile.BadZipFile, ProfileError, MachOError) as exc:
        raise SignError(str(exc)) from exc
    finally:
        _changed.reset(token)
        _paths.reset(paths_token)
        _hashes.reset(hashes_token)
        _machos.reset(machos_token)
        shutil.rmtree(scratch, ignore_errors=True)


def _sign_tree(scratch, source, output_path, original_names, app_root, **opt) -> dict:
    info_path = app_root + "Info.plist"
    info = plistlib.loads(_read(scratch, info_path))
    original_bundle_id = info.get("CFBundleIdentifier")
    if not isinstance(original_bundle_id, str) or not original_bundle_id:
        raise SignError("This IPA's app has no bundle identifier.")
    executable_name = info.get("CFBundleExecutable")
    if not isinstance(executable_name, str) or not executable_name:
        raise SignError("This IPA's app has no main executable.")
    executable_path = app_root + executable_name
    if not (scratch / executable_path).is_file():
        raise SignError("This IPA's app has no main executable.")

    scoped = opt["bundle_id"] or _bundle_id_from_entitlements(opt["entitlements"], opt["team_id"], original_bundle_id)
    info["CFBundleIdentifier"] = scoped
    if opt["remove_uisd"]:
        info.pop("UISupportedDevices", None)
    if opt["display_name"]:
        info["CFBundleDisplayName"] = opt["display_name"]
    if opt["bundle_version"]:
        info["CFBundleShortVersionString"] = opt["bundle_version"]
    if opt["enable_file_sharing"]:
        info["UIFileSharingEnabled"] = True
    info_bytes = plistlib.dumps(info, fmt=plistlib.FMT_XML)
    _write(scratch, info_path, info_bytes)
    _write(scratch, app_root + "embedded.mobileprovision", opt["profile"])

    removed = _remove_unwanted(scratch, app_root, original_names, opt)
    extras: list[str] = []
    if opt["dylibs"] and opt["inject_into_extensions"]:
        for item in bundles.find_nested_bundles(app_root, _files(scratch), lambda path: _read(scratch, path)):
            if item.is_extension:
                extras.append(item.executable_path)
    try:
        injected = tweaks.inject(
            lambda name: _read(scratch, name),
            lambda name, data: _write(scratch, name, data),
            app_root,
            executable_path,
            opt["dylibs"],
            provider=opt["substrate_provider"],
            extra_executables=extras,
            weak=bool(opt["weak_dylibs"]),
        )
    except macho.MachOError as exc:
        raise SignError(str(exc)) from exc

    paths = _paths.get() or set()
    nested = bundles.find_nested_bundles(app_root, paths, lambda path: _read(scratch, path))
    nested.sort(key=lambda item: item.root_prefix.count("/"), reverse=True)
    sealed: list[tuple[str, bytes, bytes]] = []
    for item in nested:
        _sign_nested(scratch, item, scoped, original_bundle_id, opt, injected, sealed, paths)

    main_executable = executable_path
    nested_roots = [item.root_prefix for item in nested]
    for path in sorted(_machos.get() or ()):
        if path == main_executable or not path.startswith(app_root) or "/EmbeddedPayload/" in path:
            continue
        if path not in (_paths.get() or ()):
            continue
        if any(path.startswith(root) for root in nested_roots):
            continue
        signed = sign_macho(
            _read(scratch, path), scoped, opt["team_id"], opt["certificate_der"],
            opt["private_key_der"], opt["wwdr_der"], is_main_executable=False,
        )
        _write(scratch, path, signed)

    _seal_and_sign(
        scratch, app_root, executable_path, scoped, opt["team_id"], info_bytes,
        opt["entitlements"], opt, sealed, is_main=True,
    )
    _check_sealed(scratch, sealed)
    _repack(source, scratch, output_path, set(original_names), removed, opt["zip_level"])
    return {"output": str(output_path), "bundle_id": scoped, "injected": injected, "removed": removed}


def _sign_nested(scratch, item, scoped_main, original_main, opt, injected, sealed, paths) -> None:
    identifier = item.identifier
    entitlements = None
    info_bytes = item.info_plist
    if item.is_extension:
        own = opt["extension_profiles"].get(item.identifier)
        if not item.identifier.startswith(original_main):
            raise SignError(
                f"The {item.identifier} extension does not start with the app's bundle id, "
                "so it cannot be signed with this app."
            )
        identifier = scoped_main + item.identifier[len(original_main):]
        if own:
            own_plist = read_plist(own)
            raw = own_plist.get("Entitlements")
            if not isinstance(raw, dict):
                raise SignError(f"The profile for {item.identifier} has no entitlements.")
            entitlements = raw
            profile_bytes = own
        else:
            entitlements = opt["entitlements"]
            profile_bytes = opt["profile"]
        info = plistlib.loads(item.info_plist)
        info["CFBundleIdentifier"] = identifier
        info_bytes = plistlib.dumps(info, fmt=plistlib.FMT_XML)
        _write(scratch, item.root_prefix + "Info.plist", info_bytes)
        _write(scratch, item.root_prefix + "embedded.mobileprovision", profile_bytes)
    _seal_and_sign(
        scratch, item.root_prefix, item.executable_path, identifier, opt["team_id"],
        info_bytes, entitlements, opt, sealed, is_main=False,
    )


def _seal_and_sign(scratch, root, executable_path, identifier, team_id, info_bytes, entitlements, opt, sealed, *, is_main: bool) -> None:
    entries = _resource_entries(root, {executable_path})
    for entry in entries:
        sealed.append((root + entry.relative_path, entry.sha1, entry.sha256))
    resources = plistlib.dumps(build_code_resources(entries), fmt=plistlib.FMT_XML)
    signed = sign_macho(
        _read(scratch, executable_path), identifier, team_id, opt["certificate_der"],
        opt["private_key_der"], opt["wwdr_der"],
        info_plist=info_bytes, entitlement_values=entitlements,
        resource_hashes=(hashlib.sha1(resources).digest(), hashlib.sha256(resources).digest()),
        is_main_executable=is_main,
    )
    _write(scratch, executable_path, signed)
    _write(scratch, root + "_CodeSignature/CodeResources", resources)


def _check_sealed(scratch: Path, sealed: list[tuple[str, bytes, bytes]]) -> None:
    hashes = _hashes.get() or {}
    for path, sha1, sha256 in sealed:
        current = hashes.get(path)
        if current == (sha1, sha256):
            continue
        data = _read(scratch, path)
        if hashlib.sha1(data).digest() != sha1 or hashlib.sha256(data).digest() != sha256:
            raise SignError(f"A signed resource changed after it was hashed: {path}")


def _resource_entries(root_prefix: str, exclude: set[str]) -> list[FileEntry]:
    paths = _paths.get() or set()
    hashes = _hashes.get() or {}
    entries: list[FileEntry] = []
    for path in paths:
        if not path.startswith(root_prefix) or path in exclude or path.endswith("/"):
            continue
        relative = path[len(root_prefix):]
        if not relative or relative == "_CodeSignature/CodeResources":
            continue
        digest = hashes.get(path)
        if digest is None:
            raise SignError(f"No hash for {path}")
        entries.append(FileEntry(relative, digest[0], digest[1]))
    return entries


def _forget(prefix: str) -> None:
    paths = _paths.get()
    if paths is not None:
        for name in [item for item in paths if item.startswith(prefix)]:
            paths.discard(name)
    hashes = _hashes.get()
    if hashes is not None:
        for name in [item for item in hashes if item.startswith(prefix)]:
            hashes.pop(name, None)
    machos = _machos.get()
    if machos is not None:
        for name in [item for item in machos if item.startswith(prefix)]:
            machos.discard(name)


def _remove_unwanted(scratch: Path, app_root: str, names: list[str], opt) -> list[str]:
    removed: list[str] = []
    watch = app_root + "Watch/"
    if opt["remove_watch"] and any(name.startswith(watch) for name in names):
        shutil.rmtree(scratch / watch, ignore_errors=True)
        _forget(watch)
        removed.append(watch)
    paths = _files(scratch)
    nested = bundles.find_nested_bundles(app_root, paths, lambda path: _read(scratch, path))
    keep = None if opt["remove_extensions"] else opt["keep_extensions"]
    for item in nested:
        if not item.is_extension:
            continue
        folder = item.root_prefix.rstrip("/").rsplit("/", 1)[-1]
        drop = opt["remove_extensions"] or (keep is not None and keep != [] and folder not in keep)
        if not drop:
            continue
        shutil.rmtree(scratch / item.root_prefix, ignore_errors=True)
        _forget(item.root_prefix)
        removed.append(item.root_prefix)
    if opt["remove_extensions"]:
        plugins = scratch / (app_root + "PlugIns")
        if plugins.exists():
            shutil.rmtree(plugins, ignore_errors=True)
            prefix = app_root + "PlugIns/"
            _forget(prefix)
            if prefix not in removed:
                removed.append(prefix)
    return removed


def _repack(source: Path, scratch: Path, output_path: str, original: set[str], removed: list[str], level: int) -> None:
    changed = _changed.get() or set()
    replaced: dict[str, bytes] = {}
    extras: dict[str, bytes] = {}
    for path in changed:
        if any(path.startswith(prefix) for prefix in removed):
            continue
        data = _read(scratch, path)
        if path in original:
            replaced[path] = data
        else:
            extras[path] = data

    def skip(name: str) -> bool:
        return any(name.startswith(prefix) for prefix in removed)

    zip_copy.repack(source, output_path, skip=skip, replace=replaced, extras=extras, compresslevel=level)


def _extract(archive: zipfile.ZipFile, dest: Path) -> str:
    app_root = None
    paths = _paths.get()
    hashes = _hashes.get()
    machos = _machos.get()
    for info in archive.infolist():
        name = info.filename.replace("\\", "/")
        if not _safe(name):
            raise SignError(f"Refusing an archive entry with an unsafe path: {name}")
        if info.is_dir() or name.endswith("/"):
            continue
        sha1 = hashlib.sha1()
        sha256 = hashlib.sha256()
        body: bytearray | None = None
        first = True
        for chunk in zip_copy.iter_plaintext(archive, info):
            sha1.update(chunk)
            sha256.update(chunk)
            if first:
                first = False
                if is_macho(chunk[:8]) or name.endswith("Info.plist"):
                    body = bytearray()
                    if machos is not None and is_macho(chunk[:8]):
                        machos.add(name)
            if body is not None:
                body += chunk
        if paths is not None:
            paths.add(name)
        if hashes is not None:
            hashes[name] = (sha1.digest(), sha256.digest())
        if body is not None:
            target = dest / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(body)
        parts = name.split("/")
        if app_root is None and len(parts) >= 2 and parts[0] == "Payload" and parts[1].endswith(".app"):
            app_root = f"Payload/{parts[1]}/"
    if app_root is None:
        raise SignError("This IPA has no app bundle.")
    return app_root


def _files(root: Path) -> set[str]:
    found = set()
    for path in root.rglob("*"):
        if path.is_file():
            found.add(path.relative_to(root).as_posix())
    return found


def _read(root: Path, name: str) -> bytes:
    return (root / name).read_bytes()


def _write(root: Path, name: str, data: bytes) -> None:
    bag = _changed.get()
    if bag is not None:
        bag.add(name)
    paths = _paths.get()
    if paths is not None:
        paths.add(name)
    hashes = _hashes.get()
    if hashes is not None:
        hashes[name] = (hashlib.sha1(data).digest(), hashlib.sha256(data).digest())
    machos = _machos.get()
    if machos is not None:
        if is_macho(data[:8]):
            machos.add(name)
        else:
            machos.discard(name)
    target = root / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)


def _safe(name: str) -> bool:
    if not name or name.startswith(("/", "\\")):
        return False
    return ".." not in name.replace("\\", "/").split("/")


def _team_id(profile: dict) -> str:
    for key in ("TeamIdentifier", "ApplicationIdentifierPrefix"):
        value = profile.get(key)
        if isinstance(value, list) and value and isinstance(value[0], str):
            return value[0]
    raise SignError("The provisioning profile does not name a team.")


def _bundle_id_from_entitlements(entitlements: dict, team_id: str, original: str) -> str:
    app_id = entitlements.get("application-identifier")
    prefix = team_id + "."
    if isinstance(app_id, str) and app_id.startswith(prefix):
        return app_id[len(prefix):]
    return original
