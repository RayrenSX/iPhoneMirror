"""IPA signing integration: real synthetic Mach-O signatures, no Apple/device calls."""
import asyncio
from datetime import datetime, timedelta, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import plistlib
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

_repository = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(_repository / ("src" if (_repository / "src/ipa_signing.py").is_file() else "tools")))
import ipa_signing as signing
from ipaside_engine.signer import asn1, code_directory, macho, superblob
from ipaside_engine.protected_state import ProtectedStatePath
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import NameOID


def thin_macho():
    def u32(n): return struct.pack("<I", n)
    def u64(n): return struct.pack("<Q", n)
    def name(value): return value.encode().ljust(16, b"\0")
    text = u32(0x19) + u32(72) + name("__TEXT") + u64(0) + u64(0x4000) + u64(0) + u64(512)
    text += u32(7) + u32(5) + u32(0) + u32(0)
    link = u32(0x19) + u32(72) + name("__LINKEDIT") + u64(512) + u64(64) + u64(512) + u64(64)
    link += u32(7) + u32(1) + u32(0) + u32(0)
    commands = text + link + u32(0x1D) + u32(16) + u32(512) + u32(64)
    header = u32(0xFEEDFACF) + u32(0x0100000C) + u32(0) + u32(2) + u32(3) + u32(len(commands)) + u32(0) + u32(0)
    return (header + commands).ljust(576, b"\0")


def create_ipa(path, extension=False):
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("Payload/Demo.app/Info.plist", plistlib.dumps({
            "CFBundleIdentifier": "com.example.demo", "CFBundleExecutable": "Demo", "CFBundleName": "Demo"}))
        archive.writestr("Payload/Demo.app/Demo", thin_macho())
        archive.writestr("Payload/Demo.app/Assets.car", b"preserve these bytes")
        if extension:
            archive.writestr("Payload/Demo.app/PlugIns/Widget.appex/Info.plist", plistlib.dumps({
                "CFBundleIdentifier": "com.example.demo.widget", "CFBundleExecutable": "Widget"}))
            archive.writestr("Payload/Demo.app/PlugIns/Widget.appex/Widget", thin_macho())


def create_profile(certificate, **overrides):
    data = {"TeamIdentifier": ["TEAM"], "ExpirationDate": datetime.now(timezone.utc) + timedelta(days=7),
            "DeveloperCertificates": [certificate], "ProvisionedDevices": ["device001"],
            "Entitlements": {"application-identifier": "TEAM.com.example.demo", "get-task-allow": True}}
    data.update(overrides)
    # A CMS envelope is sufficient for the local fixture. iOS ultimately verifies
    # Apple's real profile signature; this test exercises local selection/checks.
    payload = plistlib.dumps(data)
    content = asn1.sequence([asn1.object_identifier("1.2.840.113549.1.7.1"),
                             asn1.explicit_context(0, asn1.octet_string(payload))])
    signed = asn1.sequence([asn1.integer_value(1), asn1.set_of([asn1.null()]), content])
    return asn1.sequence([asn1.object_identifier("1.2.840.113549.1.7.2"), asn1.explicit_context(0, signed)])


class IpaSigningTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "IPA test")])
        self.certificate = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
            .public_key(self.key.public_key()).serial_number(1234)
            .not_valid_before(datetime.now(timezone.utc) - timedelta(minutes=1))
            .not_valid_after(datetime.now(timezone.utc) + timedelta(days=30)).sign(self.key, hashes.SHA256()))
        self.cert = self.certificate.public_bytes(serialization.Encoding.DER)
        self.ipa = self.root / "demo.ipa"
        create_ipa(self.ipa)
        self.profile = self.root / "demo.mobileprovision"
        self.profile.write_bytes(create_profile(self.cert))
        self.identity = self.root / "identity.p12"
        self.identity.write_bytes(pkcs12.serialize_key_and_certificates(b"test", self.key, self.certificate,
            None, serialization.BestAvailableEncryption(b"test-password")))
        self.request = {"action": "sign", "mode": "p12", "udid": "device-001", "path": str(self.ipa),
            "certificate": str(self.identity), "certificatePassword": "test-password", "profiles": [str(self.profile)]}

    def sign(self, **changes):
        with patch("ipaside_engine.signer.wwdr.fetch", return_value=self.cert):
            return signing.execute(dict(self.request, **changes), self.root)

    def test_p12_signs_real_macho_and_keeps_original_ipa(self):
        original = self.ipa.read_bytes()
        result = self.sign()
        with zipfile.ZipFile(result["path"]) as archive:
            blob = archive.read("Payload/Demo.app/Demo")
            self.assertEqual(archive.read("Payload/Demo.app/Assets.car"), b"preserve these bytes")
            self.assertIn("Payload/Demo.app/_CodeSignature/CodeResources", archive.namelist())
        view = macho.parse_slice(blob)
        signature = blob[view.code_limit:view.code_limit + view.existing_signature_size]
        components = [superblob.component_data(signature, item) for item in superblob.parse(signature).entries]
        directories = [code_directory.parse(item) for item in components if item[:4] == b"\xfa\xde\x0c\x02"]
        self.assertEqual([directory.hash_type for directory in directories], [1, 2])
        for directory in directories:
            digest = hashlib.sha1 if directory.hash_type == 1 else hashlib.sha256
            self.assertEqual(directory.code_hashes[0], digest(blob[:view.code_limit]).digest())
        cms_blob = next(item[8:] for item in components if item[:4] == b"\xfa\xde\x0b\x01")
        root = asn1.read_tlv(cms_blob, 0)
        content = asn1.read_children(cms_blob, root.content_range)
        signed = asn1.read_children(cms_blob, asn1.read_children(cms_blob, content[1].content_range)[0].content_range)
        signer_set = signed[-1]
        signer_info = asn1.read_children(cms_blob, asn1.read_children(cms_blob, signer_set.content_range)[0].content_range)
        attributes, rsa_signature = signer_info[3], signer_info[5]
        signed_bytes = bytearray(cms_blob[attributes.full_range.start:attributes.full_range.stop])
        signed_bytes[0] = 0x31
        self.certificate.public_key().verify(cms_blob[rsa_signature.content_range.start:rsa_signature.content_range.stop],
            bytes(signed_bytes), padding.PKCS1v15(), hashes.SHA256())
        self.assertEqual(self.ipa.read_bytes(), original)
        self.assertEqual(list(self.root.glob("*.p12")), [self.identity])

    def test_certificate_and_private_key_signing(self):
        cert, key = self.root / "cert.cer", self.root / "key.pem"
        cert.write_bytes(self.cert)
        key.write_bytes(self.key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                              serialization.BestAvailableEncryption(b"secret")))
        result = self.sign(mode="pem", certificate=str(cert), privateKey=str(key), certificatePassword="secret")
        self.assertTrue(Path(result["path"]).is_file())

    def test_profile_expiry_identity_device_and_bundle_validation(self):
        for values, code in [
            ({"ExpirationDate": datetime.now(timezone.utc) - timedelta(days=1)}, "IPA_PROFILE_EXPIRED"),
            ({"DeveloperCertificates": []}, "IPA_PROFILE_CERTIFICATE_MISMATCH"),
            ({"ProvisionedDevices": ["another-device"]}, "IPA_PROFILE_DEVICE_MISMATCH"),
            ({"ProvisionedDevices": [], "ProvisionsAllDevices": False}, "IPA_PROFILE_DEVICE_MISMATCH"),
            ({"Entitlements": {"application-identifier": "TEAM.com.wrong"}}, "IPA_PROFILE_BUNDLE_MISMATCH"),
        ]:
            self.profile.write_bytes(create_profile(self.cert, **values))
            with self.subTest(code=code), self.assertRaises(signing.SigningFailure) as result:
                self.sign()
            self.assertEqual(result.exception.code, code)
        self.profile.write_bytes(create_profile(self.cert, ProvisionedDevices=[], ProvisionsAllDevices=True))
        self.assertEqual(self.sign()["status"], "signed")
        with self.assertRaises(signing.SigningFailure) as result:
            self.sign(certificatePassword="wrong")
        self.assertEqual(result.exception.code, "IPA_BAD_CERTIFICATE")

    def test_extensions_require_matching_profiles_or_explicit_removal(self):
        create_ipa(self.ipa, extension=True)
        with self.assertRaises(signing.SigningFailure) as result:
            self.sign()
        self.assertEqual(result.exception.code, "IPA_PROFILE_BUNDLE_MISMATCH")
        extension = self.root / "widget.mobileprovision"
        extension.write_bytes(create_profile(self.cert, Entitlements={"application-identifier": "TEAM.com.example.demo.widget"}))
        result = self.sign(profiles=[str(self.profile), str(extension)])
        with zipfile.ZipFile(result["path"]) as archive:
            self.assertIn("Payload/Demo.app/PlugIns/Widget.appex/Widget", archive.namelist())
        result = self.sign(removeExtensions=True)
        with zipfile.ZipFile(result["path"]) as archive:
            self.assertFalse(any(".appex/" in name for name in archive.namelist()))

    def test_invalid_archives_are_rejected_before_signing(self):
        for entry in ["../escape", "Payload/../../escape", "C:/escape"]:
            create_ipa(self.ipa)
            with zipfile.ZipFile(self.ipa, "a") as archive:
                archive.writestr(entry, b"no")
            with self.subTest(entry=entry), self.assertRaises(signing.SigningFailure):
                signing.inspect_package(str(self.ipa))

    @unittest.skipUnless(sys.platform == "win32", "Windows DPAPI")
    def test_cached_sessions_and_keys_are_encrypted_for_windows_user(self):
        path = ProtectedStatePath(self.root) / "account.json"
        path.write_text('{"token":"private-session-value"}', encoding="utf-8")
        self.assertNotIn(b"private-session-value", path.read_bytes())
        self.assertEqual(json.loads(path.read_text(encoding="utf-8"))["token"], "private-session-value")

    def test_apple_id_two_factor_and_provisioning_dispatch_without_credentials_in_output(self):
        from ipaside_engine import gsa
        with patch.object(gsa, "begin_login", return_value={"status": "2fa_required"}) as login:
            self.assertEqual(signing.execute({"action": "login", "email": "test@example.com", "password": "never-print"}, self.root),
                             {"status": "2fa_required"})
            login.assert_called_once_with("test@example.com", "never-print")
        with patch.object(gsa, "complete_2fa", return_value={"status": "authenticated"}) as verify:
            signing.execute({"action": "verify", "email": "test@example.com", "password": "never-print", "code": "123456"}, self.root)
            verify.assert_called_once_with("test@example.com", "never-print", "123456")

    def test_worker_accepts_persistent_json_requests_and_does_not_print_password(self):
        requests = [{"action": "inspect", "path": str(self.ipa)},
                    {"action": "sign", "mode": "bad", "path": str(self.ipa), "password": "never-print"}]
        worker = Path(signing.__file__)
        result = subprocess.run([sys.executable, str(worker), str(self.root)],
            input="".join(json.dumps(item) + "\n" for item in requests), text=True, capture_output=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)
        messages = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertTrue(messages[0]["success"])
        self.assertEqual(messages[1]["code"], "IPA_INVALID_MODE")
        self.assertNotIn("never-print", result.stdout + result.stderr)

    @unittest.skipUnless(os.environ.get("IUSBBRIDGE_SIGNING_TEST_EXE"), "Packaged bridge opt-in")
    def test_packaged_bridge_checks_and_signs_p12_without_python_installation(self):
        ca_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Offline test intermediate")])
        root_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Offline test root")])
        now = datetime.now(timezone.utc)
        intermediate = (x509.CertificateBuilder().subject_name(ca_name).issuer_name(root_name)
            .public_key(ca_key.public_key()).serial_number(90).not_valid_before(now - timedelta(days=1))
            .not_valid_after(now + timedelta(days=30)).sign(ca_key, hashes.SHA256()))
        leaf_name = self.certificate.subject
        leaf = (x509.CertificateBuilder().subject_name(leaf_name).issuer_name(ca_name)
            .public_key(self.key.public_key()).serial_number(91).not_valid_before(now - timedelta(days=1))
            .not_valid_after(now + timedelta(days=7)).sign(ca_key, hashes.SHA256()))
        self.identity.write_bytes(pkcs12.serialize_key_and_certificates(b"test", self.key, leaf, [intermediate],
            serialization.BestAvailableEncryption(b"test-password")))
        self.profile.write_bytes(create_profile(leaf.public_bytes(serialization.Encoding.DER)))
        payload = [{"action": "inspect", "path": str(self.ipa)}, self.request]
        result = subprocess.run([os.environ["IUSBBRIDGE_SIGNING_TEST_EXE"], "--ipa-signing", "--ipa-workspace", str(self.root)],
            input="".join(json.dumps(item) + "\n" for item in payload), text=True, capture_output=True, timeout=40)
        self.assertEqual(result.returncode, 0, result.stderr)
        messages = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(len(messages), 2, result.stdout)
        self.assertTrue(messages[0]["success"], messages[0])
        self.assertTrue(messages[1]["success"], messages[1])
        self.assertTrue(Path(messages[1]["result"]["path"]).is_file())
        self.assertNotIn("test-password", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
