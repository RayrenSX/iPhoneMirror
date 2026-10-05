"""Offline functional probes run inside the frozen bridge during preflight.

No sockets are opened, no device is contacted, and no persistent state is changed.
"""
import ssl
import struct

import certifi
import lzfse
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from pymobiledevice3.remote.xpc_message import (
    XpcUInt64Type, XpcWrapper, create_xpc_wrapper, decode_xpc_object,
)
from qh3.quic.configuration import QuicConfiguration
from qh3.quic.connection import QuicConnection


def check_runtime_functionality(build_touchscreen_report, contact, release, build_touchscreen_frame):
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
        if (len(down) != 58 or len(up) != 58 or down[3] != (0xC0 | slot) or up[3] != slot
                or down[40 + slot] != slot + 1 or down[45:51] != b'\x01\x00\x00\x00\x00\x00'):
            raise RuntimeError('HID report serialization failed')
    checks.append('five_slot_hid_reports')

    # Exercise the full multi-contact encoder in the packaged executable, not
    # just five separate single-contact reports (which cannot preserve holds).
    for released_slot in (None, 2):
        frame = build_touchscreen_frame([
            (slot, release if slot == released_slot else contact, 100 + slot, 200 + slot)
            for slot in range(5)], timestamp=1)
        if len(frame) != 58 or frame[:3] != bytes((9, 5, 5)):
            raise RuntimeError('Multi-contact HID frame header failed')
        for slot in range(5):
            flags, x, y = struct.unpack_from('<BHH', frame, 3 + slot * 5)
            expected = slot if slot == released_slot else 0xC0 | slot
            if (flags, x, y, frame[40 + slot]) != (expected, 100 + slot, 200 + slot, slot + 1):
                raise RuntimeError('Multi-contact HID hold/release failed')
    checks.append('simultaneous_contacts_independent_release')

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
