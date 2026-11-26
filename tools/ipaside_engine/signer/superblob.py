"""The embedded-signature superblob, from Tetherless ``CodeSignatureSuperBlobBuilder.swift``."""

from __future__ import annotations

from dataclasses import dataclass

from .format import EMBEDDED_SIGNATURE, read_u32, u32


@dataclass
class Component:
    slot: int
    data: bytes


@dataclass
class IndexEntry:
    slot: int
    offset: int


@dataclass
class Parsed:
    magic: int
    length: int
    entries: list[IndexEntry]


class SuperBlobError(Exception):
    pass


def build(components: list[Component]) -> bytes:
    header_size = 12 + len(components) * 8
    index = bytearray()
    body = bytearray()
    offset = header_size
    for component in components:
        index += u32(component.slot)
        index += u32(offset)
        body += component.data
        offset += len(component.data)
    blob = bytearray()
    blob += u32(EMBEDDED_SIGNATURE)
    blob += u32(header_size + len(body))
    blob += u32(len(components))
    blob += index
    blob += body
    return bytes(blob)


def parse(blob: bytes) -> Parsed:
    if len(blob) < 12:
        raise SuperBlobError("truncated")
    magic = read_u32(blob, 0)
    if magic != EMBEDDED_SIGNATURE:
        raise SuperBlobError("bad magic")
    length = read_u32(blob, 4)
    count = read_u32(blob, 8)
    if len(blob) < 12 + count * 8 or len(blob) < length:
        raise SuperBlobError("truncated")
    entries: list[IndexEntry] = []
    cursor = 12
    for _ in range(count):
        entries.append(IndexEntry(read_u32(blob, cursor), read_u32(blob, cursor + 4)))
        cursor += 8
    return Parsed(magic, length, entries)


def component_data(blob: bytes, entry: IndexEntry) -> bytes:
    start = entry.offset
    if start + 8 > len(blob):
        raise SuperBlobError("truncated")
    size = read_u32(blob, start + 4)
    if start + size > len(blob):
        raise SuperBlobError("truncated")
    return blob[start:start + size]
