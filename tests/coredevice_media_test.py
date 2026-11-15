"""Offline protocol and actual RVRA decoder regressions. No phone/network required."""
import asyncio
import hashlib
import io
import json
from pathlib import Path
import re
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from coredevice_media import (AccessUnit, CAPTURE_MAGIC, FRAME, Feedback, HevcReceiver,
    MediaPipeline, PACKET, PacketRecording, RECORD, RtpPacket, active_size, parse_rtp, read_recording)

FIXTURES = ROOT / 'tests/fixtures/coredevice'


def units(name):
    nals = re.split(b'\x00\x00(?:\x00)?\x01', (FIXTURES / (name + '.hevc')).read_bytes())
    current = []
    for nal in filter(None, nals):
        if (nal[0] >> 1) & 63 == 35 and current:
            yield current
            current = []
        current.append(nal)
    if current:
        yield current


def datagrams(nals, seq, timestamp):
    """Fragment the real synthetic bitstream, preserving its NAL headers."""
    for index, nal in enumerate(nals):
        fragments = [nal]
        if len(nal) > 600:
            fragments = []
            chunks = [nal[i:i + 597] for i in range(2, len(nal), 597)]
            for i, chunk in enumerate(chunks):
                fu = ((nal[0] >> 1) & 63) | (128 if i == 0 else 0) | (64 if i == len(chunks) - 1 else 0)
                fragments.append(bytes([(nal[0] & 129) | 98, nal[1], fu]) + chunk)
        for i, payload in enumerate(fragments):
            marker = index == len(nals) - 1 and i == len(fragments) - 1
            yield struct.pack('!BBHII', 128, 96 | (128 if marker else 0), seq & 65535, timestamp, 123) + payload
            seq += 1


def decoded_frames(output):
    frames = []
    offset = 0
    while offset < len(output):
        magic, kind, w, h, size, pts = FRAME.unpack_from(output, offset)
        if magic != b'IMCD' or kind != 1 or size != w * h * 3 // 2:
            raise AssertionError('Invalid actual decoder frame')
        offset += FRAME.size
        payload = output[offset:offset + size]
        if len(payload) != size:
            raise AssertionError('Truncated decoder output')
        frames.append((w, h, pts, hashlib.sha256(payload).hexdigest()))
        offset += size
    return frames


def write_rotation_fixture(path):
    with Path(path).open('wb') as output:
        output.write(CAPTURE_MAGIC)
        seq, frame = 65530, 0
        for name in ('portrait', 'landscape'):
            for nals in units(name):
                for packet in datagrams(nals, seq, frame * 2400):
                    seq += 1
                    output.write(RECORD.pack(frame * 100000, 0, len(packet)))
                    output.write(packet)
                frame += 1


class MediaTests(unittest.TestCase):
    def packet(self, payload, seq=1, ts=0, marker=True):
        return RtpPacket(seq, ts, 123, marker, 96, payload)

    def test_rtp_bounds_and_extension_padding(self):
        data = struct.pack('!BBHII', 0xb1, 224, 65535, 456, 123) + b'CSRC' + b'\xbe\xde\x00\x01abcd' + b'\x26\x01data' + b'\x00\x02'
        p = parse_rtp(data)
        self.assertEqual((p.sequence, p.timestamp, p.payload), (65535, 456, b'\x26\x01data'))
        for bad in [b'', data[:11], b'\x90' + data[1:12], data[:-1] + b'\xff', b'\xa0' + data[1:12] + b'\x00']:
            with self.assertRaises(ValueError): parse_rtp(bad)

    def test_aggregation_and_incomplete_receipt_gate(self):
        receiver = HevcReceiver()
        params = [bytes([kind << 1, 1, 42]) for kind in (32, 33, 34)]
        aggregation = b'\x60\x01' + b''.join(struct.pack('!H', len(n)) + n for n in params)
        self.assertIsNone(receiver.push(self.packet(aggregation, marker=False)))
        unit = receiver.push(self.packet(b'\x26\x01key', 2))
        self.assertTrue(unit.key)
        self.assertEqual(unit.data.count(b'\x00\x00\x00\x01'), 4)
        # A missing middle fragment invalidates the full access unit, including a tail marker.
        receiver.push(self.packet(b'\x62\x01\x81abc', 3, 10, False))
        self.assertIsNone(receiver.push(self.packet(b'\x62\x01\x41def', 5, 10)))
        self.assertTrue(receiver.wait_key)
        self.assertIsNone(receiver.push(self.packet(b'\x02\x01delta', 6, 20)))
        self.assertIsNotNone(receiver.push(self.packet(b'\x26\x01key', 7, 30)))
        self.assertEqual(receiver.gaps, 1)

    def test_sequence_wrap_duplicate_and_reorder(self):
        receiver = HevcReceiver()
        for seq in (65534, 65535, 0, 1):
            receiver.push(self.packet(b'\x40\x01param', seq, seq))
        receiver.push(self.packet(b'\x40\x01duplicate', 1, 1))
        receiver.push(self.packet(b'\x40\x01old', 0, 0))
        self.assertEqual(receiver.gaps, 0)
        self.assertEqual(receiver.sequence, 1)

    def test_rvra_trailer_is_generic_and_strict(self):
        for w, h in ((1264, 2752), (720, 1280), (256, 144)):
            trailer = struct.pack('!HH', w, h) + bytes.fromhex('0000030000049209e403')
            self.assertEqual(active_size(b'\x26\x01slice' + trailer), (w, h))
            self.assertEqual(active_size(b'\x26\x01' + trailer[:-1] + b'\x04'), (0, 0))
        self.assertEqual(active_size(b'\x26\x01' + struct.pack('!HH', 9999, 2752) + trailer[4:]), (0, 0))

    def test_feedback_wrap_reports_pli_complete_and_rctl(self):
        f = Feedback(789, 123)
        for i, seq in enumerate((65534, 65535, 0, 2)):
            f.note(self.packet(b'\x26\x01x', seq, i * 2400), 100 + i)
        rr, sr = f.reports(104, 1700000000.5)
        self.assertEqual(len(rr), 48)
        self.assertEqual(len(sr), 44)
        self.assertEqual(int.from_bytes(rr[13:16], 'big'), 1)
        self.assertEqual(int.from_bytes(rr[16:20], 'big'), 65538)
        self.assertEqual(len(f.pli()), 12)
        self.assertEqual(f.complete(7200)[12:], struct.pack('!I', 7200))
        self.assertEqual(len(f.rctl(104)), 32)
        self.assertEqual(f.rctl(104)[8:12], b'RCTL')

    def test_recording_roundtrip_and_corruption(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'packets.imrtp'
            recording = PacketRecording(path)
            recording.write(0, b'video'); recording.write(1, b'audio'); recording.close()
            packets = list(read_recording(path))
            self.assertEqual([(c, d) for _, c, d in packets], [(0, b'video'), (1, b'audio')])
            for content in (b'bad', CAPTURE_MAGIC + b'x', CAPTURE_MAGIC + RECORD.pack(0, 3, 10),
                            CAPTURE_MAGIC + RECORD.pack(0, 0, 12) + b'short'):
                path.write_bytes(content)
                with self.assertRaises(ValueError): list(read_recording(path))

    def test_actual_encoded_reassembly_matches_reference(self):
        receiver = HevcReceiver()
        reconstructed = []
        seq = 65530
        for i, nals in enumerate(units('portrait')):
            for packet in datagrams(nals, seq, i * 2400):
                seq += 1
                unit = receiver.push(parse_rtp(packet))
                if unit: reconstructed.append(unit)
        self.assertEqual(len(reconstructed), 9)
        self.assertEqual(receiver.gaps, 0)
        self.assertEqual(sum(u.key for u in reconstructed), 3)

    def test_actual_decoder_and_orientation_change(self):
        decoder = ROOT / 'dist/CoreDevice/iPhoneMirror.MediaDecoder.exe'
        if not decoder.exists(): self.skipTest('Build the pinned RVRA decoder for the binary integration test')
        wire = bytearray()
        index = 0
        for name in ('portrait', 'landscape'):
            for nals in units(name):
                data = b''.join(b'\x00\x00\x00\x01' + nal for nal in nals)
                wire += PACKET.pack(b'IMPK', len(data), index * 1000000, 0, 0, 0, 0) + data
                index += 1
        result = subprocess.run([str(decoder)], input=wire, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors='replace'))
        frames = decoded_frames(result.stdout)
        expected = json.loads((FIXTURES / 'frames.sha256.json').read_text())
        self.assertEqual(len(frames), 18)
        self.assertEqual([(w, h) for w, h, _, _ in frames], [(144, 256)] * 9 + [(256, 144)] * 9)
        self.assertEqual([sha for _, _, _, sha in frames], expected['portrait'] + expected['landscape'])
        self.assertEqual([pts for _, _, pts, _ in frames], [i * 1000000 for i in range(18)])
        # Hostile dimensions must fail before the RVRA scaler accesses reference planes.
        nals = next(units('portrait')); data = b''.join(b'\x00\x00\x00\x01' + n for n in nals)
        bad = subprocess.run([str(decoder)], input=PACKET.pack(b'IMPK', len(data), 0, 8192, 8192, 0, 0) + data,
                             capture_output=True, timeout=10)
        self.assertNotEqual(bad.returncode, 0)

    def test_actual_loss_recovers_at_next_keyframe(self):
        decoder = ROOT / 'dist/CoreDevice/iPhoneMirror.MediaDecoder.exe'
        if not decoder.exists(): self.skipTest('Build the decoder first')
        receiver = HevcReceiver()
        wire, seq = bytearray(), 1
        for i, nals in enumerate(units('portrait')):
            packets = list(datagrams(nals, seq, i * 2400))
            seq += len(packets)
            for j, data in enumerate(packets):
                if i == 1 and j == 2: continue
                unit = receiver.push(parse_rtp(data))
                if unit: wire += PACKET.pack(b'IMPK', len(unit.data), i * 1000000, 0, 0, 0, 0) + unit.data
        result = subprocess.run([str(decoder)], input=wire, capture_output=True, timeout=15)
        self.assertEqual(result.returncode, 0)
        frames = decoded_frames(result.stdout)
        expected = json.loads((FIXTURES / 'frames.sha256.json').read_text())['portrait']
        self.assertEqual([f[3] for f in frames], expected[:1] + expected[3:])
        self.assertEqual(receiver.gaps, 1)


class AsyncMediaTests(unittest.IsolatedAsyncioTestCase):
    async def test_actual_pipeline_replays_packets_and_drains_decoder(self):
        from coredevice_media import replay
        decoder = ROOT / 'dist/CoreDevice/iPhoneMirror.MediaDecoder.exe'
        if not decoder.exists(): self.skipTest('Build the decoder first')
        class Sink(io.BytesIO):
            def close(self): pass
        sink = Sink()
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'rotation.imrtp'
            write_rotation_fixture(path)
            with patch('coredevice_media.open', return_value=sink, create=True):
                status = await replay(path, 'iPhoneMirror.CoreDevice.' + 'd' * 32, 2)
            self.assertIsNone(status['error'])
            self.assertEqual(status['decodedFrames'], 18)
            self.assertEqual(status['packetGaps'], 0)
            frames = decoded_frames(sink.getvalue())
            expected = json.loads((FIXTURES / 'frames.sha256.json').read_text())
            self.assertEqual([f[3] for f in frames], expected['portrait'] + expected['landscape'])

    async def test_slow_consumer_drops_and_requires_keyframe(self):
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'a' * 32)
        for i in range(4): pipeline.queues[0].put_nowait(AccessUnit(b'data', i, False))
        with patch.object(pipeline.receiver, 'push', return_value=AccessUnit(b'data', 0, False)):
            pipeline.feed(struct.pack('!BBHII', 128, 224, 1, 0, 123) + b'\x02\x01data')
        self.assertEqual(pipeline.queue_drops, 1)
        self.assertTrue(pipeline.receiver.wait_key)
        self.assertTrue(pipeline.queues[0].empty())


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--write-fixture':
        write_rotation_fixture(sys.argv[2])
    else:
        unittest.main()
