"""CoreDevice RTP, bounded decode, feedback and deterministic packet replay.

Wire protocol notes are from rPlayHub fe6a7a9 and pymobiledevice3 11.3.1.
No device discovery, HID ownership, or unsolicited input lives in this module.
"""
from __future__ import annotations

import asyncio
import contextlib
import json
import math
import os
from pathlib import Path
import re
import struct
import sys
import threading
import time
from dataclasses import dataclass

MAX_AU = 16 * 1024 * 1024
PACKET = struct.Struct("<4sIqIIII")
FRAME = struct.Struct("<4sIIIIq")
RECORD = struct.Struct("<QBI")
CAPTURE_MAGIC = b"IMRTP\x01\r\n"
START = b"\x00\x00\x00\x01"


@dataclass(frozen=True)
class RtpPacket:
    sequence: int
    timestamp: int
    ssrc: int
    marker: bool
    payload_type: int
    payload: bytes


def parse_rtp(data: bytes) -> RtpPacket:
    if len(data) < 12 or data[0] >> 6 != 2 or 192 <= data[1] <= 223:
        raise ValueError("Invalid RTP header")
    end = len(data)
    offset = 12 + (data[0] & 15) * 4
    if offset > end:
        raise ValueError("Truncated CSRC")
    if data[0] & 16:
        if offset + 4 > end:
            raise ValueError("Truncated extension")
        offset += 4 + struct.unpack_from("!H", data, offset + 2)[0] * 4
    if data[0] & 32:
        padding = data[-1]
        if not padding or padding > end - offset:
            raise ValueError("Invalid padding")
        end -= padding
    if offset >= end:
        raise ValueError("Empty RTP payload")
    seq, ts, ssrc = struct.unpack_from("!HII", data, 2)
    return RtpPacket(seq, ts, ssrc, bool(data[1] & 128), data[1] & 127, data[offset:end])


def active_size(nal: bytes) -> tuple[int, int]:
    # Only the observed 14-byte RVRA trailer is accepted. Never scan entropy data
    # for a small hardcoded list of phone dimensions.
    if len(nal) >= 16:
        tail = nal[-14:]
        if tail[4:9] == b"\x00\x00\x03\x00\x00" and tail[-1] == 3:
            w, h = struct.unpack_from("!HH", tail)
            if 2 <= w <= 8192 and 2 <= h <= 8192 and not (w | h) & 1:
                return w, h
    return 0, 0


@dataclass(frozen=True)
class AccessUnit:
    data: bytes
    timestamp: int
    key: bool
    width: int = 0
    height: int = 0


class HevcReceiver:
    """RFC 7798 without DONL (not negotiated). Loss invalidates the entire AU.

    A reordered/duplicate packet is ignored. A forward gap requires a new IRAP;
    no partially assembled frame is passed to the decoder or acknowledged.
    """
    def __init__(self):
        self.sequence = None
        self.timestamp = None
        self.ssrc = None
        self.nals = []
        self.fu = None
        self.size = 0
        self.corrupt = False
        self.wait_key = True
        self.parameters = {}
        self.dropped = 0
        self.gaps = 0

    def resync(self):
        self.wait_key = True

    def _reset(self):
        self.nals, self.fu, self.size, self.corrupt = [], None, 0, False

    def push(self, packet: RtpPacket) -> AccessUnit | None:
        if self.ssrc is not None and self.ssrc != packet.ssrc:
            self.sequence = None
            self.parameters.clear()
            self._reset()
            self.wait_key = True
        self.ssrc = packet.ssrc
        gap = False
        if self.sequence is not None:
            delta = (packet.sequence - self.sequence) & 65535
            if delta == 0 or delta >= 32768:
                return None
            gap = delta != 1
        if self.timestamp != packet.timestamp:
            if self.nals or self.fu:
                self.dropped += 1
                self.wait_key = True
            self._reset()
            self.timestamp = packet.timestamp
        self.sequence = packet.sequence
        if gap:
            self.gaps += 1
            self.corrupt, self.wait_key, self.fu = True, True, None
        if self.corrupt:
            self.nals, self.fu, self.size = [], None, 0
            if packet.marker:
                self.dropped += 1
                self._reset()
            return None
        p = packet.payload
        try:
            if len(p) < 2 or p[0] & 128 or not p[1] & 7:
                raise ValueError("Invalid HEVC NAL")
            kind = (p[0] >> 1) & 63
            if kind < 48:
                if self.fu is not None:
                    raise ValueError("Interrupted fragment")
                self.nals.append(p)
                self.size += len(p) + 4
            elif kind == 48:
                if self.fu is not None:
                    raise ValueError("Interrupted fragment")
                offset = 2
                while offset < len(p):
                    if offset + 2 > len(p):
                        raise ValueError("Truncated aggregation")
                    length = struct.unpack_from("!H", p, offset)[0]
                    offset += 2
                    if length < 2 or offset + length > len(p) or (p[offset] >> 1) & 63 >= 48:
                        raise ValueError("Invalid aggregation")
                    self.nals.append(p[offset:offset + length])
                    self.size += length + 4
                    offset += length
            elif kind == 49:
                if len(p) < 4 or p[2] & 63 >= 48 or p[2] & 192 == 192:
                    raise ValueError("Invalid FU")
                if p[2] & 128:
                    if self.fu is not None:
                        raise ValueError("Overlapping FU")
                    self.fu = bytearray([(p[0] & 129) | ((p[2] & 63) << 1), p[1]])
                if self.fu is None:
                    raise ValueError("Missing FU start")
                self.fu.extend(p[3:])
                self.size += len(p) + 4
                if p[2] & 64:
                    self.nals.append(bytes(self.fu))
                    self.fu = None
            else:
                raise ValueError("Unsupported HEVC packet")
            if self.size > MAX_AU:
                raise ValueError("Access unit too large")
        except ValueError:
            self.corrupt, self.wait_key, self.fu = True, True, None
            self.nals, self.size = [], 0
        if not packet.marker:
            return None
        if self.corrupt or self.fu is not None or not self.nals:
            self.dropped += 1
            self.wait_key = True
            self._reset()
            return None
        nals = self.nals
        self._reset()
        key = any(16 <= (n[0] >> 1) & 63 <= 23 for n in nals)
        for nal in nals:
            kind = (nal[0] >> 1) & 63
            if kind in (32, 33, 34):
                self.parameters[kind] = nal
        if not any((n[0] >> 1) & 63 < 32 for n in nals):
            return None
        if self.wait_key:
            if not key or len(self.parameters) != 3:
                self.dropped += 1
                return None
            nals = [self.parameters[k] for k in (32, 33, 34)] + [n for n in nals if (n[0] >> 1) & 63 not in (32, 33, 34)]
            self.wait_key = False
        w, h = next((size for n in reversed(nals) if (size := active_size(n)) != (0, 0)), (0, 0))
        return AccessUnit(b"".join(START + n for n in nals), packet.timestamp, key, w, h)


class Feedback:
    def __init__(self, local_ssrc: int, remote_ssrc: int):
        self.local, self.remote = local_ssrc, remote_ssrc
        self.highest = self.base = None
        self.received = self.expected_prior = self.received_prior = 0
        self.first_ts = self.last_ts = 0
        self.last_at = self.last_sr_at = 0.0
        self.lsr = 0

    def note(self, p: RtpPacket, now: float):
        if p.ssrc != self.remote:
            return
        if self.highest is None:
            self.highest = self.base = p.sequence
            self.first_ts = p.timestamp
        else:
            delta = (p.sequence - (self.highest & 65535)) & 65535
            if delta == 0 or delta >= 32768:
                return
            self.highest += delta
        self.received += 1
        self.last_ts, self.last_at = p.timestamp, now

    def note_rtcp(self, data: bytes, now: float):
        offset = 0
        while offset + 4 <= len(data):
            length = (struct.unpack_from("!H", data, offset + 2)[0] + 1) * 4
            if length < 4 or offset + length > len(data) or data[offset] >> 6 != 2:
                return
            if data[offset + 1] == 200 and length >= 28 and struct.unpack_from("!I", data, offset + 4)[0] == self.remote:
                self.lsr = struct.unpack_from("!I", data, offset + 10)[0]
                self.last_sr_at = now
            offset += length

    def sdes(self):
        return struct.pack("!BBHI", 129, 202, 3, self.local) + b"\x01\x05imdev\x00"

    def reports(self, now: float, wall: float):
        expected = 0 if self.highest is None else self.highest - self.base + 1
        lost = max(0, min(0x7fffff, expected - self.received))
        interval = expected - self.expected_prior
        loss_interval = interval - (self.received - self.received_prior)
        fraction = max(0, min(255, loss_interval * 256 // interval)) if interval else 0
        self.expected_prior, self.received_prior = expected, self.received
        dlsr = int((now - self.last_sr_at) * 65536) if self.last_sr_at else 0
        rr = struct.pack("!BBHII", 129, 201, 7, self.local, self.remote)
        rr += bytes([fraction]) + lost.to_bytes(3, "big")
        rr += struct.pack("!IIII", (self.highest or 0) & 0xffffffff, 0, self.lsr, dlsr & 0xffffffff)
        ntp = wall + 2208988800
        sr = struct.pack("!BBHIIIIII", 128, 200, 6, self.local, int(ntp) & 0xffffffff,
                         int((ntp % 1) * 2**32), self.last_ts, 0, 0)
        return rr + self.sdes(), sr + self.sdes()

    def pli(self):
        return struct.pack("!BBHII", 129, 206, 2, self.local, self.remote)

    def complete(self, timestamp):
        return struct.pack("!BBHIII", 128, 204, 3, self.local, 5, timestamp)

    def rctl(self, now):
        idle = max(0, min(65535, int((now - self.last_at) * 1000))) if self.last_at else 0
        return struct.pack("!BBHI4sIHIHHHHH", 128, 204, 7, self.local, b"RCTL", 0x85000004,
                           ((self.last_ts - self.first_ts) & 0xffffffff) >> 8 & 65535,
                           0, idle, int(now * 1024) & 65535, 0, self.received & 65535, 60000)


class PacketRecording:
    def __init__(self, path: Path):
        self.file = path.open("xb")
        self.file.write(CAPTURE_MAGIC)
        self.started = time.monotonic_ns()
        self.size = len(CAPTURE_MAGIC)

    def write(self, channel: int, data: bytes):
        if self.file is None:
            return
        if self.size + len(data) + RECORD.size > 1024**3:
            self.close()
            return
        self.file.write(RECORD.pack((time.monotonic_ns() - self.started) // 1000, channel, len(data)))
        self.file.write(data)
        self.size += len(data) + RECORD.size

    def close(self):
        if self.file:
            self.file.close()
            self.file = None


def read_recording(path: Path):
    with path.open("rb") as file:
        if file.read(len(CAPTURE_MAGIC)) != CAPTURE_MAGIC:
            raise ValueError("Not an iPhoneMirror RTP recording")
        previous = 0
        while header := file.read(RECORD.size):
            if len(header) != RECORD.size:
                raise ValueError("Truncated recording header")
            micros, channel, size = RECORD.unpack(header)
            if micros < previous or micros > 24 * 3600 * 1000000 or channel not in (0, 1) or not 1 <= size <= 65535:
                raise ValueError("Invalid recording record")
            data = file.read(size)
            if len(data) != size:
                raise ValueError("Truncated recording packet")
            previous = micros
            yield micros, channel, data


def decoder_path() -> Path:
    root = Path(sys.executable).parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parents[1] / "dist"
    path = root / "CoreDevice" / "iPhoneMirror.MediaDecoder.exe"
    if not path.is_file():
        raise FileNotFoundError("CoreDevice decoder is missing; run scripts/build_coredevice_decoder.ps1")
    return path


class MediaPipeline:
    def __init__(self, pipe: str, width=0, height=0, recording: Path | None = None):
        if not re.fullmatch(r"iPhoneMirror\.CoreDevice\.[a-f0-9]{32}", pipe):
            raise ValueError("Invalid private media pipe")
        if any(type(n) is not int or n < 0 or n > 8192 or n == 1 for n in (width, height)) or bool(width) != bool(height):
            raise ValueError("Invalid output dimensions")
        self.pipe_name, self.width, self.height = pipe, width, height
        self.receiver = HevcReceiver()
        self.queues = [asyncio.Queue(4), asyncio.Queue(12)]
        self.tasks, self.processes = [], []
        self.output_tasks = []
        self.feedback_task = None
        self.pipe = None
        self.pipe_lock = threading.Lock()
        self.recording = PacketRecording(recording) if recording else None
        self.feedback = [None, None]
        self.senders = [None, None]
        self.last_pli = 0
        self.frames = self.audio_frames = self.queue_drops = 0
        self.error = None
        self.closing = False
        self.finishing = False
        self.first_timestamps = [None, None]
        self.last_video = time.monotonic()

    async def start(self, audio=False):
        executable = decoder_path()
        try:
            self.pipe = await asyncio.wait_for(asyncio.to_thread(open,
                "\\\\.\\pipe\\" + self.pipe_name, "wb", buffering=0), 5)
            for channel in range(2 if audio else 1):
                proc = await asyncio.create_subprocess_exec(str(executable), *( ["--audio"] if channel else []),
                    stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.DEVNULL, creationflags=0x08000000 if os.name == "nt" else 0)
                self.processes.append(proc)
                output = asyncio.create_task(self._output(proc))
                self.output_tasks.append(output)
                self.tasks.extend([asyncio.create_task(self._input(channel, proc)), output])
            self.feedback_task = asyncio.create_task(self._feedback())
            self.tasks.append(self.feedback_task)
        except BaseException:
            await self.close()
            raise

    def configure_feedback(self, channel, transport, answer, address):
        cfg = answer.get("connection", {}).get("streamConfig", {})
        port, local, remote = (int(cfg.get(k, 0)) for k in ("SourcePort", "RemoteSSRC", "LocalSSRC"))
        if not (1 <= port <= 65535 and 0 < local <= 0xffffffff and 0 < remote <= 0xffffffff):
            raise ValueError("CoreDevice did not negotiate RTCP feedback")
        self.feedback[channel] = Feedback(local, remote)
        self.senders[channel] = (transport, address, port)
        if self.feedback_task is not None and self.feedback_task.done():
            if (self.error or '').startswith('feedback_'):
                self.error = None
            self.feedback_task = asyncio.create_task(self._feedback())
            self.tasks.append(self.feedback_task)

    async def _send(self, channel, data):
        if self.senders[channel]:
            transport, address, port = self.senders[channel]
            await transport.sendto(data, address, port)

    async def _feedback(self):
        try:
            report_at = 0
            while True:
                now = time.monotonic()
                for channel, feedback in enumerate(self.feedback):
                    if feedback:
                        if now >= report_at:
                            for report in feedback.reports(now, time.time()):
                                await self._send(channel, report)
                        if channel == 0:
                            await self._send(0, feedback.rctl(now))
                            if (self.receiver.wait_key or now - self.last_video > 2) and now - self.last_pli > .5:
                                await self._send(0, feedback.pli())
                                self.last_pli = now
                if now >= report_at:
                    report_at = now + 1
                await asyncio.sleep(.05)
        except asyncio.CancelledError:
            raise
        except Exception as error:
            self.error = "feedback_" + type(error).__name__

    def feed(self, data: bytes, channel=0):
        if self.closing or self.error:
            return
        if self.recording:
            self.recording.write(channel, data)
        feedback = self.feedback[channel]
        if len(data) >= 2 and 192 <= data[1] <= 223:
            if feedback:
                feedback.note_rtcp(data, time.monotonic())
            return
        try:
            packet = parse_rtp(data)
        except ValueError:
            return
        if feedback:
            if packet.ssrc != feedback.remote:
                return
            feedback.note(packet, time.monotonic())
        if channel == 0:
            unit = self.receiver.push(packet)
            if unit is None:
                return
        else:
            if packet.payload_type != 101 or len(self.processes) < 2:
                return
            unit = AccessUnit(packet.payload, packet.timestamp, True)
        queue = self.queues[channel]
        if queue.full():
            self.queue_drops += 1
            while not queue.empty():
                queue.get_nowait()
                queue.task_done()
            if channel == 0:
                self.receiver.resync()
                return
        queue.put_nowait(unit)
        if channel == 0:
            self.last_video = time.monotonic()

    async def _input(self, channel, proc):
        try:
            while True:
                unit = await self.queues[channel].get()
                if self.first_timestamps[channel] is None:
                    self.first_timestamps[channel] = unit.timestamp
                ticks = (unit.timestamp - self.first_timestamps[channel]) & 0xffffffff
                pts = ticks * 10000000 // (48000 if channel else 24000)
                proc.stdin.write(PACKET.pack(b"IMPK", len(unit.data), pts, unit.width, unit.height, self.width, self.height))
                proc.stdin.write(unit.data)
                await proc.stdin.drain()
                if not channel and self.feedback[0]:
                    # A receipt is sent only for a complete AU admitted to the decoder.
                    await self._send(0, self.feedback[0].complete(unit.timestamp))
                self.queues[channel].task_done()
        except asyncio.CancelledError:
            raise
        except Exception as error:
            self.error = "decoder_input_" + type(error).__name__

    def _write(self, data):
        with self.pipe_lock:
            self.pipe.write(data)

    async def mark_reconnecting(self):
        if self.pipe and not self.closing:
            await asyncio.to_thread(self._write, FRAME.pack(b'IMCD', 3, 0, 0, 0, 0))

    async def _output(self, proc):
        try:
            while True:
                header = await proc.stdout.readexactly(FRAME.size)
                magic, kind, w, h, size, pts = FRAME.unpack(header)
                if magic != b"IMCD" or kind not in (1, 2) or not 0 < size <= 64 * 1024 * 1024:
                    raise ValueError("Invalid decoder output")
                data = await proc.stdout.readexactly(size)
                await asyncio.to_thread(self._write, header + data)
                if kind == 1:
                    self.frames += 1
                else:
                    self.audio_frames += 1
        except asyncio.CancelledError:
            raise
        except asyncio.IncompleteReadError as error:
            if not self.closing and not (self.finishing and not error.partial):
                self.error = "decoder_output_truncated"
        except Exception as error:
            if not self.closing:
                self.error = "decoder_output_" + type(error).__name__

    def status(self):
        return {"running": not self.closing and bool(self.processes) and self.error is None,
                "decodedFrames": self.frames, "audioFrames": self.audio_frames,
                "packetGaps": self.receiver.gaps, "droppedFrames": self.receiver.dropped,
                "queueDrops": self.queue_drops, "waitingForKeyframe": self.receiver.wait_key,
                "error": self.error, "recording": bool(self.recording and self.recording.file)}

    async def finish(self):
        self.finishing = True
        async with asyncio.timeout(15):
            await asyncio.gather(*(queue.join() for queue in self.queues[:len(self.processes)]))
            for proc in self.processes:
                proc.stdin.close()
                await proc.stdin.wait_closed()
            for proc in self.processes:
                if await proc.wait() != 0:
                    raise RuntimeError('Decoder failed while draining replay')
            await asyncio.gather(*self.output_tasks)

    async def close(self):
        if self.closing:
            return
        self.closing = True
        for task in self.tasks:
            task.cancel()
        for proc in self.processes:
            if proc.returncode is None:
                with contextlib.suppress(ProcessLookupError):
                    proc.kill()
        await asyncio.gather(*self.tasks, return_exceptions=True)
        await asyncio.gather(*(p.wait() for p in self.processes), return_exceptions=True)
        if self.pipe:
            self.pipe.close()
        if self.recording:
            self.recording.close()


async def replay(path: Path, pipe: str, speed=1.0):
    if not math.isfinite(speed) or not .1 <= speed <= 16:
        raise ValueError("Replay speed must be .1..16")
    pipeline = MediaPipeline(pipe)
    await pipeline.start(audio=True)
    try:
        start = time.monotonic()
        for micros, channel, data in read_recording(path):
            await asyncio.sleep(max(0, start + micros / 1000000 / speed - time.monotonic()))
            pipeline.feed(data, channel)
            if pipeline.error:
                raise RuntimeError(pipeline.error)
        await pipeline.finish()
        if pipeline.error:
            raise RuntimeError(pipeline.error)
        return pipeline.status()
    finally:
        await pipeline.close()
