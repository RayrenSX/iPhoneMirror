"""CMS SignedData assembly from Tetherless ``CMSSignatureBuilder.swift``.

The RSA signature itself is supplied by the caller. Python's ``cryptography``
package produces the same PKCS#1 v1.5 SHA-256 signature ``SecKeyCreateSignature``
does on iOS.
"""

from __future__ import annotations

import hashlib
import plistlib

from . import asn1


def cd_hashes_plist(entries: list[bytes]) -> bytes:
    return plistlib.dumps({"cdhashes": entries}, fmt=plistlib.FMT_XML)


def _attribute(oid: str, values: list[bytes]) -> bytes:
    return asn1.sequence([asn1.object_identifier(oid), asn1.set_of(values)])


def _algorithm(oid: str) -> bytes:
    return asn1.sequence([asn1.object_identifier(oid), asn1.null()])


def signed_attributes_content(
    primary_code_directory: bytes,
    cd_hashes: bytes,
    cd_hashes2_digest: bytes,
) -> bytes:
    content_type = _attribute("1.2.840.113549.1.9.3", [asn1.object_identifier("1.2.840.113549.1.7.1")])
    digest = hashlib.sha256(primary_code_directory).digest()
    message_digest = _attribute("1.2.840.113549.1.9.4", [asn1.octet_string(digest)])
    hashes_plist = _attribute("1.2.840.113635.100.9.1", [asn1.octet_string(cd_hashes)])
    hashes2_value = asn1.sequence([
        asn1.object_identifier("2.16.840.1.101.3.4.2.1"),
        asn1.octet_string(cd_hashes2_digest),
    ])
    hashes2 = _attribute("1.2.840.113635.100.9.2", [hashes2_value])
    return asn1.canonical_set_content([content_type, message_digest, hashes_plist, hashes2])


def prepare_signing(
    primary_code_directory: bytes,
    cd_hashes: bytes,
    cd_hashes2_digest: bytes,
) -> tuple[bytes, bytes]:
    content = signed_attributes_content(primary_code_directory, cd_hashes, cd_hashes2_digest)
    return asn1.tlv(0x31, content), asn1.tlv(0xA0, content)


def assemble(
    certificates_der: list[bytes],
    issuer_full_der: bytes,
    serial_number_content: bytes,
    signed_attributes_implicit: bytes,
    signature: bytes,
) -> bytes:
    issuer_and_serial = asn1.sequence([
        issuer_full_der,
        asn1.integer_unsigned(serial_number_content),
    ])
    signer_info = asn1.sequence([
        asn1.integer_value(1),
        issuer_and_serial,
        _algorithm("2.16.840.1.101.3.4.2.1"),
        signed_attributes_implicit,
        _algorithm("1.2.840.113549.1.1.1"),
        asn1.octet_string(signature),
    ])
    encap = asn1.sequence([asn1.object_identifier("1.2.840.113549.1.7.1")])
    certificates = asn1.implicit_context(0, b"".join(certificates_der), constructed=True)
    signed_data = asn1.sequence([
        asn1.integer_value(1),
        asn1.set_of([_algorithm("2.16.840.1.101.3.4.2.1")]),
        encap,
        certificates,
        asn1.set_of([signer_info]),
    ])
    return asn1.sequence([
        asn1.object_identifier("1.2.840.113549.1.7.2"),
        asn1.explicit_context(0, signed_data),
    ])


def rsa_sha256_sign(private_key_der: bytes, data_to_sign: bytes) -> bytes:
    """RSA PKCS#1 v1.5 SHA-256, the operation ``SecKeyCreateSignature`` performs."""
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import padding

    key = serialization.load_der_private_key(private_key_der, password=None)
    return key.sign(data_to_sign, padding.PKCS1v15(), hashes.SHA256())
