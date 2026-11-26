"""Signing-material crypto helpers.

Generates the RSA keypair and certificate request sent to Apple, and signs an
IPA with the certificate Apple returns. The private key never leaves this machine.
"""

from __future__ import annotations

import os
import plistlib
import shutil
import sys
from pathlib import Path
from typing import Any

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import NameOID

from . import paths
from .errors import EngineError

P12_PASSWORD = "iPASide"


class SigningError(EngineError):
    """Raised when an IPA cannot be signed."""


def generate_key_and_csr(common_name: str = "iPASide") -> tuple[bytes, str]:
    """Return (private_key_pem, csr_pem) for a fresh RSA-2048 development key."""
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    csr = (
        x509.CertificateSigningRequestBuilder()
        .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, common_name)]))
        .sign(key, hashes.SHA256())
    )
    key_pem = key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.TraditionalOpenSSL,
        serialization.NoEncryption(),
    )
    csr_pem = csr.public_bytes(serialization.Encoding.PEM).decode()
    return key_pem, csr_pem


def build_p12(cert_der: bytes, key_pem: bytes, password: str = P12_PASSWORD) -> bytes:
    """Bundle a DER certificate and PEM private key into a PKCS#12 blob."""
    key = serialization.load_pem_private_key(key_pem, password=None)
    cert = x509.load_der_x509_certificate(cert_der)
    encryption = (
        serialization.BestAvailableEncryption(password.encode())
        if password
        else serialization.NoEncryption()
    )
    return pkcs12.serialize_key_and_certificates(b"iPASide", key, cert, None, encryption)


def resolve_zsign() -> str:
    """Locate the zsign binary: env override, bundled vendor copy, then PATH."""
    override = os.environ.get("IPASIDE_ZSIGN")
    if override and Path(override).exists():
        return override
    exe = "zsign.exe" if os.name == "nt" else "zsign"
    candidates = [
        paths.resource_dir() / "vendor" / exe,   # bundled next to the package (frozen or source)
        Path(sys.executable).parent / exe,        # alongside a frozen engine exe
    ]
    for candidate in candidates:
        if candidate.exists():
            return str(candidate)
    found = shutil.which("zsign")
    if found:
        return found
    raise SigningError("zsign not found (set IPASIDE_ZSIGN or bundle vendor/zsign.exe)")


#: Injected into LiveContainer so it can import our signing certificate itself.
HELPER_DYLIB = "iPASideCertImport.dylib"


def resolve_helper_dylib() -> str | None:
    """Locate the bundled certificate-import dylib, or None if it is not shipped.

    This is an iOS arm64 Mach-O, so it is built on macOS in CI and committed rather
    than compiled during packaging (see tools/lc-cert-import/). Callers treat a
    missing dylib as "fall back to a manual import" rather than an error, so a build
    that omits it still installs LiveContainer successfully.
    """
    override = os.environ.get("IPASIDE_CERT_IMPORT_DYLIB")
    if override and Path(override).exists():
        return override
    candidates = [
        paths.resource_dir() / "vendor" / HELPER_DYLIB,
        Path(sys.executable).parent / HELPER_DYLIB,
    ]
    for candidate in candidates:
        if candidate.exists():
            return str(candidate)
    return None


def sign_ipa(
    input_ipa: str,
    output_ipa: str,
    *,
    p12_path: str,
    p12_password: str,
    profile_path: str,
    bundle_id: str | None = None,
    display_name: str | None = None,
    bundle_version: str | None = None,
    remove_extensions: bool = True,
    remove_watch: bool = True,
    remove_uisd: bool = True,
    enable_file_sharing: bool = False,
    dylibs: list[str] | None = None,
    weak_dylibs: bool = False,
    inject_into_extensions: bool = False,
    entitlements: str | None = None,
    zip_level: int = 6,
    temp_folder: str | None = None,
    legacy_sha1: bool = False,
    extension_profiles: dict[str, str] | None = None,
    substrate_provider: str = "legacy",
) -> dict[str, Any]:
    """Re-sign an IPA with iPASide's own signer.

    The signature matches Tetherless: a SHA-1 CodeDirectory, a SHA-256 alternate,
    an empty requirements blob, and a CMS signature over the development
    certificate. ``legacy_sha1`` is accepted and ignored because both hashes are
    always written, which is what iOS 10 and current iOS each require.

    ``entitlements`` replaces the profile's entitlements. It exists for apps that
    need entries a profile does not spell out, such as LiveContainer's expanded
    keychain groups. Every entitlement still has to be allowed by the profile.

    ``temp_folder`` is where the IPA is unpacked. It must already exist. Omitted,
    the system temp directory is used. Files this run does not change are copied
    still compressed.
    """
    del legacy_sha1  # both hash types are always embedded
    from .signer.binary import SignError
    from .signer.package import sign_package
    from .signer.profile import ProfileError
    from .signer.wwdr import WWDRError, fetch

    try:
        key, cert, chain = pkcs12.load_key_and_certificates(
            Path(p12_path).read_bytes(),
            p12_password.encode() if p12_password else None,
        )
        if key is None or cert is None:
            raise SigningError("The signing identity has no certificate and private key.")
        certificate_der = cert.public_bytes(serialization.Encoding.DER)
        private_key_der = key.private_bytes(
            serialization.Encoding.DER,
            serialization.PrivateFormat.PKCS8,
            serialization.NoEncryption(),
        )
        wwdr = _wwdr_from_chain(cert, chain) or fetch(certificate_der)
        override = plistlib.loads(Path(entitlements).read_bytes()) if entitlements else None
        if override is not None and not isinstance(override, dict):
            raise SigningError("The entitlements file is not a plist dictionary.")
        profiles = {
            identifier: Path(path).read_bytes()
            for identifier, path in (extension_profiles or {}).items()
        }
        return sign_package(
            input_ipa,
            output_ipa,
            certificate_der=certificate_der,
            private_key_der=private_key_der,
            profile=Path(profile_path).read_bytes(),
            wwdr_der=wwdr,
            bundle_id=bundle_id,
            display_name=display_name,
            bundle_version=bundle_version,
            remove_extensions=remove_extensions,
            remove_watch=remove_watch,
            remove_uisd=remove_uisd,
            enable_file_sharing=enable_file_sharing,
            dylibs=dylibs,
            weak_dylibs=weak_dylibs,
            inject_into_extensions=inject_into_extensions,
            entitlements_override=override,
            extension_profiles=profiles,
            substrate_provider=substrate_provider,
            zip_level=zip_level,
            work_dir=temp_folder,
        )
    except (OSError, SigningError, SignError, WWDRError, ProfileError, ValueError) as exc:
        if isinstance(exc, SigningError):
            raise
        raise SigningError(str(exc)) from exc


def _wwdr_from_chain(leaf: x509.Certificate, chain: list[x509.Certificate] | None) -> bytes | None:
    for extra in chain or []:
        if extra.subject == leaf.issuer and extra.subject != extra.issuer:
            return extra.public_bytes(serialization.Encoding.DER)
    return None
