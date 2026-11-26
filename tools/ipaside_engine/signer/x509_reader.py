"""The X.509 fields a CMS signature needs, from Tetherless ``X509CertificateReader.swift``."""

from __future__ import annotations

from dataclasses import dataclass

from .asn1 import ASN1Error, read_children, read_tlv


class CertificateError(Exception):
    pass


@dataclass
class CertificateFields:
    serial_number_content: bytes
    issuer_full_der: bytes
    subject_common_name: str | None
    ca_issuer_url: str | None


_COMMON_NAME = bytes([0x55, 0x04, 0x03])
_AIA = bytes([0x2B, 0x06, 0x01, 0x05, 0x05, 0x07, 0x01, 0x01])
_CA_ISSUERS = bytes([0x2B, 0x06, 0x01, 0x05, 0x05, 0x07, 0x30, 0x02])


def read(certificate_der: bytes) -> CertificateFields:
    try:
        certificate = read_tlv(certificate_der, 0)
        children = read_children(certificate_der, certificate.content_range)
        if not children:
            raise CertificateError("malformed")
        fields = read_children(certificate_der, children[0].content_range)
        if not fields:
            raise CertificateError("malformed")
        if fields[0].tag == 0xA0:
            fields = fields[1:]
        if len(fields) < 6:
            raise CertificateError("malformed")
        serial = certificate_der[fields[0].content_range.start:fields[0].content_range.stop]
        issuer = certificate_der[fields[2].full_range.start:fields[2].full_range.stop]
        subject = fields[4]
        name = _common_name(certificate_der, subject.content_range)
        url = None
        for item in fields:
            if item.tag == 0xA3:
                url = _ca_issuer_url(certificate_der, item)
                break
        return CertificateFields(serial, issuer, name, url)
    except ASN1Error as exc:
        raise CertificateError(str(exc)) from exc


def _common_name(data: bytes, span: range) -> str | None:
    for rdn in read_children(data, span):
        for pair in read_children(data, rdn.content_range):
            parts = read_children(data, pair.content_range)
            if len(parts) != 2 or parts[0].tag != 0x06:
                continue
            if data[parts[0].content_range.start:parts[0].content_range.stop] != _COMMON_NAME:
                continue
            return data[parts[1].content_range.start:parts[1].content_range.stop].decode("utf-8")
    return None


def _ca_issuer_url(data: bytes, extensions_tlv) -> str | None:
    explicit = read_children(data, extensions_tlv.content_range)
    if not explicit:
        return None
    extensions = read_children(data, explicit[0].content_range)
    for ext in extensions:
        parts = read_children(data, ext.content_range)
        if not parts or parts[0].tag != 0x06:
            continue
        if data[parts[0].content_range.start:parts[0].content_range.stop] != _AIA:
            continue
        value = next((part for part in parts if part.tag == 0x04), None)
        if value is None:
            continue
        ext_bytes = data[value.content_range.start:value.content_range.stop]
        syntax = read_tlv(ext_bytes, 0)
        for description in read_children(ext_bytes, syntax.content_range):
            ad_parts = read_children(ext_bytes, description.content_range)
            if len(ad_parts) != 2 or ad_parts[0].tag != 0x06:
                continue
            if ext_bytes[ad_parts[0].content_range.start:ad_parts[0].content_range.stop] != _CA_ISSUERS:
                continue
            if ad_parts[1].tag != 0x86:
                continue
            return ext_bytes[ad_parts[1].content_range.start:ad_parts[1].content_range.stop].decode("utf-8")
    return None
