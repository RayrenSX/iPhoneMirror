"""Device management RPC and feedback required by the existing HID authentication stream."""
from __future__ import annotations

import asyncio
import contextlib
import ipaddress
import json
import logging
import re
import time
import uuid

from pymobiledevice3.exceptions import AppInstallError, CoreDeviceError, InvalidServiceError
from pymobiledevice3.remote.core_device.display_service import DisplayService
from pymobiledevice3.services.springboard import SpringBoardServicesService

from coredevice_media import Feedback, MediaPipeline, parse_rtp
from device_management import ACTIONS, DeviceManagement, HubError

log = logging.getLogger(__name__)

_CLIENT_MEDIA_SESSION = 'avcMediaStreamOptionClientSessionID'


def _is_own_media_session(session, session_id):
    try:
        current = session['connection']['options'][_CLIENT_MEDIA_SESSION]['uuid']
        return current == session_id or isinstance(current, str) and uuid.UUID(current) == session_id
    except (TypeError, KeyError, ValueError):
        return False


async def stop_display_media(rsd, session_id, *, service_factory=None):
    """Release remote observation without stopping a different client's stream."""
    service_factory = service_factory or DisplayService

    @contextlib.asynccontextmanager
    async def display_channel():
        # A reused RemoteXPC channel can assert on iOS when the daemon sends
        # the second media action's reply. Each channel gets one invocation.
        channel = service_factory(rsd)
        try:
            await channel.connect()
            yield channel
        finally:
            with contextlib.suppress(Exception):
                await asyncio.wait_for(channel.close(), .5)

    async def stop():
        for attempt in range(2):
            try:
                async with asyncio.timeout(3):
                    async with display_channel() as query:
                        status = await query.get_media_stream_server_status()
                    sessions = status.get('sessions')
                    if type(sessions) is not list or any(
                            not _is_own_media_session(item, session_id) for item in sessions):
                        log.warning('CoreDevice media release skipped: server reports another or unknown session')
                        return False
                    # The pinned SDK sends the startup UUID as the stop payload.
                    # iOS expects StopRequest; an idle server can still have
                    # a remote-observation assertion after its last RTP stream.
                    async with display_channel() as stopper:
                        await stopper.invoke('com.apple.coredevice.feature.stopmediastream',
                                             {'stopAll': True},
                                             action_identifier='com.apple.coredevice.action.mediastreamstop')
                    async with display_channel() as verification:
                        status = await verification.get_media_stream_server_status()
                    if status.get('running') is not False or status.get('sessions') != []:
                        raise RuntimeError('CoreDevice media server did not become idle')
            except Exception as error:
                log.warning('CoreDevice media stop attempt=%d failed: %s', attempt + 1, type(error).__name__)
            else:
                log.info('CoreDevice media stop completed attempt=%d', attempt + 1)
                return True
        return False

    # The transport owner must wait for stop before leaving its RSD scope,
    # even if shutdown or tunnel recovery cancels an in-flight request.
    return await wait_for_media_cleanup(asyncio.create_task(stop()))


async def wait_for_media_cleanup(task):
    cancelled = None
    while not task.done():
        try:
            await asyncio.shield(task)
        except asyncio.CancelledError as error:
            cancelled = error
    if cancelled is not None:
        raise cancelled
    return task.result()


class DeviceHub:
    def __init__(self, owner, media_receiver_factory=None):
        self.owner = owner
        self.media_receiver_factory = media_receiver_factory
        self.tasks = {}
        self.lock = asyncio.Lock()
        self.feedback = None
        self.feedback_task = None
        self.management = DeviceManagement(owner)
        self.mirror = None
        self.mirror_watch = None

    async def bind(self):
        """Rebind feedback after the existing HID bridge repairs its tunnel."""
        answer = self.owner.stream_answer
        cfg = answer.get('connection', {}).get('streamConfig', {})
        local, remote = int(cfg.get('RemoteSSRC', 0)), int(cfg.get('LocalSSRC', 0))
        self.feedback = Feedback(local, remote) if local and remote else None
        if self.feedback_task is None or self.feedback_task.done():
            self.feedback_task = asyncio.create_task(self._gate_feedback())

    async def _gate_feedback(self):
        try:
            while True:
                await asyncio.sleep(1)
                if self.feedback and self.owner.transport and self.owner.stream_answer and not (
                        self.mirror and self.mirror.shares_gate):
                    cfg = self.owner.stream_answer['connection']['streamConfig']
                    for report in self.feedback.reports(time.monotonic(), time.time()):
                        await self.owner.transport.sendto(report, self.owner.rsd.service.address[0], int(cfg['SourcePort']))
        except asyncio.CancelledError:
            raise
        except Exception:
            # The bridge health monitor owns transport recovery.
            pass

    def feed(self, data):
        if self.mirror and self.mirror.shares_gate:
            self.mirror.pipeline.feed(data)
            return
        if self.feedback:
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
        except FileNotFoundError:
            response['code'] = 'DECODER_MISSING'
        except ValueError:
            response['code'] = 'INVALID_REQUEST'
        except AppInstallError:
            response['code'] = 'APP_INSTALL_FAILED'
        except InvalidServiceError:
            response['code'] = 'CAPABILITY_NOT_SUPPORTED'
        except Exception as error:
            response['code'] = 'SESSION_CHANGED' if str(error) == 'SESSION_CHANGED' else 'DEVICE_HUB_UNAVAILABLE'
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
        if action == 'mirror_start':
            if self.mirror is not None:
                raise HubError('COREDEVICE_MIRROR_BUSY')
            major = str(self.owner.rsd.product_version).split('.')[0]
            if not major.isdecimal() or int(major) < 27:
                raise HubError('COREDEVICE_MIRROR_UNSUPPORTED')
            direct_video = args.get('directVideo', False)
            if type(direct_video) is not bool:
                raise ValueError('Invalid direct video flag')
            mirror = CoreDeviceMirror(self.owner, args.get('pipe', ''),
                                      args.get('width', 0), args.get('height', 0),
                                      args.get('panelWidth', 0), args.get('panelHeight', 0),
                                      self.media_receiver_factory, direct_video=direct_video)
            self.mirror = mirror
            try:
                await mirror.start()
            except BaseException:
                await self._stop_mirror()
                raise
            self.mirror_watch = asyncio.create_task(self._watch_mirror(mirror))
            return {'decodedFrames': mirror.pipeline.frames}
        if action == 'mirror_stop':
            if self.mirror is not None and self.mirror.pipeline.pipe_name == args.get('pipe'):
                pipeline = self.mirror.pipeline
                shared = self.mirror.shares_gate
                if await self._stop_mirror() is False:
                    raise HubError('COREDEVICE_MIRROR_RELEASE_UNCONFIRMED')
                return {'running': False, 'sharesControlMedia': shared,
                        'decodedFrames': pipeline.frames, 'queueDrops': pipeline.queue_drops,
                        'displayDrops': pipeline.display_drops, 'packetGaps': pipeline.receiver.gaps,
                        'sourceFrames': pipeline.received_video, 'decoderFrames': pipeline.decoded_video,
                        'decoderRestarts': pipeline.decoder_restarts[0]}
            return {'running': False}
        return await self.management.request(action, args)

    async def _watch_mirror(self, mirror):
        previous_at = time.monotonic()
        previous = (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
        while self.mirror is mirror:
            await asyncio.sleep(1)
            pipeline = mirror.pipeline
            now = time.monotonic()
            elapsed = now - previous_at
            if elapsed >= 5:
                current = (pipeline.received_video, pipeline.decoded_video, pipeline.frames,
                           pipeline.queue_drops, pipeline.display_drops, pipeline.receiver.gaps,
                           pipeline.decoder_restarts[0], pipeline.size_changes, pipeline.missing_sizes,
                           pipeline.recovered_packets, pipeline.double_escaped_sizes, pipeline.pli_requests)
                source, decoded, written, queue, display, gaps, restarts, tiers, missing, ordered, escaped, pli = (
                    a - b for a, b in zip(current, previous))
                log.info('CoreDevice media source_fps=%.1f decoder_fps=%.1f ipc_fps=%.1f queue_drops=%d '
                         'display_drops=%d packet_gaps=%d decoder_restarts=%d rvra_changes=%d '
                         'missing_sizes=%d reordered_packets=%d double_escaped_sizes=%d pli_requests=%d max_source_gap_ms=%.1f',
                         source / elapsed, decoded / elapsed, written / elapsed, queue, display,
                         gaps, restarts, tiers, missing, ordered, escaped, pli, pipeline.source_frame_gap * 1000)
                pipeline.source_frame_gap = 0.0
                previous_at, previous = now, current
            if (mirror.reader and mirror.reader.done() or pipeline.pipe_failed or
                    time.monotonic() - pipeline.last_video > 30):
                async with self.lock:
                    if self.mirror is mirror:
                        await self._stop_mirror()
                return

    async def _stop_mirror(self):
        if self.mirror is None:
            return
        mirror = self.mirror
        watch, self.mirror_watch = self.mirror_watch, None
        if watch is not None and watch is not asyncio.current_task():
            watch.cancel()
            await asyncio.gather(watch, return_exceptions=True)
        try:
            return await mirror.close()
        finally:
            if self.mirror is mirror:
                self.mirror = None

    async def suspend(self):
        # Retire old operations before the owner replaces RSD. A multi-stage
        # operation must never continue its next stage on the newly bound phone.
        current = asyncio.current_task()
        pending = [task for task in self.tasks.values() if task is not current]
        for task in pending:
            task.cancel()
        await asyncio.gather(*pending, return_exceptions=True)
        await self._stop_mirror()
        await self.management.close()
        self.feedback = None

    async def close(self):
        for task in list(self.tasks.values()):
            task.cancel()
        await asyncio.gather(*list(self.tasks.values()), return_exceptions=True)
        await self.suspend()
        if self.feedback_task:
            self.feedback_task.cancel()
            await asyncio.gather(self.feedback_task, return_exceptions=True)


class CoreDeviceMirror:
    def __init__(self, owner, pipe, width, height, panel_width=0, panel_height=0, media_receiver_factory=None,
                 *, direct_video=False):
        self.rsd = owner.rsd
        display = getattr(self.rsd, '_bridge_touch_display', None)
        self.display_id = getattr(display, 'id', 1)
        self.transport = owner.transport
        self.answer = owner.stream_answer
        self.shares_gate = self.transport is not None and self.answer is not None
        self.media_receiver_factory = media_receiver_factory
        if getattr(display, 'multiple_panels', False):
            width = height = 0
            panel_width = panel_height = 0
        self.pipeline = MediaPipeline(pipe, width, height, live=True,
                                      panel_width=panel_width, panel_height=panel_height, direct_video=direct_video)
        self.session_id = None
        self.service = None
        self.reader = None
        self.orientation_task = None
        self._close_task = None

    async def start(self):
        orientation_observed = asyncio.Event()
        self.orientation_task = asyncio.create_task(self._observe_orientation(orientation_observed))
        with contextlib.suppress(TimeoutError):
            await asyncio.wait_for(orientation_observed.wait(), 2)
        await self.pipeline.start()
        if not self.shares_gate:
            await self._open_video()
        self.pipeline.configure_feedback(0, self.transport, self.answer, self.rsd.service.address[0])
        if not self.shares_gate:
            self.reader = asyncio.create_task(self._receive())
        try:
            async with asyncio.timeout(14):
                while not self.pipeline.frames:
                    if self.reader and self.reader.done() or self.pipeline.pipe_failed:
                        raise HubError('COREDEVICE_MIRROR_DISCONNECTED')
                    await asyncio.sleep(.05)
        except TimeoutError as error:
            raise HubError('COREDEVICE_MIRROR_NO_FRAME') from error

    async def _open_video(self):
        if self.media_receiver_factory is None:
            raise HubError('COREDEVICE_MIRROR_DISCONNECTED')
        self.service = service = DisplayService(self.rsd)
        try:
            await asyncio.wait_for(service.connect(), 6)
            expected = ipaddress.IPv6Address(self.rsd._bridge_tunnel_address)
            self.transport = self.media_receiver_factory(str(expected))
            if ipaddress.IPv6Address(self.transport.local_ip) != expected:
                raise HubError('COREDEVICE_MIRROR_DISCONNECTED')
            self.session_id = uuid.uuid4()
            self.answer = await asyncio.wait_for(service.start_video_stream(
                receiver_ip=self.transport.local_ip, receiver_port=self.transport.port,
                sender_ip=self.rsd.service.address[0], display_id=self.display_id,
                client_session_id=self.session_id, timeout=7,
                allow_rtcp_fb=True, ltrp_enabled=False), 9)
        except CoreDeviceError as error:
            detail = str(error)
            code = ('COREDEVICE_MIRROR_UNSUPPORTED' if '9021' in detail else
                    'COREDEVICE_MIRROR_PHONE_BUSY' if '9022' in detail else 'COREDEVICE_MIRROR_REFUSED')
            raise HubError(code) from error

    async def _receive(self):
        while data := await self.transport.recv():
            self.pipeline.feed(data)

    async def _observe_orientation(self, observed):
        if SpringBoardServicesService.RSD_SERVICE_NAME not in (self.rsd.peer_info or {}).get('Services', {}):
            observed.set()
            return
        while True:
            service = SpringBoardServicesService(self.rsd)
            try:
                await asyncio.wait_for(service.connect(), 2)
                previous = None
                consecutive = 0
                while True:
                    orientation = int(await asyncio.wait_for(service.get_interface_orientation(), 1))
                    if orientation == previous:
                        consecutive += 1
                    else:
                        previous, consecutive = orientation, 1
                    # SpringBoard reports transient intermediate directions
                    # while rotating. Keep the last confirmed transform until
                    # the new direction has held for two polling intervals.
                    if consecutive >= 3:
                        self.pipeline.interface_orientation = orientation
                        observed.set()
                    await asyncio.sleep(.12)
            except Exception:
                observed.set()
            finally:
                with contextlib.suppress(Exception):
                    await asyncio.wait_for(service.close(), 1)
            await asyncio.sleep(1)

    async def close(self):
        if self._close_task is None:
            self._close_task = asyncio.create_task(self._close())
        return await wait_for_media_cleanup(self._close_task)

    async def _close(self):
        released = True
        try:
            if self.session_id is not None:
                released = await stop_display_media(self.rsd, self.session_id)
        finally:
            await self._close_local()
        return released

    async def _close_local(self):
        if self.service is not None:
            with contextlib.suppress(Exception):
                await asyncio.wait_for(self.service.close(), .5)
        tasks = [task for task in (self.reader, self.orientation_task) if task is not None]
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        try:
            await self.pipeline.close()
        finally:
            if not self.shares_gate and self.transport is not None:
                self.transport.close()
