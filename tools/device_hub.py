"""Device Hub RPC on the bridge's explicitly selected, already prepared RSD."""
from __future__ import annotations

import asyncio
import contextlib
import json
import os
from pathlib import Path
import re
import time
import uuid

from coredevice_media import Feedback, MediaPipeline, parse_rtp
from device_management import ACTIONS, DeviceManagement, HubError
from pymobiledevice3.remote.core_device.app_service import AppServiceService
from pymobiledevice3.remote.core_device.display_service import DisplayService
from pymobiledevice3.remote.core_device.screen_stream import open_media_receiver


class DeviceHub:
    def __init__(self, owner):
        self.owner = owner
        self.pipeline = None
        self.tasks = {}
        self.lock = asyncio.Lock()
        self.feedback = None
        self.feedback_task = None
        self.audio_service = self.audio_transport = self.audio_task = None
        self.audio_requested = False
        self.management = DeviceManagement(owner)

    async def bind(self):
        """Rebind after the existing bridge repairs its tunnel; keep the output pipe."""
        answer = self.owner.stream_answer
        cfg = answer.get('connection', {}).get('streamConfig', {})
        local, remote = int(cfg.get('RemoteSSRC', 0)), int(cfg.get('LocalSSRC', 0))
        self.feedback = Feedback(local, remote) if local and remote else None
        if self.feedback_task is None or self.feedback_task.done():
            self.feedback_task = asyncio.create_task(self._gate_feedback())
        if self.pipeline:
            self.pipeline.receiver = type(self.pipeline.receiver)()
            self.pipeline.first_timestamps = [None, None]
            for channel, error in enumerate(self.pipeline.decoder_errors):
                if error and not error.startswith('audio_'):
                    self.pipeline._decoder_failed(channel, error)
            self.pipeline.last_video = time.monotonic()
            self.pipeline.configure_feedback(0, self.owner.transport, answer, self.owner.rsd.service.address[0])
            if self.audio_requested:
                await self._start_audio()

    async def _gate_feedback(self):
        try:
            while True:
                await asyncio.sleep(1)
                if self.pipeline and not self.owner._recovering and self.owner._media_health:
                    if time.monotonic() - self.pipeline.last_video > 10 or (self.pipeline.error or '').startswith('feedback_'):
                        self.owner._media_health.error = TimeoutError('CoreDevice media stalled; reconnect after keyframe requests')
                if not self.pipeline and self.feedback and self.owner.transport and self.owner.stream_answer:
                    cfg = self.owner.stream_answer['connection']['streamConfig']
                    for report in self.feedback.reports(time.monotonic(), time.time()):
                        await self.owner.transport.sendto(report, self.owner.rsd.service.address[0], int(cfg['SourcePort']))
        except asyncio.CancelledError:
            raise
        except Exception:
            # The bridge health monitor owns transport recovery.
            pass

    def feed(self, data):
        if self.pipeline:
            try:
                self.pipeline.feed(data)
            except Exception as error:
                # Media recording/consumer failures must not kill the HID auth drain.
                self.pipeline.error = 'media_' + type(error).__name__
        elif self.feedback:
            if len(data) > 1 and 192 <= data[1] <= 223:
                self.feedback.note_rtcp(data, time.monotonic())
            else:
                with contextlib.suppress(ValueError):
                    self.feedback.note(parse_rtp(data), time.monotonic())

    def dispatch(self, frame, generation):
        request_id = frame.get('requestId')
        if not isinstance(request_id, str) or not re.fullmatch('[a-f0-9]{32}', request_id):
            return
        if frame.get('kind') == 'cancel_hub':
            if task := self.tasks.get(request_id):
                task.cancel()
            return
        if request_id in self.tasks or len(self.tasks) >= 8:
            return
        self.tasks[request_id] = asyncio.create_task(self._respond(frame, generation))

    async def _respond(self, frame, generation):
        response = {'event': 'hub_result', 'requestId': frame['requestId'], 'generation': generation, 'success': False}
        try:
            action = frame.get('action')
            if not isinstance(action, str) or action not in ACTIONS:
                raise ValueError('Unknown operation')
            async with asyncio.timeout(ACTIONS[action]['timeout']):
                async with self.lock:
                    if generation != self.owner._generation or self.owner._recovering:
                        raise RuntimeError('SESSION_CHANGED')
                    args = frame.get('arguments')
                    response['result'] = await self.request(action, {} if args is None else args)
                    if len(json.dumps(response['result'], ensure_ascii=False).encode('utf-8')) > 3 * 1024 * 1024:
                        response.pop('result', None)
                        raise HubError('RESULT_TOO_LARGE')
                    if generation != self.owner._generation or self.owner._recovering:
                        raise RuntimeError('SESSION_CHANGED')
                    response['success'] = True
        except asyncio.CancelledError:
            response['code'] = 'CANCELLED'
            raise
        except HubError as error:
            response['code'] = error.code
        except (ValueError, FileNotFoundError) as error:
            response['code'] = 'INVALID_REQUEST' if isinstance(error, ValueError) else 'DECODER_MISSING'
        except Exception as error:
            response['code'] = str(error) if str(error) in ('SESSION_CHANGED', 'MEDIA_UNAVAILABLE', 'MEDIA_ALREADY_RUNNING') else 'DEVICE_HUB_UNAVAILABLE'
            response['errorType'] = type(error).__name__
        finally:
            self.tasks.pop(frame['requestId'], None)
            with contextlib.suppress(Exception):
                await self.owner.ipc.emit(response)

    async def request(self, action, args):
        if not isinstance(args, dict):
            raise ValueError('Invalid arguments')
        if not isinstance(action, str) or action not in ACTIONS:
            raise ValueError('Unknown operation')
        if action == 'record_start':
            if not self.pipeline:
                raise RuntimeError('MEDIA_UNAVAILABLE')
            return self.pipeline.start_recording(args.get('format', 'h265'))
        if action == 'record_stop':
            if not self.pipeline:
                raise RuntimeError('MEDIA_UNAVAILABLE')
            return self.pipeline.stop_recording()
        if action == 'media_status':
            return self.pipeline.status() if self.pipeline else {'running': False}
        if action == 'media_stop':
            await self._stop_media()
            return {'running': False}
        if action == 'media_start':
            if self.pipeline:
                raise RuntimeError('MEDIA_ALREADY_RUNNING')
            if self.owner.transport is None or self.owner.stream_answer is None:
                raise RuntimeError('MEDIA_UNAVAILABLE')
            recording = None
            if args.get('record') is True:
                root = Path(os.environ.get('LOCALAPPDATA') or Path.home() / 'AppData' / 'Local') / 'iPhoneMirror' / 'MediaCaptures'
                root.mkdir(parents=True, exist_ok=True)
                recording = root / (time.strftime('%Y%m%d-%H%M%S-') + uuid.uuid4().hex[:8] + '.imrtp')
            pipeline = MediaPipeline(args.get('pipe', ''), args.get('width', 0), args.get('height', 0), recording)
            try:
                await pipeline.start(audio=args.get('audio') is True)
                pipeline.configure_feedback(0, self.owner.transport, self.owner.stream_answer, self.owner.rsd.service.address[0])
                self.pipeline = pipeline
                self.audio_requested = args.get('audio') is True
                if self.audio_requested:
                    await self._start_audio()
                if pipeline.feedback[0]:
                    await pipeline._send(0, pipeline.feedback[0].pli())
                return {**pipeline.status(), 'recordingPath': str(recording) if recording else None}
            except BaseException:
                self.pipeline = None
                await pipeline.close()
                await self.suspend()
                raise
        return await self.management.request(action, args)

    async def _start_audio(self):
        if self.audio_service:
            return
        self.audio_service = DisplayService(self.owner.rsd)
        await self.audio_service.__aenter__()
        self.audio_transport, ip = open_media_receiver(self.audio_service, (1024 * 1024,))
        csid = self.owner.stream_answer['connection']['options']['avcMediaStreamOptionClientSessionID']['uuid']
        if not isinstance(csid, uuid.UUID):
            csid = uuid.UUID(str(csid))
        answer = await self.audio_service.start_audio_stream(ip, self.audio_transport.port,
            self.owner.rsd.service.address[0], client_session_id=csid)
        self.pipeline.configure_feedback(1, self.audio_transport, answer, self.owner.rsd.service.address[0])
        if (self.pipeline.decoder_errors[1] or '').startswith('audio_'):
            self.pipeline.decoder_errors[1] = None
        self.audio_task = asyncio.create_task(self._receive_audio())

    async def _receive_audio(self):
        try:
            while True:
                data = await self.audio_transport.recv()
                if not data:
                    raise EOFError()
                self.pipeline.feed(data, 1)
        except asyncio.CancelledError:
            raise
        except Exception as error:
            if self.pipeline:
                self.pipeline.decoder_errors[1] = 'audio_' + type(error).__name__
                if self.owner._media_health:
                    self.owner._media_health.error = error

    async def suspend(self):
        # Retire old operations before the owner replaces RSD. A multi-stage
        # operation must never continue its next stage on the newly bound phone.
        current = asyncio.current_task()
        pending = [task for task in self.tasks.values() if task is not current]
        for task in pending:
            task.cancel()
        await asyncio.gather(*pending, return_exceptions=True)
        await self.management.close()
        if self.pipeline:
            self.pipeline.receiver.resync()
            self.pipeline.feedback = [None, None]
            self.pipeline.senders = [None, None]
            with contextlib.suppress(Exception):
                await asyncio.wait_for(self.pipeline.mark_reconnecting(), 2)
        self.feedback = None
        if self.audio_task:
            self.audio_task.cancel()
            await asyncio.gather(self.audio_task, return_exceptions=True)
            self.audio_task = None
        if self.audio_transport:
            self.audio_transport.close()
            self.audio_transport = None
        if self.audio_service:
            with contextlib.suppress(Exception):
                await asyncio.wait_for(self.audio_service.__aexit__(None, None, None), 2)
            self.audio_service = None

    async def _stop_media(self):
        await self.suspend()
        if self.pipeline:
            await self.pipeline.close()
            self.pipeline = None
        self.audio_requested = False
        if self.owner.stream_answer:
            await self.bind()

    async def close(self):
        for task in list(self.tasks.values()):
            task.cancel()
        await asyncio.gather(*list(self.tasks.values()), return_exceptions=True)
        await self.suspend()
        if self.pipeline:
            await self.pipeline.close()
            self.pipeline = None
        if self.feedback_task:
            self.feedback_task.cancel()
            await asyncio.gather(self.feedback_task, return_exceptions=True)
