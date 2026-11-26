"""Replace an existing Mach-O code signature, or add the slot when the file has none.

A binary that was already signed, which every App Store and sideloaded Mach-O is,
has ``LC_CODE_SIGNATURE`` at the end of ``__LINKEDIT``. A library we inject, such
as the LiveContainer certificate importer, does not. When the header still has
unused zero padding, the missing command is written there and the signature is
placed on a 16-byte boundary at the end of the file. The same replacement path
then fills it in.
"""

from __future__ import annotations

from dataclasses import dataclass


class MachOError(Exception):
    pass


_MAGIC_64 = 0xFEEDFACF
_CIGAM_64 = 0xCFFAEDFE
_MAGIC_32 = 0xFEEDFACE
_CIGAM_32 = 0xCEFAEDFE
_FAT = 0xCAFEBABE
_LC_SEGMENT = 0x1
_LC_SEGMENT_64 = 0x19
_LC_CODE_SIGNATURE = 0x1D
_LC_ENCRYPTION = 0x21
_LC_ENCRYPTION_64 = 0x2C
_MH_EXECUTE = 0x2
_VM_PROT_EXECUTE = 0x4


def _u32(data: bytes | bytearray, offset: int, big: bool) -> int:
    raw = data[offset:offset + 4]
    return int.from_bytes(raw, "big" if big else "little")


def _u64(data: bytes | bytearray, offset: int, big: bool) -> int:
    raw = data[offset:offset + 8]
    return int.from_bytes(raw, "big" if big else "little")


def _write_u32(data: bytearray, offset: int, value: int, big: bool) -> None:
    data[offset:offset + 4] = int(value).to_bytes(4, "big" if big else "little")


def _write_u64(data: bytearray, offset: int, value: int, big: bool) -> None:
    data[offset:offset + 8] = int(value).to_bytes(8, "big" if big else "little")


@dataclass
class SliceInfo:
    is_64_bit: bool
    big_endian: bool
    header_size: int
    is_main_executable: bool
    exec_seg_base: int
    exec_seg_limit: int
    linkedit_vmsize_offset: int
    linkedit_filesize_offset: int
    linkedit_file_offset: int
    code_signature_dataoff_offset: int
    code_signature_datasize_offset: int
    code_limit: int
    existing_signature_size: int


def is_thin_macho(data: bytes) -> bool:
    if len(data) < 4:
        return False
    magic = int.from_bytes(data[:4], "little")
    return magic in (_MAGIC_64, _CIGAM_64, _MAGIC_32, _CIGAM_32)


def is_macho(data: bytes) -> bool:
    if is_thin_macho(data):
        return True
    if len(data) < 4:
        return False
    return int.from_bytes(data[:4], "big") == _FAT


def parse_slice(data: bytes) -> SliceInfo:
    if len(data) < 28:
        raise MachOError("truncated")
    magic = int.from_bytes(data[:4], "little")
    if magic == _MAGIC_64:
        is64, big = True, False
    elif magic == _CIGAM_64:
        is64, big = True, True
    elif magic == _MAGIC_32:
        is64, big = False, False
    elif magic == _CIGAM_32:
        is64, big = False, True
    else:
        raise MachOError("not a Mach-O")
    header_size = 32 if is64 else 28
    if len(data) < header_size:
        raise MachOError("truncated")
    filetype = _u32(data, 12, big)
    ncmds = _u32(data, 16, big)
    sizeofcmds = _u32(data, 20, big)
    if header_size + sizeofcmds > len(data):
        raise MachOError("truncated")

    exec_min: int | None = None
    exec_max: int | None = None
    le_vm = le_file = le_off = None
    sig_off = sig_size_off = code_limit = sig_size = None
    cursor = header_size
    for _ in range(ncmds):
        if cursor + 8 > len(data):
            raise MachOError("truncated")
        cmd = _u32(data, cursor, big)
        cmdsize = _u32(data, cursor + 4, big)
        if cmdsize < 8 or cursor + cmdsize > len(data):
            raise MachOError("truncated")
        if cmd in (_LC_SEGMENT_64, _LC_SEGMENT):
            name = bytes(data[cursor + 8:cursor + 24]).split(b"\x00", 1)[0].decode("utf-8", "replace")
            if cmd == _LC_SEGMENT_64:
                vmsize_at = cursor + 8 + 16 + 8
                fileoff_at = vmsize_at + 8
                filesize_at = fileoff_at + 8
                initprot_at = filesize_at + 8 + 4
                fileoff = _u64(data, fileoff_at, big)
                filesize = _u64(data, filesize_at, big)
                initprot = _u32(data, initprot_at, big)
            else:
                vmsize_at = cursor + 8 + 16 + 4
                fileoff_at = vmsize_at + 4
                filesize_at = fileoff_at + 4
                initprot_at = filesize_at + 4 + 4
                fileoff = _u32(data, fileoff_at, big)
                filesize = _u32(data, filesize_at, big)
                initprot = _u32(data, initprot_at, big)
            if initprot & _VM_PROT_EXECUTE:
                exec_min = fileoff if exec_min is None else min(exec_min, fileoff)
                end = fileoff + filesize
                exec_max = end if exec_max is None else max(exec_max, end)
            if name == "__LINKEDIT":
                le_vm, le_file, le_off = vmsize_at, filesize_at, fileoff
        elif cmd == _LC_CODE_SIGNATURE:
            sig_off = cursor + 8
            sig_size_off = cursor + 12
            code_limit = _u32(data, cursor + 8, big)
            sig_size = _u32(data, cursor + 12, big)
        elif cmd in (_LC_ENCRYPTION, _LC_ENCRYPTION_64):
            if _u32(data, cursor + 16, big) >= 1:
                raise MachOError("this binary is FairPlay-encrypted and cannot be re-signed")
        cursor += cmdsize

    if None in (sig_off, sig_size_off, code_limit, sig_size):
        raise MachOError("this Mach-O has no existing code signature to replace")
    if None in (le_vm, le_file, le_off):
        raise MachOError("no __LINKEDIT segment")
    if code_limit + sig_size > len(data):
        raise MachOError("truncated")
    exec_base = exec_min or 0
    return SliceInfo(
        is_64_bit=is64,
        big_endian=big,
        header_size=header_size,
        is_main_executable=filetype == _MH_EXECUTE,
        exec_seg_base=exec_base,
        exec_seg_limit=(exec_max or 0) - exec_base,
        linkedit_vmsize_offset=le_vm,
        linkedit_filesize_offset=le_file,
        linkedit_file_offset=le_off,
        code_signature_dataoff_offset=sig_off,
        code_signature_datasize_offset=sig_size_off,
        code_limit=code_limit,
        existing_signature_size=sig_size,
    )


def code_data(data: bytes, info: SliceInfo) -> bytes:
    return data[:info.code_limit]


@dataclass
class FatArch:
    cpu_type: int
    cpu_subtype: int
    align: int
    offset: int
    size: int


def fat_architectures(data: bytes) -> list[FatArch] | None:
    if len(data) < 8 or int.from_bytes(data[:4], "big") != _FAT:
        return None
    count = int.from_bytes(data[4:8], "big")
    arches: list[FatArch] = []
    cursor = 8
    for _ in range(count):
        if cursor + 20 > len(data):
            raise MachOError("truncated")
        cpu_type = int.from_bytes(data[cursor:cursor + 4], "big")
        cpu_subtype = int.from_bytes(data[cursor + 4:cursor + 8], "big")
        offset = int.from_bytes(data[cursor + 8:cursor + 12], "big")
        size = int.from_bytes(data[cursor + 12:cursor + 16], "big")
        align = int.from_bytes(data[cursor + 16:cursor + 20], "big")
        if offset + size > len(data):
            raise MachOError("truncated")
        arches.append(FatArch(cpu_type, cpu_subtype, align, offset, size))
        cursor += 20
    return arches


def build_fat(slices: list[tuple[int, int, int, bytes]]) -> bytes:
    """``(cpu_type, cpu_subtype, align, bytes)`` for each already-signed slice."""
    header = bytearray(8 + 20 * len(slices))
    header[0:4] = _FAT.to_bytes(4, "big")
    header[4:8] = len(slices).to_bytes(4, "big")
    body = bytearray()
    for index, (cpu_type, cpu_subtype, align, blob) in enumerate(slices):
        align_bytes = 1 << align
        padding = (align_bytes - ((len(header) + len(body)) % align_bytes)) % align_bytes
        body += bytes(padding)
        offset = len(header) + len(body)
        body += blob
        at = 8 + index * 20
        header[at:at + 4] = cpu_type.to_bytes(4, "big")
        header[at + 4:at + 8] = cpu_subtype.to_bytes(4, "big")
        header[at + 8:at + 12] = offset.to_bytes(4, "big")
        header[at + 12:at + 16] = len(blob).to_bytes(4, "big")
        header[at + 16:at + 20] = align.to_bytes(4, "big")
    return bytes(header) + bytes(body)


def replace_signature(data: bytes, info: SliceInfo, new_signature: bytes) -> bytes:
    result = bytearray(data[:info.code_limit])
    result += new_signature
    _write_u32(result, info.code_signature_datasize_offset, len(new_signature), info.big_endian)
    new_linkedit = info.code_limit + len(new_signature) - info.linkedit_file_offset
    if info.is_64_bit:
        _write_u64(result, info.linkedit_filesize_offset, new_linkedit, info.big_endian)
        _write_u64(result, info.linkedit_vmsize_offset, new_linkedit, info.big_endian)
    else:
        _write_u32(result, info.linkedit_filesize_offset, new_linkedit, info.big_endian)
        _write_u32(result, info.linkedit_vmsize_offset, new_linkedit, info.big_endian)
    return bytes(result)


_LC_LOAD_DYLIB = 0xC
_LC_LOAD_WEAK_DYLIB = 0x80000018


def _header(data: bytes) -> tuple[bool, int, int, int]:
    """``(big_endian, header_size, ncmds, sizeofcmds)`` for one thin slice."""
    if len(data) < 28:
        raise MachOError("truncated")
    magic = int.from_bytes(data[:4], "little")
    if magic == _MAGIC_64:
        big, header = False, 32
    elif magic == _CIGAM_64:
        big, header = True, 32
    elif magic == _MAGIC_32:
        big, header = False, 28
    elif magic == _CIGAM_32:
        big, header = True, 28
    else:
        raise MachOError("not a Mach-O")
    if len(data) < header:
        raise MachOError("truncated")
    ncmds = _u32(data, 16, big)
    sizeofcmds = _u32(data, 20, big)
    if header + sizeofcmds > len(data):
        raise MachOError("truncated")
    return big, header, ncmds, sizeofcmds


def find_dylib_load_command(data: bytes, match) -> tuple[int, int, str] | None:
    """First ``LC_LOAD_DYLIB`` whose path ``match`` accepts: ``(offset, size, path)``."""
    big, header, ncmds, sizeofcmds = _header(data)
    cursor = header
    end = header + sizeofcmds
    for _ in range(ncmds):
        if cursor + 8 > end:
            raise MachOError("truncated")
        cmd = _u32(data, cursor, big)
        cmdsize = _u32(data, cursor + 4, big)
        if cmdsize < 8 or cursor + cmdsize > end:
            raise MachOError("truncated")
        if cmd in (_LC_LOAD_DYLIB, _LC_LOAD_WEAK_DYLIB):
            name_at = _u32(data, cursor + 8, big)
            start = cursor + name_at
            if start > cursor + cmdsize:
                raise MachOError("truncated")
            stop = data.find(b"\x00", start, cursor + cmdsize)
            if stop < 0:
                stop = cursor + cmdsize
            path = data[start:stop].decode("utf-8", "replace")
            if match(path):
                return cursor, cmdsize, path
        cursor += cmdsize
    return None


def remove_load_command(data: bytes, offset: int, cmd_size: int) -> bytes:
    big, header, ncmds, sizeofcmds = _header(data)
    region_end = header + sizeofcmds
    if offset < header or offset + cmd_size > region_end:
        raise MachOError("truncated")
    result = bytearray(data)
    tail = result[offset + cmd_size:region_end]
    result[offset:offset + len(tail)] = tail
    for index in range(offset + len(tail), region_end):
        result[index] = 0
    _write_u32(result, 16, ncmds - 1, big)
    _write_u32(result, 20, sizeofcmds - cmd_size, big)
    return bytes(result)


def insert_load_dylib(data: bytes, path: str, *, weak: bool) -> bytes:
    """Add a dylib load command in the zero padding after the existing commands."""
    big, header, ncmds, sizeofcmds = _header(data)
    encoded = path.encode("utf-8")
    padded = ((len(encoded) // 8) + 1) * 8
    cmd_size = 24 + padded
    insert_at = header + sizeofcmds
    if insert_at + cmd_size > len(data) or any(data[insert_at:insert_at + cmd_size]):
        raise MachOError(
            f"There is no unused space after this Mach-O's load commands to load {path}."
        )
    result = bytearray(data)
    _write_u32(result, insert_at, _LC_LOAD_WEAK_DYLIB if weak else _LC_LOAD_DYLIB, big)
    _write_u32(result, insert_at + 4, cmd_size, big)
    _write_u32(result, insert_at + 8, 24, big)
    result[insert_at + 24:insert_at + 24 + len(encoded)] = encoded
    _write_u32(result, 16, ncmds + 1, big)
    _write_u32(result, 20, sizeofcmds + cmd_size, big)
    return bytes(result)


def replace_dylib_load_command(data: bytes, match, new_path: str, *, weak: bool) -> bytes | None:
    found = find_dylib_load_command(data, match)
    if found is None:
        return None
    offset, cmd_size, _path = found
    return insert_load_dylib(remove_load_command(data, offset, cmd_size), new_path, weak=weak)


def ensure_signature_slot(data: bytes) -> bytes:
    """Give a thin Mach-O an empty code-signature slot when it has none.

    The slot is a 16-byte ``LC_CODE_SIGNATURE`` written into the zero padding
    after the load commands. Its data starts at the next 16-byte boundary at
    the end of the file and is empty until the signer fills it.
    """
    big, header, ncmds, sizeofcmds = _header(data)
    cursor = header
    end = header + sizeofcmds
    has_signature = False
    has_linkedit = False
    for _ in range(ncmds):
        if cursor + 8 > end:
            raise MachOError("truncated")
        cmd = _u32(data, cursor, big)
        cmdsize = _u32(data, cursor + 4, big)
        if cmdsize < 8 or cursor + cmdsize > end:
            raise MachOError("truncated")
        if cmd == _LC_CODE_SIGNATURE:
            has_signature = True
            break
        if cmd in (_LC_SEGMENT_64, _LC_SEGMENT):
            name = bytes(data[cursor + 8:cursor + 24]).split(b"\x00", 1)[0]
            if name == b"__LINKEDIT":
                has_linkedit = True
        cursor += cmdsize
    if has_signature:
        return data
    if not has_linkedit:
        raise MachOError("no __LINKEDIT segment")

    insert_at = header + sizeofcmds
    if insert_at + 16 > len(data) or any(data[insert_at:insert_at + 16]):
        raise MachOError(
            "this Mach-O has no code signature and no unused header space to add one"
        )
    result = bytearray(data)
    pad = (16 - (len(result) % 16)) % 16
    if pad:
        result += bytes(pad)
    _write_u32(result, insert_at, _LC_CODE_SIGNATURE, big)
    _write_u32(result, insert_at + 4, 16, big)
    _write_u32(result, insert_at + 8, len(result), big)
    _write_u32(result, insert_at + 12, 0, big)
    _write_u32(result, 16, ncmds + 1, big)
    _write_u32(result, 20, sizeofcmds + 16, big)
    return bytes(result)
