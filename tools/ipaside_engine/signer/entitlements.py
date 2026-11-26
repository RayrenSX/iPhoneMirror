"""Entitlement blobs transcribed from Tetherless ``EntitlementsBuilder.swift``."""

from __future__ import annotations

from . import asn1
from .format import EMBEDDED_DER_ENTITLEMENTS, EMBEDDED_ENTITLEMENTS, u32


def build_plain(xml_plist: bytes) -> bytes:
    return u32(EMBEDDED_ENTITLEMENTS) + u32(len(xml_plist) + 8) + xml_plist


def build_der(entitlements: dict) -> bytes:
    body = asn1.integer_value(1) + encode_value(entitlements)
    wrapped = asn1.tlv(0x70, body)
    return u32(EMBEDDED_DER_ENTITLEMENTS) + u32(len(wrapped) + 8) + wrapped


def encode_value(value) -> bytes:
    if isinstance(value, bool):
        return asn1.boolean(value)
    if isinstance(value, int):
        return asn1.integer_value(value)
    if isinstance(value, str):
        return asn1.utf8_string(value)
    if isinstance(value, list):
        return asn1.sequence([encode_value(item) for item in value])
    if isinstance(value, dict):
        entries = [
            asn1.sequence([asn1.utf8_string(key), encode_value(value[key])])
            for key in sorted(value)
        ]
        return asn1.tlv(0xB0, b"".join(entries))
    return asn1.null()
