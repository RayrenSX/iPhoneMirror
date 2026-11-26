"""Offline protocol and actual RVRA decoder regressions. No phone/network required."""
import asyncio
import hashlib
import io
import os
import json
from pathlib import Path
import re
import struct
import subprocess
import sys
import tempfile
import unittest
import uuid
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
    def test_direct_decoder_reports_complete_frames_and_preserves_orientation(self):
        try:
            from coredevice_media import decoder_path
            executable = decoder_path()
        except FileNotFoundError:
            self.skipTest("CoreDevice decoder is not built")

        packets = []
        for i, (name, orientation, width, height) in enumerate((
                ('portrait', 1, 144, 256), ('landscape', 4, 256, 144))):
            nals = next(units(name))
            payload = b''.join(b'\x00\x00\x00\x01' + nal for nal in nals)
            packets.append(PACKET.pack(b'IMPK', len(payload), i, width | orientation << 28, height, 0, 0) + payload)
        with tempfile.TemporaryFile() as pipe:
            # Each drain starts with an empty preview mailbox.
            for packet in packets:
                result = subprocess.run([str(executable), '--direct-video'], input=packet, stdout=pipe,
                                        stderr=subprocess.PIPE, check=True, timeout=10)
                self.assertEqual(result.stderr, b'BV')
            pipe.seek(0)
            expected = json.loads((FIXTURES / 'frames.sha256.json').read_text())
            for i, (name, orientation, width, height) in enumerate((
                    ('portrait', 1, 144, 256), ('landscape', 4, 256, 144))):
                length = width * height * 3 // 2
                self.assertEqual(FRAME.unpack(pipe.read(FRAME.size)),
                                 (b'IMCD', 4 + orientation, width, height, length, i))
                self.assertEqual(hashlib.sha256(pipe.read(length)).hexdigest(), expected[name][0])
            self.assertEqual(pipe.read(1), b'')

    def test_decoder_preserves_the_native_preview_framing_at_every_rvra_tier(self):
        try:
            from coredevice_media import decoder_path
            executable = decoder_path()
        except FileNotFoundError:
            self.skipTest("CoreDevice decoder is not built")
        nals = next(units('portrait'))
        payload = b''.join(b'\x00\x00\x00\x01' + nal for nal in nals)
        def decode(active_w, active_h, target_w, target_h):
            packet = PACKET.pack(b'IMPK', len(payload), 0, active_w, active_h, target_w, target_h)
            return decoded_frames(subprocess.run([str(executable)], input=packet + payload,
                capture_output=True, timeout=10, check=True).stdout)[0]
        smaller_tier = decode(144, 256, (164 << 16) | 72, (272 << 16) | 128)
        unpadded = decode(144, 256, 72, 128)
        self.assertEqual(smaller_tier, unpadded, "Neither axis may crop to the preview size")
        target_w = (140 << 16) | 72
        target_h = (252 << 16) | 128
        self.assertEqual(decode(144, 256, target_w, target_h), decode(140, 252, 72, 128))
        self.assertEqual(decode(72, 128, target_w, target_h), decode(70, 126, 72, 128))
        self.assertEqual(decode(0, 0, target_w, target_h), decode(140, 252, 72, 128))

    def packet(self, payload, seq=1, ts=0, marker=True):
        return RtpPacket(seq, ts, 123, marker, 96, payload)

    def test_rtp_bounds_and_extension_padding(self):
        data = struct.pack('!BBHII', 0xb1, 224, 65535, 456, 123) + b'CSRC' + b'\xbe\xde\x00\x01abcd' + b'\x26\x01data' + b'\x00\x02'
        p = parse_rtp(data)
        self.assertEqual((p.sequence, p.timestamp, p.payload), (65535, 456, b'\x26\x01data'))
        self.assertEqual(p.extension_profile, 0xBEDE)
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
        for w, h in ((1264, 2752), (1088, 1920), (256, 144)):
            trailer = struct.pack('!HH', w, h) + bytes.fromhex('0000030000049209e403')
            self.assertEqual(active_size(b'\x26\x01slice' + trailer), (w, h))
            self.assertEqual(active_size(b'\x26\x01' + trailer[:-1] + b'\x04'), (0, 0))
        self.assertEqual(active_size(b'\x26\x01' + struct.pack('!HH', 9999, 2752) + trailer[4:]), (0, 0))

    def test_rvra_trailer_is_removed_only_from_the_slice(self):
        trailer = struct.pack('!HH', 1208, 2656) + bytes.fromhex('0000030000049209e403')
        receiver = HevcReceiver()
        receiver.wait_key = False
        suffix_sei = b'\x50\x01' + trailer
        self.assertEqual(active_size(suffix_sei), (0, 0))
        receiver.push(self.packet(b'\x02\x01slice' + trailer, marker=False))
        unit = receiver.push(self.packet(suffix_sei, seq=2))
        self.assertEqual((unit.width, unit.height), (1208, 2656))
        self.assertIn(b'slice' + trailer, unit.data)
        self.assertEqual(unit.decoder_data(), b'\x00\x00\x00\x01\x02\x01slice\x00\x00\x00\x01' + suffix_sei)

    def test_missing_delta_size_keeps_previous_rvra_tier_until_keyframe(self):
        try:
            from coredevice_media import decoder_path
            decoder = decoder_path()
        except FileNotFoundError:
            self.skipTest("CoreDevice decoder is not built")
        def decode(repeat):
            wire = bytearray()
            for i, nals in enumerate(units('portrait')):
                data = b''.join(b'\x00\x00\x00\x01' + nal for nal in nals)
                w, h = (112, 192) if i == 0 or (repeat and i < 3) else (0, 0)
                wire += PACKET.pack(b'IMPK', len(data), i, w, h, 144, 256) + data
            return decoded_frames(subprocess.run([decoder], input=wire, capture_output=True,
                                                 timeout=10, check=True).stdout)
        repeated = decode(True)
        self.assertEqual(decode(False), repeated)
        reference = json.loads((FIXTURES / 'frames.sha256.json').read_text())['portrait']
        self.assertEqual([f[3] for f in repeated[3:]], reference[3:])

    def test_tier_changes_request_a_bounded_keyframe_refresh(self):
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'a' * 32, live=True)
        pipeline.receiver.wait_key = False
        pipeline.last_video = 101.5
        pipeline._note_video_frame(AccessUnit(b'', 0, True, 1216, 2656), 100)
        pipeline._note_video_frame(AccessUnit(b'', 1, False, 1088, 1920), 101)
        pipeline._note_video_frame(AccessUnit(b'', 2, False), 101.5)
        self.assertEqual((pipeline.size_changes, pipeline.missing_sizes), (1, 1))
        self.assertFalse(pipeline._should_request_keyframe(101.5))
        self.assertFalse(pipeline._should_request_keyframe(102))
        self.assertTrue(pipeline._should_request_keyframe(102.3))
        pipeline._note_video_frame(AccessUnit(b'', 3, True, 1216, 2656), 102.3)
        self.assertFalse(pipeline._should_request_keyframe(102.5))

    def test_only_one_keyframe_probe_is_sent_during_an_idle_episode(self):
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'a' * 32, live=True)
        pipeline.receiver.wait_key = False
        pipeline.last_video = 20
        self.assertFalse(pipeline._should_request_keyframe(21))
        self.assertTrue(pipeline._should_request_keyframe(22.1))
        pipeline.last_pli = 22.1
        self.assertFalse(pipeline._should_request_keyframe(25))
        pipeline.last_video = 26
        self.assertTrue(pipeline._should_request_keyframe(28.1))

    def test_rvra_doubly_escaped_trailer_survives_a_fragment_boundary(self):
        receiver = HevcReceiver()
        receiver.wait_key = False
        trailer = bytes.fromhex('02d00500000300000300049209e403')
        self.assertEqual(active_size(b'\x02\x01slice' + trailer), (720, 1280))
        self.assertEqual(active_size(b'\x02\x01slice' + trailer[:-1]), (0, 0))
        self.assertEqual(active_size(b'\x02\x01slice' + trailer[:6] + b'\x05' + trailer[7:]), (0, 0))
        self.assertIsNone(receiver.push(self.packet(b'\x62\x01\x81slice' + trailer[:7], marker=False)))
        unit = receiver.push(self.packet(b'\x62\x01\x41' + trailer[7:], seq=2))
        self.assertEqual((unit.width, unit.height, unit.trailer_length), (720, 1280, 15))
        self.assertTrue(unit.data.endswith(trailer))
        self.assertEqual(unit.decoder_data(), b'\0\0\0\1\x02\x01slice')

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
        self.assertEqual(f.rctl(), struct.pack('!BBHI4sIIIII', 128, 204, 7, 789, b'RCTL',
            0x85000004, (7200 >> 8) << 16, 1, (7200 // 24) << 16, (4 << 16) | 0xea61))

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
    async def test_live_receiver_recovers_out_of_order_fragments_at_sequence_wrap(self):
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'a' * 32, live=True)
        try:
            pipeline.receiver.wait_key = False
            packets = list(datagrams([b'\x02\x01' + b'x' * 1600], 65535, 800))
            self.assertEqual(len(packets), 3)
            for i in (0, 2, 2, 1):
                pipeline.feed(packets[i])
            unit = pipeline.queues[0].get_nowait()
            self.assertEqual(unit.data, b'\0\0\0\1\x02\x01' + b'x' * 1600)
            self.assertEqual((pipeline.receiver.gaps, pipeline.recovered_packets), (0, 1))
            self.assertIsNone(pipeline.reorder_timer)
        finally:
            await pipeline.close()

    async def test_live_receiver_expires_a_missing_fragment_and_requests_recovery(self):
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'a' * 32, live=True)
        try:
            pipeline.receiver.wait_key = False
            packets = list(datagrams([b'\x02\x01' + b'x' * 1600], 1, 800))
            pipeline.feed(packets[0])
            pipeline.feed(packets[2])
            self.assertEqual(pipeline.receiver.gaps, 0)
            async with asyncio.timeout(1):
                while not pipeline.receiver.gaps:
                    await asyncio.sleep(.005)
            self.assertEqual(pipeline.receiver.gaps, 1)
            self.assertTrue(pipeline.receiver.wait_key)
            self.assertTrue(pipeline.queues[0].empty())
            self.assertIsNone(pipeline.reorder_timer)
        finally:
            await pipeline.close()

    async def test_partial_direct_frame_retires_the_preview_pipe(self):
        if os.name != 'nt':
            self.skipTest('Direct preview uses a Windows named pipe')
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'a' * 32, live=True, direct_video=True)
        reader = asyncio.StreamReader()
        reader.feed_data(b'BVD' + b'B')
        reader.feed_eof()
        process = type('Decoder', (), {'stderr': reader})()
        await pipeline._output(process)
        self.assertTrue(pipeline.pipe_failed)
        self.assertEqual((pipeline.decoded_video, pipeline.frames, pipeline.display_drops), (2, 1, 1))
        self.assertEqual(pipeline.restart_tasks, [None, None])

    async def test_direct_decoder_recovers_through_the_same_preview_pipe(self):
        if os.name != 'nt':
            self.skipTest('Direct preview uses a Windows named pipe')
        try:
            import win32file
            import win32pipe
            from coredevice_media import decoder_path
            decoder_path()
        except (ImportError, FileNotFoundError):
            self.skipTest('Windows pipe bindings and the compatible decoder are required')
        name = 'iPhoneMirror.CoreDevice.' + uuid.uuid4().hex
        pipe = win32pipe.CreateNamedPipe(r'\\.\pipe' + '\\' + name, win32pipe.PIPE_ACCESS_INBOUND,
            8, 1, 0, 1024 * 1024, 0, None)
        pipeline = MediaPipeline(name, live=True, direct_video=True)
        nals = next(units('portrait'))
        payload = b''.join(b'\x00\x00\x00\x01' + nal for nal in nals)
        received = asyncio.Queue()
        loop = asyncio.get_running_loop()

        def read_exact(size):
            data = bytearray()
            while len(data) < size:
                data.extend(win32file.ReadFile(pipe, size - len(data))[1])
            return data

        def read_preview():
            win32pipe.ConnectNamedPipe(pipe, None)
            for _ in range(3):
                header = FRAME.unpack(read_exact(FRAME.size))
                if header[4]:
                    read_exact(header[4])
                loop.call_soon_threadsafe(received.put_nowait, header)

        reader = asyncio.create_task(asyncio.to_thread(read_preview))
        try:
            await pipeline.start()
            pipeline.interface_orientation = 1
            pipeline.queues[0].put_nowait(AccessUnit(payload, 0, True, 144, 256))
            first = await asyncio.wait_for(received.get(), 5)
            self.assertEqual(first[:4], (b'IMCD', 5, 144, 256))
            pipeline.processes[0].kill()
            self.assertEqual(await asyncio.wait_for(received.get(), 5), (b'IMCD', 3, 0, 0, 0, 0))
            async with asyncio.timeout(5):
                while pipeline.decoder_restarts[0] != 1:
                    await asyncio.sleep(.01)
            pipeline.interface_orientation = 2
            pipeline.queues[0].put_nowait(AccessUnit(payload, 800, True, 144, 256))
            resumed = await asyncio.wait_for(received.get(), 5)
            self.assertEqual(resumed[:4], (b'IMCD', 6, 144, 256))
            await pipeline.finish()
            self.assertEqual((pipeline.decoded_video, pipeline.frames, pipeline.display_drops), (2, 2, 0))
        finally:
            await pipeline.close()
            win32file.CloseHandle(pipe)
            await asyncio.gather(reader, return_exceptions=True)

    async def test_live_preview_drops_only_decoded_frames_when_consumer_is_slow(self):
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'a' * 32, live=True)
        pipeline._queue_live_video(b'old', b'old pixels', True)
        pipeline._queue_live_video(b'new', b'new pixels', True)
        self.assertEqual(pipeline.live_video.get_nowait(), (b'new', b'new pixels', True))
        self.assertEqual(pipeline.display_drops, 1)
        self.assertEqual(pipeline.queue_drops, 0)

    async def test_decoder_failure_restarts_and_recovers_at_complete_keyframe(self):
        if not (ROOT / 'dist/CoreDevice/iPhoneMirror.MediaDecoder.exe').exists():
            self.skipTest('Build decoder first')
        class Sink(io.BytesIO):
            def close(self): pass
        sink = Sink()
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'f' * 32)
        with patch('coredevice_media.open', return_value=sink, create=True):
            await pipeline.start()
        try:
            pipeline.processes[0].kill()
            for _ in range(200):
                if pipeline.decoder_restarts[0] == 1: break
                await asyncio.sleep(.01)
            self.assertEqual(pipeline.decoder_restarts[0], 1)
            self.assertIsNone(pipeline.decoder_errors[0])
            sequence = 1
            for index, nals in enumerate(units('portrait')):
                for data in datagrams(nals, sequence, index * 2400):
                    pipeline.feed(data)
                    sequence += 1
                await asyncio.sleep(.02)
            await pipeline.finish()
            self.assertEqual(pipeline.frames, 9)
            self.assertIn(FRAME.pack(b'IMCD', 3, 0, 0, 0, 0), sink.getvalue())
        finally:
            await pipeline.close()

    async def test_encoded_recording_starts_with_irap_and_audio_failure_does_not_block_video(self):
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'e' * 32)
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, {'LOCALAPPDATA': directory}):
                result = pipeline.start_recording('h265')
            pipeline.decoder_errors[1] = 'audio_EOFError'
            first = next(units('portrait'))
            for packet in datagrams(first, 1, 0):
                pipeline.feed(packet)
            self.assertFalse(pipeline.queues[0].empty())
            result = pipeline.stop_recording()
            data = Path(result['path']).read_bytes()
            self.assertTrue(data.startswith(b'\x00\x00\x00\x01\x40'))
            self.assertGreater(result['bytes'], 0)
            self.assertFalse(pipeline.status()['recording'])

    async def test_midstream_packet_recording_bootstraps_codec_parameters(self):
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'b' * 32)
        first = next(units('portrait'))
        sequence = 1
        for data in datagrams(first, sequence, 0):
            pipeline.feed(data); sequence += 1
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, {'LOCALAPPDATA': directory}):
                path = pipeline.start_recording('imrtp')['path']
            # A phone may send the next IRAP without repeating parameter sets.
            picture = [nal for nal in first if (nal[0] >> 1) & 63 not in (32, 33, 34)]
            for data in datagrams(picture, sequence, 24000):
                pipeline.feed(data); sequence += 1
            pipeline.stop_recording()
            receiver, complete = HevcReceiver(), []
            for _, _, data in read_recording(Path(path)):
                unit = receiver.push(parse_rtp(data))
                if unit: complete.append(unit)
            self.assertEqual(len(complete), 1)
            self.assertTrue(complete[0].key)

    async def test_recording_disk_failure_does_not_stop_video(self):
        class FailedDisk:
            def write(self, data): raise OSError("disk full")
            def close(self): raise OSError("flush failed")
        pipeline = MediaPipeline('iPhoneMirror.CoreDevice.' + 'c' * 32)
        pipeline.encoded_recording = {"file": FailedDisk(), "bytes": 0, "started": False}
        for data in datagrams(next(units('portrait')), 1, 0):
            pipeline.feed(data)
        self.assertFalse(pipeline.queues[0].empty())
        self.assertIsNone(pipeline.error)
        self.assertEqual(pipeline.recording_error, "OSError")
        self.assertIsNone(pipeline.encoded_recording)

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
