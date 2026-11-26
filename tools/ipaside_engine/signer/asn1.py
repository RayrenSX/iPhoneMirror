"""DER helpers transcribed from Tetherless ``ASN1.swift``."""

from functools import cmp_to_key


class ASN1Error(Exception):
    pass


def length(n: int) -> bytes:
    if n < 0x80:
        return bytes([n])
    raw = n.to_bytes((n.bit_length() + 7) // 8, "big")
    return bytes([0x80 | len(raw)]) + raw


def tlv(tag: int, content: bytes) -> bytes:
    return bytes([tag]) + length(len(content)) + content


def sequence(elements: list[bytes]) -> bytes:
    return tlv(0x30, b"".join(elements))


def canonical_set_content(elements: list[bytes]) -> bytes:
    return b"".join(sorted(elements, key=cmp_to_key(_der_cmp)))


def set_of(elements: list[bytes]) -> bytes:
    return tlv(0x31, canonical_set_content(elements))


def explicit_context(number: int, content: bytes) -> bytes:
    return tlv(0xA0 | number, content)


def implicit_context(number: int, content: bytes, *, constructed: bool) -> bytes:
    tag = (0xA0 if constructed else 0x80) | number
    return tlv(tag, content)


def integer_unsigned(magnitude: bytes) -> bytes:
    trimmed = bytearray(magnitude)
    while len(trimmed) > 1 and trimmed[0] == 0 and (trimmed[1] & 0x80) == 0:
        del trimmed[0]
    if not trimmed:
        trimmed = bytearray([0])
    if trimmed[0] & 0x80:
        trimmed.insert(0, 0)
    return tlv(0x02, bytes(trimmed))


def integer_value(value: int) -> bytes:
    if value < 0:
        raise ValueError("only non-negative integers are encoded here")
    raw = bytearray()
    magnitude = value
    while True:
        raw.insert(0, magnitude & 0xFF)
        magnitude >>= 8
        if magnitude == 0:
            break
    return integer_unsigned(bytes(raw))


def boolean(value: bool) -> bytes:
    return tlv(0x01, bytes([0xFF if value else 0x00]))


def bit_string(data: bytes) -> bytes:
    return tlv(0x03, bytes([0x00]) + data)


def null() -> bytes:
    return tlv(0x05, b"")


def octet_string(data: bytes) -> bytes:
    return tlv(0x04, data)


def utf8_string(text: str) -> bytes:
    return tlv(0x0C, text.encode("utf-8"))


def object_identifier(dotted: str) -> bytes:
    arcs = [int(part) for part in dotted.split(".")]
    if len(arcs) < 2:
        raise ValueError("an OID needs at least two arcs")
    content = bytearray([arcs[0] * 40 + arcs[1]])
    for arc in arcs[2:]:
        groups = [arc & 0x7F]
        remaining = arc >> 7
        while remaining:
            groups.insert(0, 0x80 | (remaining & 0x7F))
            remaining >>= 7
        content.extend(groups)
    return tlv(0x06, bytes(content))


class TLV:
    def __init__(self, tag: int, content: range, full: range) -> None:
        self.tag = tag
        self.content_range = content
        self.full_range = full


def read_tlv(data: bytes, offset: int) -> TLV:
    if offset >= len(data):
        raise ASN1Error("truncated")
    tag = data[offset]
    if offset + 1 >= len(data):
        raise ASN1Error("truncated")
    first = data[offset + 1]
    content_start = offset + 2
    if first & 0x80 == 0:
        size = first
    else:
        count = first & 0x7F
        if count == 0 or count > 4:
            raise ASN1Error("bad length")
        if content_start + count > len(data):
            raise ASN1Error("truncated")
        size = 0
        for index in range(count):
            size = (size << 8) | data[content_start + index]
        content_start += count
    content_end = content_start + size
    if content_end > len(data):
        raise ASN1Error("truncated")
    return TLV(tag, range(content_start, content_end), range(offset, content_end))


def read_children(data: bytes, span: range) -> list[TLV]:
    children: list[TLV] = []
    offset = span.start
    while offset < span.stop:
        item = read_tlv(data, offset)
        children.append(item)
        offset = item.full_range.stop
    return children


def _der_cmp(left: bytes, right: bytes) -> int:
    """X.690 SET OF order. Equal encodings compare equal so the sort stays stable."""
    for index in range(max(len(left), len(right))):
        byte_a = left[index] if index < len(left) else 0
        byte_b = right[index] if index < len(right) else 0
        if byte_a != byte_b:
            return -1 if byte_a < byte_b else 1
    return 0
