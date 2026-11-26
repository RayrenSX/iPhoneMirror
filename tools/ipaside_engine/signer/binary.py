"""Sign one Mach-O the way Tetherless's ``signOneThinSlice`` does.

The signature is built twice. The first pass only learns how big the blob is,
because that size is written into the Mach-O header and the header is part of
what the CodeDirectory hashes. The second pass hashes the patched header and
must come out the same size.
"""

from __future__ import annotations

import hashlib
import plistlib

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import padding
from cryptography.hazmat.primitives.serialization import load_der_private_key
from cryptography.x509 import load_der_x509_certificate

from . import cms, code_directory, entitlements, macho, requirements, superblob, x509_reader
from .apple_root import APPLE_ROOT_CA
from .format import (
    BLOB_WRAPPER,
    HASH_SHA1,
    HASH_SHA256,
    SLOT_ALTERNATE_CODE_DIRECTORY,
    SLOT_CODE_DIRECTORY,
    SLOT_DER_ENTITLEMENTS,
    SLOT_ENTITLEMENTS,
    SLOT_REQUIREMENTS,
    SLOT_SIGNATURE,
    u32,
)

_EXEC_MAIN = 0x1
_EXEC_ALLOW_UNSIGNED = 0x10


class SignError(Exception):
    pass


def sign_macho(
    executable: bytes,
    identifier: str,
    team_id: str,
    certificate_der: bytes,
    private_key_der: bytes,
    wwdr_der: bytes,
    *,
    info_plist: bytes | None = None,
    entitlement_values: dict | None = None,
    resource_hashes: tuple[bytes, bytes] | None = None,
    is_main_executable: bool | None = None,
) -> bytes:
    arches = macho.fat_architectures(executable)
    if not arches:
        return _sign_thin(
            executable, identifier, team_id, certificate_der, private_key_der, wwdr_der,
            info_plist=info_plist, entitlement_values=entitlement_values,
            resource_hashes=resource_hashes, is_main_executable=is_main_executable,
        )
    signed = []
    for arch in arches:
        piece = _sign_thin(
            executable[arch.offset:arch.offset + arch.size],
            identifier, team_id, certificate_der, private_key_der, wwdr_der,
            info_plist=info_plist, entitlement_values=entitlement_values,
            resource_hashes=resource_hashes, is_main_executable=is_main_executable,
        )
        signed.append((arch.cpu_type, arch.cpu_subtype, arch.align, piece))
    return macho.build_fat(signed)


def _sign_thin(
    executable: bytes,
    identifier: str,
    team_id: str,
    certificate_der: bytes,
    private_key_der: bytes,
    wwdr_der: bytes,
    *,
    info_plist: bytes | None,
    entitlement_values: dict | None,
    resource_hashes: tuple[bytes, bytes] | None,
    is_main_executable: bool | None,
) -> bytes:
    executable = macho.ensure_signature_slot(executable)
    info = macho.parse_slice(executable)
    fields = x509_reader.read(certificate_der)
    requirements_blob = requirements.build_empty()
    plain = der = None
    if entitlement_values is not None:
        xml = plistlib.dumps(entitlement_values, fmt=plistlib.FMT_XML)
        plain = entitlements.build_plain(xml)
        der = entitlements.build_der(entitlement_values)

    sha1_slots = code_directory.SpecialSlots()
    sha256_slots = code_directory.SpecialSlots()
    if info_plist is not None:
        sha1_slots.info_plist = hashlib.sha1(info_plist).digest()
        sha256_slots.info_plist = hashlib.sha256(info_plist).digest()
    sha1_slots.requirements = hashlib.sha1(requirements_blob).digest()
    sha256_slots.requirements = hashlib.sha256(requirements_blob).digest()
    if resource_hashes is not None:
        sha1_slots.resource_directory, sha256_slots.resource_directory = resource_hashes
    if plain is not None:
        sha1_slots.entitlements = hashlib.sha1(plain).digest()
        sha256_slots.entitlements = hashlib.sha256(plain).digest()
    if der is not None:
        sha1_slots.der_entitlements = hashlib.sha1(der).digest()
        sha256_slots.der_entitlements = hashlib.sha256(der).digest()

    main = info.is_main_executable if is_main_executable is None else is_main_executable
    flags = _EXEC_MAIN if main else 0
    if entitlement_values is not None and entitlement_values.get("get-task-allow") is True:
        flags |= _EXEC_ALLOW_UNSIGNED

    def assemble(signature: bytes) -> bytes:
        primary_len = code_directory.blob_size(identifier, team_id, len(code), HASH_SHA1)
        alternate_len = code_directory.blob_size(identifier, team_id, len(code), HASH_SHA256)
        # The CMS size depends on the signature's length, not its bytes. A stand-in of
        # that length gives the final superblob size before any page is hashed.
        cd_hashes = cms.cd_hashes_plist([bytes(20), bytes(20)])
        data_to_sign, implicit = cms.prepare_signing(b"", cd_hashes, bytes(32))
        cms_der = cms.assemble(
            [certificate_der, wwdr_der, APPLE_ROOT_CA],
            fields.issuer_full_der,
            fields.serial_number_content,
            implicit,
            signature,
        )
        wrapper = u32(BLOB_WRAPPER) + u32(8 + len(cms_der)) + cms_der
        components = [
            superblob.Component(SLOT_CODE_DIRECTORY, bytes(primary_len)),
            superblob.Component(SLOT_REQUIREMENTS, requirements_blob),
        ]
        if plain is not None:
            components.append(superblob.Component(SLOT_ENTITLEMENTS, plain))
        if der is not None:
            components.append(superblob.Component(SLOT_DER_ENTITLEMENTS, der))
        components.append(superblob.Component(SLOT_ALTERNATE_CODE_DIRECTORY, bytes(alternate_len)))
        components.append(superblob.Component(SLOT_SIGNATURE, wrapper))
        return superblob.build(components)

    def superblob_for(code: bytes) -> bytes:
        primary = code_directory.build(
            identifier, team_id, code, sha1_slots, HASH_SHA1,
            exec_seg_base=info.exec_seg_base, exec_seg_limit=info.exec_seg_limit, exec_seg_flags=flags,
        )
        alternate = code_directory.build(
            identifier, team_id, code, sha256_slots, HASH_SHA256,
            exec_seg_base=info.exec_seg_base, exec_seg_limit=info.exec_seg_limit, exec_seg_flags=flags,
        )
        cd_hashes = cms.cd_hashes_plist([
            hashlib.sha1(primary).digest(),
            hashlib.sha256(alternate).digest()[:20],
        ])
        data_to_sign, implicit = cms.prepare_signing(primary, cd_hashes, hashlib.sha256(alternate).digest())
        signature = _sign_and_check(private_key_der, certificate_der, data_to_sign, identifier)
        cms_der = cms.assemble(
            [certificate_der, wwdr_der, APPLE_ROOT_CA],
            fields.issuer_full_der,
            fields.serial_number_content,
            implicit,
            signature,
        )
        wrapper = u32(BLOB_WRAPPER) + u32(8 + len(cms_der)) + cms_der
        components = [
            superblob.Component(SLOT_CODE_DIRECTORY, primary),
            superblob.Component(SLOT_REQUIREMENTS, requirements_blob),
        ]
        if plain is not None:
            components.append(superblob.Component(SLOT_ENTITLEMENTS, plain))
        if der is not None:
            components.append(superblob.Component(SLOT_DER_ENTITLEMENTS, der))
        components.append(superblob.Component(SLOT_ALTERNATE_CODE_DIRECTORY, alternate))
        components.append(superblob.Component(SLOT_SIGNATURE, wrapper))
        return superblob.build(components)

    code = macho.code_data(executable, info)
    key = load_der_private_key(private_key_der, password=None)
    measured = assemble(bytes(key.key_size // 8))
    patched = macho.replace_signature(executable, info, measured)
    final = superblob_for(patched[:info.code_limit])
    if len(final) != len(measured):
        raise SignError(f"signature size changed between passes for {identifier}")
    return macho.replace_signature(executable, info, final)


def _sign_and_check(private_key_der: bytes, certificate_der: bytes, data: bytes, identifier: str) -> bytes:
    key = load_der_private_key(private_key_der, password=None)
    signature = key.sign(data, padding.PKCS1v15(), hashes.SHA256())
    public = load_der_x509_certificate(certificate_der).public_key()
    try:
        public.verify(signature, data, padding.PKCS1v15(), hashes.SHA256())
    except Exception as exc:
        raise SignError(f"the signing key does not match the certificate for {identifier}") from exc
    return signature
