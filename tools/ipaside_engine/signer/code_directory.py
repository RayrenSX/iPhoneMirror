"""CS_CodeDirectory blobs, transcribed from Tetherless ``CodeDirectoryBuilder.swift``."""

from __future__ import annotations

import hashlib
from dataclasses import dataclass, field

from .format import CODE_DIRECTORY, read_u32, u32, u64

PAGE_SIZE_LOG2 = 12
PAGE_SIZE = 1 << 12
_HEADER_SIZE = 88


@dataclass
class SpecialSlots:
    info_plist: bytes = b""
    requirements: bytes = b""
    resource_directory: bytes = b""
    application: bytes = b""
    entitlements: bytes = b""
    reserved: bytes = b""
    der_entitlements: bytes = b""


@dataclass
class ParsedCodeDirectory:
    magic: int
    length: int
    version: int
    flags: int
    hash_offset: int
    ident_offset: int
    n_special_slots: int
    n_code_slots: int
    code_limit: int
    hash_size: int
    hash_type: int
    team_offset: int
    identifier: str
    team_id: str | None
    special_slot_hashes: list[bytes] = field(default_factory=list)
    code_hashes: list[bytes] = field(default_factory=list)


class CodeDirectoryError(Exception):
    pass


def hash_size(hash_type: int) -> int:
    return 32 if hash_type == 2 else 20


def _digest(data: bytes, hash_type: int) -> bytes:
    algo = hashlib.sha256 if hash_type == 2 else hashlib.sha1
    return algo(data).digest()


def code_page_hashes(code_data: bytes, hash_type: int) -> list[bytes]:
    result: list[bytes] = []
    offset = 0
    while offset < len(code_data):
        end = min(offset + PAGE_SIZE, len(code_data))
        result.append(_digest(code_data[offset:end], hash_type))
        offset = end
    return result


def blob_size(
    identifier: str,
    team_id: str | None,
    code_length: int,
    hash_type: int,
) -> int:
    """Byte length of :func:`build` for this code length. Independent of the page hashes."""
    size = hash_size(hash_type)
    pages = (code_length + PAGE_SIZE - 1) // PAGE_SIZE if code_length else 0
    identifier_bytes = len(identifier.encode("utf-8")) + 1
    team_bytes = len(team_id.encode("utf-8")) + 1 if team_id is not None else 0
    return _HEADER_SIZE + identifier_bytes + team_bytes + 7 * size + pages * size


def build(
    identifier: str,
    team_id: str | None,
    code_data: bytes,
    special_slots: SpecialSlots,
    hash_type: int,
    flags: int = 0,
    exec_seg_base: int = 0,
    exec_seg_limit: int = 0,
    exec_seg_flags: int = 0,
) -> bytes:
    size = hash_size(hash_type)
    zero = bytes(size)
    code_hashes = code_page_hashes(code_data, hash_type)

    def slot(value: bytes) -> bytes:
        return value if value else zero

    special = [
        slot(special_slots.der_entitlements),
        slot(special_slots.reserved),
        slot(special_slots.entitlements),
        slot(special_slots.application),
        slot(special_slots.resource_directory),
        slot(special_slots.requirements),
        slot(special_slots.info_plist),
    ]
    identifier_bytes = identifier.encode("utf-8") + b"\x00"
    team_bytes = team_id.encode("utf-8") + b"\x00" if team_id is not None else b""
    ident_offset = _HEADER_SIZE
    team_offset = _HEADER_SIZE + len(identifier_bytes) if team_id is not None else 0
    hash_offset = _HEADER_SIZE + len(identifier_bytes) + len(team_bytes) + 7 * size

    blob = bytearray()
    blob += u32(CODE_DIRECTORY)
    length_at = len(blob)
    blob += b"\x00\x00\x00\x00"
    blob += u32(0x00020400)
    blob += u32(flags)
    blob += u32(hash_offset)
    blob += u32(ident_offset)
    blob += u32(7)
    blob += u32(len(code_hashes))
    blob += u32(len(code_data))
    blob += bytes([size, hash_type, 0, PAGE_SIZE_LOG2])
    blob += u32(0)
    blob += u32(0)
    blob += u32(team_offset)
    blob += u32(0)
    blob += u64(len(code_data))
    blob += u64(exec_seg_base)
    blob += u64(exec_seg_limit)
    blob += u64(exec_seg_flags)
    if len(blob) != _HEADER_SIZE:
        raise CodeDirectoryError("the CodeDirectory header is no longer 88 bytes")
    blob += identifier_bytes
    blob += team_bytes
    for item in special:
        blob += item
    for item in code_hashes:
        blob += item
    blob[length_at:length_at + 4] = u32(len(blob))
    return bytes(blob)


def parse(blob: bytes) -> ParsedCodeDirectory:
    if len(blob) < _HEADER_SIZE:
        raise CodeDirectoryError("truncated")
    magic = read_u32(blob, 0)
    if magic != CODE_DIRECTORY:
        raise CodeDirectoryError("bad magic")
    length = read_u32(blob, 4)
    if len(blob) < length:
        raise CodeDirectoryError("truncated")
    version = read_u32(blob, 8)
    flags = read_u32(blob, 12)
    hash_offset = read_u32(blob, 16)
    ident_offset = read_u32(blob, 20)
    n_special = read_u32(blob, 24)
    n_code = read_u32(blob, 28)
    code_limit = read_u32(blob, 32)
    size = blob[36]
    hash_type = blob[37]
    team_offset = read_u32(blob, 48)

    def cstring(offset: int) -> str:
        if offset >= len(blob):
            raise CodeDirectoryError("bad string")
        end = blob.find(b"\x00", offset)
        if end < 0:
            raise CodeDirectoryError("bad string")
        return blob[offset:end].decode("utf-8")

    special: list[bytes] = []
    cursor = hash_offset - n_special * size
    for _ in range(n_special):
        if cursor + size > len(blob):
            raise CodeDirectoryError("truncated")
        special.append(blob[cursor:cursor + size])
        cursor += size
    code_hashes: list[bytes] = []
    cursor = hash_offset
    for _ in range(n_code):
        if cursor + size > len(blob):
            raise CodeDirectoryError("truncated")
        code_hashes.append(blob[cursor:cursor + size])
        cursor += size
    return ParsedCodeDirectory(
        magic=magic,
        length=length,
        version=version,
        flags=flags,
        hash_offset=hash_offset,
        ident_offset=ident_offset,
        n_special_slots=n_special,
        n_code_slots=n_code,
        code_limit=code_limit,
        hash_size=size,
        hash_type=hash_type,
        team_offset=team_offset,
        identifier=cstring(ident_offset),
        team_id=None if team_offset == 0 else cstring(team_offset),
        special_slot_hashes=special,
        code_hashes=code_hashes,
    )
