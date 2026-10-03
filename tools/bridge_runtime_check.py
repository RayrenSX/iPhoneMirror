"""Offline functional probes run inside the frozen bridge during preflight.

No sockets are opened, no device is contacted, and no persistent state is changed.
"""
import ssl

import certifi
import lzfse
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from pymobiledevice3.remote.xpc_message import (
    XpcUInt64Type, XpcWrapper, create_xpc_wrapper, decode_xpc_object,
)
from qh3.quic.configuration import QuicConfiguration
from qh3.quic.connection import QuicConnection


def check_runtime_functionality(build_touchscreen_report, contact, release):
    checks = []
    payload = {'text': 'clipboard 中文 😀', 'id': XpcUInt64Type(42),
               'report': b'\x00\x01\xff', 'values': [True, None, 1.25]}
    wire = create_xpc_wrapper(payload, message_id=7, wanting_reply=True)
    parsed = XpcWrapper.parse(wire)
    if parsed.message.message_id != 7 or decode_xpc_object(parsed.message.payload.obj) != payload:
        raise RuntimeError('XPC roundtrip failed')
    checks.append('xpc_roundtrip')

    for slot in range(5):
        down = build_touchscreen_report(slot, contact, 123, 456, timestamp=1)
        up = build_touchscreen_report(slot, release, 123, 456, timestamp=1)
        if len(down) != 58 or len(up) != 58 or down[3] != (0xC2 | slot) or up[3] != (0x02 | slot):
            raise RuntimeError('HID report serialization failed')
    checks.append('five_slot_hid_reports')

    # QuicConnection builds encrypted packets in memory; it does not own a socket.
    connection = QuicConnection(configuration=QuicConfiguration(is_client=True, alpn_protocols=['h3']))
    connection.connect(('127.0.0.1', 443), now=0)
    packets = connection.datagrams_to_send(now=0)
    if not packets or not all(len(packet) >= 1200 for packet, _ in packets):
        raise RuntimeError('QUIC initial packet encryption failed')
    connection.close()
    checks.append('quic_initial_encryption')

    aes = AESGCM(bytes(range(32)))
    nonce, plaintext = bytes(range(12)), b'offline bridge dependency check' * 20
    if aes.decrypt(nonce, aes.encrypt(nonce, plaintext, b'probe'), b'probe') != plaintext:
        raise RuntimeError('AES-GCM roundtrip failed')
    checks.append('aes_gcm_roundtrip')
    if lzfse.decompress(lzfse.compress(plaintext)) != plaintext:
        raise RuntimeError('LZFSE roundtrip failed')
    checks.append('lzfse_roundtrip')
    if ssl.create_default_context(cafile=certifi.where()).cert_store_stats()['x509_ca'] == 0:
        raise RuntimeError('Bundled CA store is empty')
    checks.append('ca_store')
    return checks
