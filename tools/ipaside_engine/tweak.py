"""Extract injectable dylibs from Debian tweak packages (.deb).

Injecting a tweak into a sideloaded app means adding its Mach-O dylib(s) to the
app binary. Tweak .debs - rootful (``/Library/...``), rootless (``/var/jb/...``),
or roothide - package the dylib under ``data.tar`` somewhere below
``.../MobileSubstrate/DynamicLibraries/`` (or ``usr/lib``). We pull every Mach-O
dylib out regardless of the install prefix, tag it with its CPU arch(es), and
hand the files to zsign. A ``.dylib`` source is passed through untouched.
"""

from __future__ import annotations

import io
import lzma
import tarfile
from pathlib import Path
from typing import Any

from . import ipa, paths


def _read_ar(data: bytes) -> dict[str, bytes]:
    if data[:8] != b"!<arch>\n":
        raise ValueError("not a .deb package (missing ar header)")
    members: dict[str, bytes] = {}
    offset = 8
    total = len(data)
    while offset + 60 <= total:
        header = data[offset:offset + 60]
        name = header[0:16].decode("ascii", "replace").strip().rstrip("/")
        try:
            size = int(header[48:58].decode("ascii").strip() or "0")
        except ValueError:
            break
        start = offset + 60
        members[name] = data[start:start + size]
        offset = start + size + (size & 1)  # ar members are 2-byte aligned
    return members


def _open_data_tar(name: str, raw: bytes) -> tarfile.TarFile:
    if name.endswith(".zst"):
        try:
            import zstandard
        except ImportError as exc:
            raise ValueError("this .deb is zstd-compressed; the 'zstandard' package is required") from exc
        raw = zstandard.ZstdDecompressor().decompress(raw, max_output_size=512 * 1024 * 1024)
        return tarfile.open(fileobj=io.BytesIO(raw), mode="r:")
    if name.endswith(".lzma"):
        raw = lzma.decompress(raw, format=lzma.FORMAT_AUTO)  # handles legacy .lzma (alone) + .xz
        return tarfile.open(fileobj=io.BytesIO(raw), mode="r:")
    return tarfile.open(fileobj=io.BytesIO(raw), mode="r:*")  # .gz / .bz2 / .xz / plain


def _safe_stem(path: str) -> str:
    stem = Path(path).stem
    cleaned = "".join(c for c in stem if c.isalnum() or c in "._-")
    return cleaned or "tweak"


def extract_deb(deb_path: str) -> list[str]:
    """Extract every Mach-O dylib from a .deb into a per-package cache dir; return paths."""
    data = Path(deb_path).read_bytes()
    members = _read_ar(data)
    data_name = next((n for n in members if n.startswith("data.tar")), None)
    if data_name is None:
        raise ValueError("no data.tar found inside the .deb")

    dest = paths.tweaks_dir() / _safe_stem(deb_path)
    dest.mkdir(parents=True, exist_ok=True)
    extracted: list[str] = []
    with _open_data_tar(data_name, members[data_name]) as tar:
        for member in tar.getmembers():
            if member.isfile() and member.name.lower().endswith(".dylib"):
                stream = tar.extractfile(member)
                if stream is None:
                    continue
                out = dest / Path(member.name).name
                out.write_bytes(stream.read())
                extracted.append(str(out))
    if not extracted:
        raise ValueError("no .dylib found in this .deb")
    return extracted


def resolve(source: str) -> list[dict[str, Any]]:
    """Return the injectable dylib(s) for a .deb or .dylib source, with arch info."""
    path = Path(source)
    suffix = path.suffix.lower()
    if suffix == ".deb":
        dylibs, origin = extract_deb(source), path.name
    elif suffix == ".dylib":
        dylibs, origin = [str(path)], None
    else:
        raise ValueError(f"unsupported tweak type '{path.suffix or path.name}' (use .deb or .dylib)")
    return [
        {"path": d, "name": Path(d).name, "arches": sorted(ipa.macho_arches(d)), "from_deb": origin}
        for d in dylibs
    ]
