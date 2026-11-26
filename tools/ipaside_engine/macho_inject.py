"""Insert a dylib load command into a Mach-O without signing it.

This is the same insertion zsign performs while signing (``LC_LOAD_DYLIB`` or
``LC_LOAD_WEAK_DYLIB`` written into the free space before ``__text``). Exporting
an unsigned IPA stops there: the next signing tool is what applies a signature.
"""

from __future__ import annotations

import struct
from pathlib import Path

from .errors import EngineError

_FAT_MAGIC = 0xCAFEBABE
_FAT_CIGAM = 0xBEBAFECA
_MH_MAGIC_64 = 0xFEEDFACF
_MH_CIGAM_64 = 0xCFFAEDFE
_MH_MAGIC = 0xFEEDFACE
_MH_CIGAM = 0xCEFAEDFE
_LC_SEGMENT = 0x1
_LC_SEGMENT_64 = 0x19
_LC_LOAD_DYLIB = 0xC
_LC_LOAD_WEAK_DYLIB = 0x80000018


class MachOInjectError(EngineError):
    """The binary cannot take another library load command."""


def is_macho(path: Path) -> bool:
    try:
        magic = struct.unpack(">I", path.read_bytes()[:4])[0]
    except (OSError, struct.error):
        return False
    return magic in (_FAT_MAGIC, _FAT_CIGAM, _MH_MAGIC, _MH_CIGAM, _MH_MAGIC_64, _MH_CIGAM_64)


def inject_load_dylib(data: bytearray, load_path: str, *, weak: bool) -> None:
    """Add ``load_path`` to every architecture in ``data``, in place."""
    if len(data) < 8:
        raise MachOInjectError("The executable is too small to be a Mach-O.")
    magic = struct.unpack_from(">I", data, 0)[0]
    if magic in (_FAT_MAGIC, _FAT_CIGAM):
        _inject_fat(data, load_path, weak=weak, big=magic == _FAT_MAGIC)
        return
    _inject_thin(data, 0, len(data), load_path, weak=weak)


def _inject_fat(data: bytearray, load_path: str, *, weak: bool, big: bool) -> None:
    endian = ">" if big else "<"
    nfat = struct.unpack_from(endian + "I", data, 4)[0]
    if nfat > 32:
        raise MachOInjectError("The executable has an implausible number of architectures.")
    for index in range(nfat):
        entry = 8 + index * 20
        offset, size = struct.unpack_from(endian + "II", data, entry + 8)
        end = offset + size
        if end > len(data):
            raise MachOInjectError("An architecture slice runs past the end of the executable.")
        _inject_thin(data, offset, end, load_path, weak=weak)


def _inject_thin(data: bytearray, start: int, end: int, load_path: str, *, weak: bool) -> None:
    if start + 32 > end:
        raise MachOInjectError("An architecture slice is too small to be a Mach-O.")
    magic = struct.unpack_from(">I", data, start)[0]
    if magic in (_MH_MAGIC_64, _MH_CIGAM_64):
        # Read as a big-endian integer: FE ED FA CF is a big-endian header,
        # CF FA ED FE is a little-endian header.
        endian = ">" if magic == _MH_MAGIC_64 else "<"
        header_size = 32
        segment_cmd = _LC_SEGMENT_64
        segment_size = 72
        section_size = 80
        section_offset_at = 48
    elif magic in (_MH_MAGIC, _MH_CIGAM):
        endian = ">" if magic == _MH_MAGIC else "<"
        header_size = 28
        segment_cmd = _LC_SEGMENT
        segment_size = 56
        section_size = 68
        section_offset_at = 40
    else:
        raise MachOInjectError("An architecture slice is not a Mach-O.")

    ncmds, sizeofcmds = struct.unpack_from(endian + "II", data, start + 16)
    commands = start + header_size
    if commands + sizeofcmds > end:
        raise MachOInjectError("The executable's load commands run past the file.")

    wanted = _LC_LOAD_WEAK_DYLIB if weak else _LC_LOAD_DYLIB
    other = _LC_LOAD_DYLIB if weak else _LC_LOAD_WEAK_DYLIB
    cursor = commands
    text_offset: int | None = None
    for _ in range(ncmds):
        if cursor + 8 > commands + sizeofcmds:
            raise MachOInjectError("A load command is truncated.")
        cmd, cmdsize = struct.unpack_from(endian + "II", data, cursor)
        if cmdsize < 8 or cursor + cmdsize > commands + sizeofcmds:
            raise MachOInjectError("A load command has a bad size.")
        if cmd in (_LC_LOAD_DYLIB, _LC_LOAD_WEAK_DYLIB):
            name_at = struct.unpack_from(endian + "I", data, cursor + 8)[0]
            name = _cstring(data, cursor + name_at, cursor + cmdsize)
            if name == load_path:
                if cmd == other:
                    struct.pack_into(endian + "I", data, cursor, wanted)
                return
        if cmd == segment_cmd and text_offset is None:
            text_offset = _text_file_offset(
                data, cursor, cmdsize, endian, segment_size, section_size, section_offset_at
            )
        cursor += cmdsize

    encoded = load_path.encode("utf-8")
    padding = 8 - (len(encoded) % 8)
    command_size = 24 + len(encoded) + padding
    free = 0
    if text_offset is not None and text_offset > header_size + sizeofcmds:
        free = text_offset - header_size - sizeofcmds
    if free < command_size:
        raise MachOInjectError(
            f"There is no room in the executable header to load {load_path}."
        )

    dest = commands + sizeofcmds
    struct.pack_into(endian + "II", data, dest, wanted, command_size)
    struct.pack_into(endian + "I", data, dest + 8, 24)  # name.offset
    struct.pack_into(endian + "I", data, dest + 12, 2)  # timestamp, matching zsign
    data[dest + 24:dest + 24 + len(encoded) + padding] = encoded + bytes(padding)
    struct.pack_into(endian + "I", data, start + 16, ncmds + 1)
    struct.pack_into(endian + "I", data, start + 20, sizeofcmds + command_size)


def _text_file_offset(
    data: bytearray,
    command: int,
    cmdsize: int,
    endian: str,
    segment_size: int,
    section_size: int,
    section_offset_at: int,
) -> int | None:
    if cmdsize < segment_size + 16:
        return None
    segname = bytes(data[command + 8:command + 24]).split(b"\0", 1)[0]
    if segname != b"__TEXT":
        return None
    nsects = struct.unpack_from(endian + "I", data, command + segment_size - 8)[0]
    for index in range(nsects):
        section = command + segment_size + index * section_size
        if section + section_offset_at + 4 > command + cmdsize:
            return None
        name = bytes(data[section:section + 16]).split(b"\0", 1)[0]
        if name == b"__text":
            return struct.unpack_from(endian + "I", data, section + section_offset_at)[0]
    return None


def _cstring(data: bytearray, start: int, limit: int) -> str:
    if start < 0 or start >= limit or start >= len(data):
        return ""
    end = data.find(b"\0", start, min(limit, len(data)))
    if end < 0:
        end = min(limit, len(data))
    return data[start:end].decode("utf-8", "replace")


def inject_dylib_file(executable: Path, dylib: Path, *, weak: bool) -> str:
    """Copy ``dylib`` beside ``executable`` and load it from ``@executable_path``."""
    if not is_macho(dylib):
        raise MachOInjectError(f"{dylib.name} is not a Mach-O library.")
    name = dylib.name
    if not name or name in (".", "..") or "/" in name or "\\" in name:
        raise MachOInjectError(f"{dylib.name} is not a usable library name.")
    destination = executable.parent / name
    if destination.resolve() != dylib.resolve():
        destination.write_bytes(dylib.read_bytes())
    load_path = f"@executable_path/{name}"
    blob = bytearray(executable.read_bytes())
    inject_load_dylib(blob, load_path, weak=weak)
    executable.write_bytes(blob)
    return load_path
