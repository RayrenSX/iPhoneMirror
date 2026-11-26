"""Local IPA signing worker. Credentials travel only through JSON on stdin.

Signing/provisioning uses the MIT iPASide engine at the revision in
ipaside_engine/UPSTREAM.json. Installation stays on the host's existing bridge.
"""
from __future__ import annotations

from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import stat
import sys
import uuid
import zipfile

MAX_IPA = 512 * 1024 * 1024
MAX_EXPANDED = 4 * 1024 * 1024 * 1024
MAX_ENTRY = 512 * 1024 * 1024
MAX_REQUEST = 256 * 1024


class SigningFailure(Exception):
    def __init__(self, code, detail=""):
        self.code = code
        super().__init__(detail or code)


def _path(value, suffixes=None, maximum=MAX_IPA):
    if not isinstance(value, str) or not value or len(value) > 32768:
        raise SigningFailure("IPA_INVALID_FILE")
    path = Path(value)
    if not path.is_absolute() or not path.is_file() or not 0 < path.stat().st_size <= maximum:
        raise SigningFailure("IPA_INVALID_FILE")
    if suffixes and path.suffix.lower() not in suffixes:
        raise SigningFailure("IPA_INVALID_FILE")
    return path


def inspect_package(value):
    path = _path(value, {".ipa"})
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        if len(entries) > 100000 or sum(entry.file_size for entry in entries) > MAX_EXPANDED:
            raise SigningFailure("IPA_INVALID_FILE")
        names = set()
        for entry in entries:
            name = entry.filename.replace("\\", "/")
            parts = name.rstrip("/").split("/")
            if (not parts or name.startswith("/") or any(p in ("", "..", ".") or ":" in p or "\0" in p for p in parts)
                    or name.casefold() in names or entry.flag_bits & 1 or entry.file_size > MAX_ENTRY
                    or stat.S_ISLNK(entry.external_attr >> 16)):
                raise SigningFailure("IPA_INVALID_FILE")
            names.add(name.casefold())
        roots = [entry.filename for entry in entries if re.fullmatch(r"Payload/[^/]+\.app/Info\.plist", entry.filename)]
        if len(roots) != 1:
            raise SigningFailure("IPA_INVALID_FILE")
    from ipaside_engine import ipa
    metadata = ipa.inspect(str(path))
    # Icons can be very large and are unnecessary for this installation flow.
    metadata.pop("icon", None)
    if not metadata.get("bundle_id"):
        raise SigningFailure("IPA_INVALID_FILE")
    return metadata


def _load_identity(request):
    from cryptography import x509
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.serialization import pkcs12
    password = request.get("certificatePassword", "")
    if not isinstance(password, str):
        raise SigningFailure("IPA_BAD_CERTIFICATE")
    certificate = _path(request.get("certificate"), maximum=8 * 1024 * 1024).read_bytes()
    try:
        if request["mode"] == "p12":
            key, cert, chain = pkcs12.load_key_and_certificates(certificate, password.encode() if password else None)
        else:
            cert = (x509.load_pem_x509_certificate(certificate) if b"-----BEGIN" in certificate
                    else x509.load_der_x509_certificate(certificate))
            raw = _path(request.get("privateKey"), maximum=8 * 1024 * 1024).read_bytes()
            loader = serialization.load_pem_private_key if b"-----BEGIN" in raw else serialization.load_der_private_key
            key = loader(raw, password.encode() if password else None)
            chain = []
        if key is None or cert is None:
            raise ValueError("Missing key or certificate")
        if key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo) != \
                cert.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo):
            raise ValueError("Certificate and key do not match")
        if not cert.not_valid_before_utc <= datetime.now(timezone.utc) < cert.not_valid_after_utc:
            raise SigningFailure("IPA_CERTIFICATE_EXPIRED")
        return key, cert, chain
    except SigningFailure:
        raise
    except (ValueError, TypeError) as error:
        raise SigningFailure("IPA_BAD_CERTIFICATE") from error


def _check_profile(data, certificate_der, udid, bundle_id):
    from ipaside_engine.signer.profile import read_plist
    profile = read_plist(data)
    expiration = profile.get("ExpirationDate")
    if not isinstance(expiration, datetime) or expiration.replace(tzinfo=timezone.utc) <= datetime.now(timezone.utc):
        raise SigningFailure("IPA_PROFILE_EXPIRED")
    if certificate_der not in profile.get("DeveloperCertificates", []):
        raise SigningFailure("IPA_PROFILE_CERTIFICATE_MISMATCH")
    devices = profile.get("ProvisionedDevices", [])
    if not profile.get("ProvisionsAllDevices") and not any(
            str(item).replace("-", "").lower() == udid.replace("-", "").lower() for item in devices):
        raise SigningFailure("IPA_PROFILE_DEVICE_MISMATCH")
    allowed = profile.get("Entitlements", {}).get("application-identifier", "").partition(".")[2]
    if not allowed or not (bundle_id == allowed or allowed.endswith("*") and bundle_id.startswith(allowed[:-1])):
        raise SigningFailure("IPA_PROFILE_BUNDLE_MISMATCH")
    return profile


def sign_package(request, workspace):
    from cryptography.hazmat.primitives import serialization
    from ipaside_engine import signing, ipa
    metadata = inspect_package(request.get("path"))
    mode = request.get("mode")
    if mode not in ("apple_id", "p12", "pem"):
        raise SigningFailure("IPA_INVALID_MODE")
    udid = request.get("udid")
    if not isinstance(udid, str) or not udid.strip():
        raise SigningFailure("IPA_DEVICE_REQUIRED")
    bundle_id = request.get("bundleId") or metadata["bundle_id"]
    if not isinstance(bundle_id, str) or not re.fullmatch(r"[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", bundle_id):
        raise SigningFailure("IPA_INVALID_BUNDLE_ID")
    remove_extensions = request.get("removeExtensions", False)
    if type(remove_extensions) is not bool:
        raise SigningFailure("IPA_INVALID_REQUEST")
    generated = []
    extension_profiles = {}
    try:
        if mode == "apple_id":
            from ipaside_engine import gsa, provision
            email = request.get("email")
            with gsa.acting_as(email):
                account = gsa.load_session().get("email")
                signing_directory = provision.paths.signing_dir(provision._account_slug(account))
                generated = [signing_directory / "identity.p12", signing_directory / "profile.mobileprovision",
                             signing_directory / "bundle.json"]
                bundle_id = provision.team_scoped_bundle_id(bundle_id)
                extensions = ipa.extension_identities(request["path"]) if not remove_extensions else []
                assets = provision.ensure_signing_assets(bundle_id, udid,
                    app_name=metadata.get("display_name") or "iPhoneMirror App",
                    app_groups=ipa.app_group_identifiers(request["path"]),
                    extension_bundle_ids=[identity for _, identity in extensions],
                    original_bundle_id=metadata["bundle_id"])
            p12_path, profile_path = Path(assets["p12_path"]), Path(assets["profile_path"])
            password = assets["p12_password"]
            extension_profiles = assets.get("extension_profiles", {})
        else:
            key, certificate, chain = _load_identity(request)
            raw_certificate = certificate.public_bytes(serialization.Encoding.DER)
            profiles = request.get("profiles")
            if not isinstance(profiles, list) or not 1 <= len(profiles) <= 64:
                raise SigningFailure("IPA_PROFILE_REQUIRED")
            profile_files = [_path(path, {".mobileprovision", ".provisionprofile"}, 8 * 1024 * 1024) for path in profiles]
            from ipaside_engine.signer.profile import read_plist
            profile_data = [(path, read_plist(path.read_bytes())) for path in profile_files]
            def matches(profile, identifier):
                allowed = profile.get("Entitlements", {}).get("application-identifier", "").partition(".")[2]
                return allowed and (allowed == identifier or allowed.endswith("*") and identifier.startswith(allowed[:-1]))
            def select(identifier):
                candidates = [path for path, profile in profile_data if matches(profile, identifier)]
                if not candidates:
                    raise SigningFailure("IPA_PROFILE_BUNDLE_MISMATCH")
                _check_profile(candidates[0].read_bytes(), raw_certificate, udid, identifier)
                return candidates[0]
            profile_path = select(bundle_id)
            if not remove_extensions:
                for _, identifier in ipa.extension_identities(request["path"]):
                    mapped = bundle_id + identifier[len(metadata["bundle_id"]):] if identifier.startswith(metadata["bundle_id"] + ".") else identifier
                    extension_profiles[identifier] = str(select(mapped))
            from cryptography.hazmat.primitives.serialization import pkcs12
            password = uuid.uuid4().hex
            p12_path = workspace / (uuid.uuid4().hex + ".p12")
            generated.append(p12_path)
            p12_path.write_bytes(pkcs12.serialize_key_and_certificates(b"iPhoneMirror", key, certificate, chain,
                                  serialization.BestAvailableEncryption(password.encode())))
        output = workspace / (uuid.uuid4().hex + ".ipa")
        signing.sign_ipa(request["path"], str(output), p12_path=str(p12_path), p12_password=password,
                         profile_path=str(profile_path), bundle_id=bundle_id, remove_extensions=remove_extensions,
                         remove_watch=remove_extensions, remove_uisd=False, extension_profiles=extension_profiles,
                         temp_folder=str(workspace))
        inspect_package(str(output))
        return {"path": str(output), "bundleId": bundle_id, "status": "signed"}
    finally:
        for path in generated:
            path.unlink(missing_ok=True)


def execute(request, workspace):
    action = request.get("action")
    if action == "inspect":
        return inspect_package(request.get("path"))
    if action == "sign":
        return sign_package(request, workspace)
    if action in ("login", "verify"):
        from ipaside_engine import gsa
        email, password = request.get("email"), request.get("password")
        if not isinstance(email, str) or "@" not in email or not isinstance(password, str) or not password:
            raise SigningFailure("IPA_ACCOUNT_REQUIRED")
        # Never allow request bodies or account tokens into diagnostic files.
        gsa._trace = lambda *_args, **_kwargs: None
        server = request.get("anisetteServer", "").strip()
        if server:
            from urllib.parse import urlparse
            if urlparse(server).scheme != "https" or not urlparse(server).hostname:
                raise SigningFailure("IPA_INVALID_ANISETTE_SERVER")
            os.environ["IPASIDE_ANISETTE_SERVER"] = server
        else:
            os.environ.pop("IPASIDE_ANISETTE_SERVER", None)
        # The optional emulator has a different native runtime; this integration
        # uses the upstream remote protocol and never spawns its local emulator.
        os.environ.pop("IPASIDE_ANISETTE", None)
        if action == "login":
            return gsa.begin_login(email, password)
        code = request.get("code", "").strip()
        if not re.fullmatch(r"\d{6}", code):
            raise SigningFailure("IPA_CODE_REQUIRED")
        return gsa.complete_2fa(email, password, code)
    raise SigningFailure("IPA_INVALID_REQUEST")


def run_worker(workspace):
    workspace = Path(workspace).resolve()
    if not workspace.is_dir():
        return 2
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    while True:
        line = sys.stdin.buffer.readline(MAX_REQUEST + 1)
        if not line:
            return 0
        request = None
        try:
            if len(line) > MAX_REQUEST:
                return 2
            request = json.loads(line)
            if not isinstance(request, dict):
                raise SigningFailure("IPA_INVALID_REQUEST")
            result = execute(request, workspace)
            response = {"success": True, "result": result}
        except Exception as error:
            # No repr(request), traceback, password, certificate key or tokens.
            code = error.code if isinstance(error, SigningFailure) else "IPA_SIGNING_FAILED"
            from ipaside_engine.errors import EngineError
            from ipaside_engine.signer.binary import SignError
            detail = str(error)[:1200] if isinstance(error, (EngineError, SignError)) else ""
            if isinstance(request, dict):
                for field in ("password", "certificatePassword", "code"):
                    secret = request.get(field)
                    if isinstance(secret, str) and secret:
                        detail = detail.replace(secret, "[redacted]")
            response = {"success": False, "code": code, "message": detail}
        print(json.dumps(response, ensure_ascii=True), flush=True)


if __name__ == "__main__":
    raise SystemExit(run_worker(sys.argv[1]))
