"""Copy a zip while leaving untouched entries compressed as they already are.

Changed files are written again. Everything else is copied as the compressed
bytes already in the archive, which is the slow part of unpacking and
re-zipping a large IPA.
"""

from __future__ import annotations

import struct
import zipfile
import zlib
from collections.abc import Callable
from pathlib import Path


def repack(
    source: str | Path,
    output: str | Path,
    *,
    skip: Callable[[str], bool] = lambda _name: False,
    replace: dict[str, bytes] | None = None,
    extras: dict[str, bytes] | None = None,
    compresslevel: int = 1,
) -> None:
    """Write ``output``, copying ``source`` except where ``skip`` / ``replace`` / ``extras`` say."""
    Path(output).parent.mkdir(parents=True, exist_ok=True)
    replaced = replace or {}
    with zipfile.ZipFile(source, "r") as zin, zipfile.ZipFile(output, "w") as zout:
        for info in zin.infolist():
            name = info.filename
            if skip(name) or name in replaced:
                continue
            _copy_compressed(zin, zout, info)
        for name, data in replaced.items():
            if skip(name):
                continue
            _write_new(zout, name, data, compresslevel)
        for name, data in (extras or {}).items():
            _write_new(zout, name, data, compresslevel)


_MACHO_MAGICS = {
    b"\xcf\xfa\xed\xfe",
    b"\xfe\xed\xfa\xcf",
    b"\xce\xfa\xed\xfe",
    b"\xfe\xed\xfa\xce",
    b"\xca\xfe\xba\xbe",
    b"\xbe\xba\xfe\xca",
}


def _write_new(zout: zipfile.ZipFile, name: str, data: bytes, compresslevel: int) -> None:
    method, payload = _compress_if_smaller(data, compresslevel)
    info = zipfile.ZipInfo(filename=name)
    info.compress_type = method
    mode = 0o755 if data[:4] in _MACHO_MAGICS else 0o644
    info.external_attr = mode << 16
    info.CRC = zlib.crc32(data) & 0xFFFFFFFF
    info.file_size = len(data)
    info.compress_size = len(payload)
    if not zout.fp:
        raise zipfile.BadZipFile("The output archive is not open for writing.")
    info.header_offset = zout.fp.tell()
    zout.fp.write(info.FileHeader(False))
    zout.fp.write(payload)
    zout.filelist.append(info)
    zout.NameToInfo[info.filename] = info
    zout.start_dir = zout.fp.tell()


def _compress_if_smaller(data: bytes, level: int) -> tuple[int, bytes]:
    """Raw DEFLATE, kept only when it is smaller than the original bytes."""
    if not data or level <= 0:
        return zipfile.ZIP_STORED, data
    compressor = zlib.compressobj(level, zlib.DEFLATED, wbits=-15)
    deflated = compressor.compress(data) + compressor.flush()
    if len(deflated) < len(data):
        return zipfile.ZIP_DEFLATED, deflated
    return zipfile.ZIP_STORED, data


def _copy_compressed(zin: zipfile.ZipFile, zout: zipfile.ZipFile, info: zipfile.ZipInfo) -> None:
    """Copy one entry's already-compressed payload. Directories are names only."""
    if info.is_dir():
        zout.writestr(info, b"")
        return
    raw = _read_compressed(zin, info)
    copied = zipfile.ZipInfo(filename=info.filename, date_time=info.date_time)
    copied.compress_type = info.compress_type
    copied.external_attr = info.external_attr
    copied.flag_bits = info.flag_bits & ~0x8
    copied.CRC = info.CRC
    copied.file_size = info.file_size
    copied.compress_size = len(raw)
    if not zout.fp:
        raise zipfile.BadZipFile("The output archive is not open for writing.")
    copied.header_offset = zout.fp.tell()
    header = copied.FileHeader(False)
    zout.fp.write(header)
    zout.fp.write(raw)
    zout.filelist.append(copied)
    zout.NameToInfo[copied.filename] = copied
    zout.start_dir = zout.fp.tell()


def _read_compressed(zin: zipfile.ZipFile, info: zipfile.ZipInfo) -> bytes:
    if zin.fp is None:
        raise zipfile.BadZipFile("The source archive is not open.")
    zin.fp.seek(info.header_offset)
    local = zin.fp.read(30)
    if len(local) != 30 or local[:4] != b"PK\x03\x04":
        raise zipfile.BadZipFile(f"{info.filename} has no local file header.")
    name_len, extra_len = struct.unpack_from("<HH", local, 26)
    zin.fp.seek(name_len + extra_len, 1)
    raw = zin.fp.read(info.compress_size)
    if len(raw) != info.compress_size:
        raise zipfile.BadZipFile(f"{info.filename} is shorter than its directory says.")
    return raw


def iter_plaintext(zin: zipfile.ZipFile, info: zipfile.ZipInfo):
    """Yield the entry's bytes without recomputing its CRC.

    The central directory already stores the CRC. Copying an unchanged entry
    uses that value. Hashing a file for the signature does not need a third
    pass over the same bytes.
    """
    if info.is_dir():
        return
    if info.flag_bits & 0x1:
        raise zipfile.BadZipFile(f"{info.filename} is encrypted.")
    if info.compress_type == zipfile.ZIP_STORED:
        raw = _read_compressed(zin, info)
        view = memoryview(raw)
        step = 1024 * 1024
        for start in range(0, len(raw), step):
            yield bytes(view[start:start + step])
        if not raw:
            yield b""
        return
    if info.compress_type == zipfile.ZIP_DEFLATED:
        raw = _read_compressed(zin, info)
        decoder = zlib.decompressobj(wbits=-15)
        view = memoryview(raw)
        step = 256 * 1024
        produced = False
        for start in range(0, len(raw), step):
            chunk = decoder.decompress(view[start:start + step])
            if chunk:
                produced = True
                yield chunk
        tail = decoder.flush()
        if tail:
            produced = True
            yield tail
        if not produced:
            yield b""
        return
    yield zin.read(info)
