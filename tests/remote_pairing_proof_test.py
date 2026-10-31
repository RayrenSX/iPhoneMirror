"""Exercise the SDK pairing exchange with real SRP, including invalid proofs."""
import hashlib
from binascii import unhexlify
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as b
from pymobiledevice3.remote.tunnel_service import (
    PairConsentResult, PairingDataComponentTLVBuf, PairingDataComponentType,
)
from srptools import SRPContext, SRPServerSession
from srptools.constants import PRIME_3072, PRIME_3072_GEN


class RemotePairingProofTests(unittest.IsolatedAsyncioTestCase):
    async def exchange(self, service_type, tamper=False):
        context = SRPContext('Pair-Setup', password='000000', prime=PRIME_3072,
                             generator=PRIME_3072_GEN, hash_func=hashlib.sha512)
        _, verifier, salt = context.get_user_data_triplet()
        server = SRPServerSession(context, password_verifier=verifier)
        if service_type is b.RemotePairingLockdownService:
            service = service_type(SimpleNamespace(reader=None, writer=None), 'test-device')
        else:
            service = service_type('test-device', '192.0.2.1', 1)
        service._init_srp_context(PairConsentResult(bytes.fromhex(server.public), bytes.fromhex(salt), None))
        server.process(service.srp_context.public, salt)
        self.assertEqual(server.key, service.srp_context.key)
        self.assertTrue(server.verify_proof(service.srp_context.key_proof))
        proof = unhexlify(server.key_proof_hash)
        if tamper:
            proof = bytes([proof[0] ^ 1]) + proof[1:]
        reply = PairingDataComponentTLVBuf.build([
            {'type': PairingDataComponentType.STATE, 'data': b'\x04'},
            {'type': PairingDataComponentType.PROOF, 'data': proof},
        ])
        service._send_receive_pairing_data = AsyncMock(return_value=reply)
        # Run the actual SDK encoder/decoder and proof verifier, not a stub.
        await service._verify_proof()
        service._send_receive_pairing_data.assert_awaited_once()

    async def test_valid_proof_passes_for_usb_and_wireless_pairing(self):
        for service_type in (b.RemotePairingLockdownService, b.RemotePairingTunnelService):
            with self.subTest(service=service_type.__name__):
                await self.exchange(service_type)

    async def test_modified_proof_is_rejected_for_both_transports(self):
        for service_type in (b.RemotePairingLockdownService, b.RemotePairingTunnelService):
            with self.subTest(service=service_type.__name__):
                with self.assertRaises(AssertionError):
                    await self.exchange(service_type, tamper=True)

    def test_non_ascii_proof_is_rejected(self):
        context = SimpleNamespace(verify_proof=lambda _: self.fail('Invalid encoding reached SRP'))
        self.assertFalse(b._RemotePairingSrpContext(context).verify_proof(b'\xff'))
