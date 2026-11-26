"""Requirements blob transcribed from Tetherless ``RequirementsBuilder.swift``.

``SigningController`` uses the empty blob. The full expression is kept because
Tetherless still tests it.
"""

from __future__ import annotations

from .format import REQUIREMENT, REQUIREMENTS, u32

_APPLE_WWDR_OID = bytes([0x2A, 0x86, 0x48, 0x86, 0xF7, 0x63, 0x64, 0x06, 0x02, 0x01])


def build(bundle_id: str, subject_common_name: str) -> bytes:
    expr = bytearray()
    expr += u32(1)
    expr += u32(6)
    expr += u32(2)
    _append_padded(expr, bundle_id.encode("utf-8"))
    expr += u32(6)
    expr += u32(15)
    expr += u32(6)
    expr += u32(11)
    expr += u32(0)
    _append_padded(expr, b"subject.CN")
    expr += u32(1)
    _append_padded(expr, subject_common_name.encode("utf-8"))
    expr += u32(14)
    expr += u32(1)
    _append_padded(expr, _APPLE_WWDR_OID)
    expr += u32(0)

    outer = 20
    inner = 8 + len(expr)
    blob = bytearray()
    blob += u32(REQUIREMENTS)
    blob += u32(outer + inner)
    blob += u32(1)
    blob += u32(3)
    blob += u32(outer)
    blob += u32(REQUIREMENT)
    blob += u32(inner)
    blob += expr
    return bytes(blob)


def build_empty() -> bytes:
    return u32(REQUIREMENTS) + u32(12) + u32(0)


def _append_padded(output: bytearray, data: bytes) -> None:
    output += u32(len(data))
    output += data
    pad = (4 - (len(data) % 4)) % 4
    if pad:
        output += bytes(pad)
