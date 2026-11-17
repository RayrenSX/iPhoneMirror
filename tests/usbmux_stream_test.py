"""Corrupt/retransmitted mux input must not poison recovery or TLS streams."""
import struct
import sys
import threading
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from iostouch.qt import usbmux_usb as m


class StreamTests(unittest.TestCase):
    def setUp(self):
        self.writes = []
        self.mux = m.MuxDevice(self.writes.append)
        self.mux.version = 2
        self.mux.ready.set()

    def connection(self, port=1, seq=100):
        conn = m.MuxConnection(self.mux, port, 62078)
        conn.state = 'connected'
        conn.tx_ack = seq
        self.mux._conns[port] = conn
        return conn

    def packet(self, payload=b'data', seq=100, port=1, flags=m.TH_ACK):
        packet = bytearray(m.build_mux_packet(2, m.PROTO_TCP,
                                  m.build_tcp(62078, port, seq, 0, flags, 65536, payload)))
        struct.pack_into('!I', packet, 8, m.MUX_DEVICE_MAGIC)
        return bytes(packet)

    def test_duplicate_and_overlapping_payload_never_duplicate_tls_bytes(self):
        conn = self.connection()
        self.mux.feed(self.packet(b'ABCD'))
        self.mux.feed(self.packet(b'ABCD'))
        self.mux.feed(self.packet(b'CDEF', seq=102))
        self.assertEqual(b'ABCDEF', conn.recv(timeout=0))
        self.assertEqual(6, conn.bytes_rx)
        self.assertEqual(106, conn.tx_ack)
        self.assertFalse(conn.closed)
        self.assertEqual(3, len(self.writes))

    def test_maximum_mux_packet_reassembles_from_bounded_usb_reads(self):
        conn = self.connection()
        payload = (bytes(range(256)) * 256)[:m.DEV_MRU - 36]
        packet = self.packet(payload)
        self.assertEqual(m.DEV_MRU, len(packet))
        for offset in range(0, len(packet), m.USB_MRU):
            self.mux.feed(packet[offset:offset + m.USB_MRU])
        self.assertEqual(payload, conn.recv(timeout=0))
        self.assertFalse(conn.closed)

    def test_payload_and_fin_wrap_32_bit_sequence(self):
        conn = self.connection(seq=0xfffffffe)
        self.mux.feed(self.packet(b'ABCD', seq=0xfffffffe))
        self.mux.feed(self.packet(b'CD', seq=0))
        self.mux.feed(self.packet(b'EF', seq=2, flags=m.TH_ACK | m.TH_FIN))
        self.assertEqual(b'ABCDEF', conn.recv(timeout=0))
        self.assertEqual(5, conn.tx_ack)
        self.assertTrue(conn.closed)

    def test_old_fin_does_not_close_newer_stream(self):
        conn = self.connection()
        self.mux.feed(self.packet(b'ABCD'))
        self.mux.feed(self.packet(b'AB', flags=m.TH_ACK | m.TH_FIN))
        self.assertFalse(conn.closed)
        self.assertEqual(104, conn.tx_ack)

    def test_receive_gap_timeout_retires_only_affected_tcp_stream(self):
        bad = self.connection()
        good = self.connection(port=2)
        self.mux.feed(self.packet(b'ABCD', seq=102))
        self.mux.feed(self.packet(b'good', port=2))
        self.assertFalse(bad.closed)
        self.assertEqual(b'', bad.recv(timeout=0))
        with patch.object(m.time, 'monotonic', return_value=bad._gap_started + m.RECEIVE_GAP_TIMEOUT + .1):
            self.mux.expire_receive_gaps()
        self.assertTrue(bad.closed)
        self.assertIn('receive gap', bad.close_reason)
        self.assertEqual(b'', bad.recv(timeout=0))
        self.assertEqual(b'good', good.recv(timeout=0))
        self.assertIsNone(self.mux._failure_reason)

    def test_out_of_order_and_overlapping_segments_reassemble_without_disconnect(self):
        conn = self.connection()
        self.mux.feed(self.packet(b'EF', seq=104))
        self.mux.feed(self.packet(b'CDE', seq=102))
        self.mux.feed(self.packet(b'ABCD', seq=100))
        self.assertEqual(b'ABCDEF', conn.recv(timeout=0))
        self.assertEqual(106, conn.tx_ack)
        self.assertFalse(conn.closed)
        self.assertEqual({}, conn._pending)
        self.assertIsNone(conn._gap_started)

    def test_out_of_order_fin_waits_for_preceding_bytes(self):
        conn = self.connection()
        self.mux.feed(self.packet(b'EF', seq=104, flags=m.TH_ACK | m.TH_FIN))
        self.assertFalse(conn.closed)
        self.mux.feed(self.packet(b'ABCD', seq=100))
        self.assertEqual(b'ABCDEF', conn.recv(timeout=0))
        self.assertTrue(conn.closed)
        self.assertEqual(107, conn.tx_ack)

    def test_reorder_buffer_is_bounded_and_duplicate_future_segments_share_space(self):
        conn = self.connection()
        for _ in range(100): self.mux.feed(self.packet(b'X', seq=102))
        self.assertEqual(1, len(conn._pending))
        for seq in range(103, 168): self.mux.feed(self.packet(b'X', seq=seq))
        self.assertTrue(conn.closed)
        self.assertEqual({}, conn._pending)
        self.assertIn('window exceeded', conn.close_reason)

    def test_invalid_tcp_offsets_are_rejected(self):
        for offset in (0, 4, 6, 15):
            data = bytearray(m.build_tcp(1, 2, 0, 0, m.TH_ACK, 65536))
            data[12] = offset << 4
            with self.subTest(offset=offset), self.assertRaises(m.MuxError):
                m.parse_tcp(data)
        data = bytearray(m.build_tcp(1, 2, 0, 0, m.TH_ACK, 65536))
        data[12] = 6 << 4
        self.assertEqual(b'payload', m.parse_tcp(bytes(data) + b'opts' + b'payload').payload)

    def test_poisoned_length_is_removed_and_fresh_tcp_stream_can_connect(self):
        old = self.connection()
        old._inbox += b'old TLS fragment'
        # Same length that recurred in the user's failed automatic retries.
        poison = struct.pack('!II', m.PROTO_TCP, 2426763216) + b'not a header'
        self.mux.feed(poison)
        self.assertTrue(old.closed)
        self.assertEqual(b'', old.recv(timeout=0))
        self.assertIsNone(self.mux._failure_reason)
        self.assertTrue(self.mux.ready.is_set())
        # Connect through the same mux/USB writer, without VERSION/SETUP/reset.
        def write(packet):
            self.writes.append(packet)
            if packet and struct.unpack_from('!I', packet)[0] == m.PROTO_TCP:
                seg = m.parse_tcp(packet[16:])
                if seg.flags == m.TH_SYN:
                    self.mux.feed(m.build_mux_packet(2, m.PROTO_TCP,
                        m.build_tcp(seg.dport, seg.sport, 99, 1, m.TH_SYN | m.TH_ACK, 65536)))
        self.mux._write_raw = write
        fresh = self.mux.connect(62078, timeout=.1)
        self.mux.feed(self.packet(b'fresh', seq=100, port=fresh.sport))
        self.assertEqual(b'fresh', fresh.recv(timeout=0))
        self.assertTrue(all(struct.unpack_from('!I', p)[0] == m.PROTO_TCP for p in self.writes if p))

    def test_resync_preserves_split_header_at_every_boundary(self):
        packet = bytearray(m.build_mux_packet(2, m.PROTO_CONTROL, b'healthy'))
        struct.pack_into('!I', packet, 8, m.MUX_DEVICE_MAGIC)
        for split in range(1, len(packet)):
            mux = m.MuxDevice(Mock())
            mux.version = 2
            control = Mock()
            mux.on_control = control
            with self.subTest(split=split):
                mux.feed(b'corrupt prefix' + packet[:split])
                mux.feed(packet[split:])
                control.assert_called_once_with(b'healthy')
                self.assertEqual(b'', mux._assembler._buf)

    def test_good_packet_before_bad_packet_is_not_lost(self):
        received = []
        self.mux.on_control = received.append
        control = m.build_mux_packet(2, m.PROTO_CONTROL, b'first')
        self.mux.feed(control + b'broken header bytes here')
        self.mux.feed(m.build_mux_packet(2, m.PROTO_CONTROL, b'second'))
        self.assertEqual([b'first', b'second'], received)

    def test_oversized_corrupt_stream_retains_at_most_split_header(self):
        self.connection()
        for _ in range(10):
            self.mux.feed(b'\xff' * m.DEV_MRU)
            self.assertLessEqual(len(self.mux._assembler._buf), 15)
        self.mux.feed(m.build_mux_packet(2, m.PROTO_CONTROL, b'recovered'))
        self.assertEqual(0, len(self.mux._assembler._buf))

    def test_same_read_can_negotiate_version_then_parse_v2(self):
        self.mux.version = 0
        callback = Mock()
        self.mux.on_control = callback
        self.mux.feed(m.build_mux_packet(1, m.PROTO_VERSION, m.build_version_payload()) +
                      m.build_mux_packet(2, m.PROTO_CONTROL, b'next'))
        self.assertEqual(2, self.mux.version)
        callback.assert_called_once_with(b'next')

    def test_bad_v2_magic_and_short_tcp_invalidate_streams(self):
        for packet in (m.build_mux_packet(2, m.PROTO_TCP, b'short'),
                       struct.pack('!IIIHH', 6, 36, 0, 0, 0) + bytes(20)):
            with self.subTest(packet=packet):
                self.setUp()
                conn = self.connection()
                self.mux.feed(packet)
                self.assertTrue(conn.closed)
                self.assertIsNone(self.mux._failure_reason)

    def test_corrupt_usb_read_keeps_same_reader_and_interface_alive(self):
        import usb.core
        transport = m.UsbMuxTransport.__new__(m.UsbMuxTransport)
        transport.mux = self.mux
        transport.timeout_ms = 10
        transport._stop = threading.Event()
        transport._thread = None
        transport.resumed = True
        transport.bytes_in = 0
        transport.dev = Mock()
        healthy = threading.Event()
        self.mux.on_control = lambda _: healthy.set()
        chunks = iter((b'bad mux framing bytes', m.build_mux_packet(2, m.PROTO_CONTROL, b'healthy')))
        def read(*args, **kwargs):
            self.assertEqual(16384, args[0])
            try: return next(chunks)
            except StopIteration:
                transport._stop.wait(.01)
                raise usb.core.USBTimeoutError('idle')
        transport._ep_in = Mock(read=read)
        self.connection()
        with patch('usb.util.release_interface') as release, patch('usb.util.claim_interface') as claim:
            try:
                transport.start()
                self.assertTrue(healthy.wait(1))
                self.assertTrue(transport._thread.is_alive())
                self.assertIsNone(transport.failure_reason)
                transport.dev.reset.assert_not_called()
                transport.dev.set_configuration.assert_not_called()
                transport.dev.clear_halt.assert_not_called()
                release.assert_not_called()
                claim.assert_not_called()
                self.assertEqual([], self.writes)  # no VERSION/SETUP handshake
            finally:
                transport._stop.set()
                transport._thread.join(1)


if __name__ == '__main__':
    unittest.main()
