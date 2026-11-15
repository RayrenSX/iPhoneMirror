"""Device management RPC and feedback required by the existing HID authentication stream."""
from __future__ import annotations

import asyncio
import contextlib
import json
import re
import time

from pymobiledevice3.exceptions import AppInstallError, InvalidServiceError

from coredevice_media import Feedback, parse_rtp
from device_management import ACTIONS, DeviceManagement, HubError


class DeviceHub:
    def __init__(self, owner):
        self.owner = owner
        self.tasks = {}
        self.lock = asyncio.Lock()
        self.feedback = None
        self.feedback_task = None
        self.management = DeviceManagement(owner)

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
                if self.feedback and self.owner.transport and self.owner.stream_answer:
                    cfg = self.owner.stream_answer['connection']['streamConfig']
                    for report in self.feedback.reports(time.monotonic(), time.time()):
                        await self.owner.transport.sendto(report, self.owner.rsd.service.address[0], int(cfg['SourcePort']))
        except asyncio.CancelledError:
            raise
        except Exception:
            # The bridge health monitor owns transport recovery.
            pass

    def feed(self, data):
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
        except (ValueError, FileNotFoundError):
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
        return await self.management.request(action, args)

    async def suspend(self):
        # Retire old operations before the owner replaces RSD. A multi-stage
        # operation must never continue its next stage on the newly bound phone.
        current = asyncio.current_task()
        pending = [task for task in self.tasks.values() if task is not current]
        for task in pending:
            task.cancel()
        await asyncio.gather(*pending, return_exceptions=True)
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
