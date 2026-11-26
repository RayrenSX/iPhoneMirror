"""Code-signing constants from Tetherless ``CodeSigningFormat.swift``.

Every multi-byte field in a code-signing blob is big-endian.
"""

from __future__ import annotations

REQUIREMENT = 0xFADE0C00
REQUIREMENTS = 0xFADE0C01
CODE_DIRECTORY = 0xFADE0C02
EMBEDDED_SIGNATURE = 0xFADE0CC0
EMBEDDED_ENTITLEMENTS = 0xFADE7171
EMBEDDED_DER_ENTITLEMENTS = 0xFADE7172
BLOB_WRAPPER = 0xFADE0B01

SLOT_CODE_DIRECTORY = 0
SLOT_INFO = 1
SLOT_REQUIREMENTS = 2
SLOT_RESOURCE_DIR = 3
SLOT_APPLICATION = 4
SLOT_ENTITLEMENTS = 5
SLOT_DER_ENTITLEMENTS = 7
SLOT_ALTERNATE_CODE_DIRECTORY = 0x1000
SLOT_SIGNATURE = 0x10000

HASH_SHA1 = 1
HASH_SHA256 = 2

FLAG_ADHOC = 0x0002


def u32(value: int) -> bytes:
    return int(value & 0xFFFFFFFF).to_bytes(4, "big")


def u64(value: int) -> bytes:
    return int(value & 0xFFFFFFFFFFFFFFFF).to_bytes(8, "big")


def read_u32(data: bytes, offset: int) -> int:
    return int.from_bytes(data[offset:offset + 4], "big")


def read_u64(data: bytes, offset: int) -> int:
    return int.from_bytes(data[offset:offset + 8], "big")
