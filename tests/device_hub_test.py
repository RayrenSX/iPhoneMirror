import asyncio
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
import uuid
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from device_hub import CoreDeviceMirror, DeviceHub, stop_display_media
from device_management import ACTIONS, HubError

STOP_FEATURE = 'com.apple.coredevice.feature.stopmediastream'
STOP_ACTION = 'com.apple.coredevice.action.mediastreamstop'
IDLE_MEDIA = {'running': False, 'sessions': []}


def media_status(*session_ids):
    return {'running': True, 'sessions': [{'connection': {'options': {
        'avcMediaStreamOptionClientSessionID': {'uuid': session_id}}}} for session_id in session_ids]}


def media_channel(status=IDLE_MEDIA, *, stop=None):
    return SimpleNamespace(connect=AsyncMock(), close=AsyncMock(), invoke=AsyncMock(side_effect=stop),
                           get_media_stream_server_status=AsyncMock(return_value=status))


class HubTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.owner = SimpleNamespace(_generation=7, _recovering=False, rsd=object(), transport=None,
            stream_answer=None, ipc=SimpleNamespace(emit=AsyncMock()))
        self.hub = DeviceHub(self.owner)

    def service(self):
        service = SimpleNamespace(list_apps=AsyncMock(return_value=[{'bundleIdentifier': 'com.apple.Preferences',
            'name': 'Settings', 'container': '/private/secret'}, {'name': 'No identifier'}]),
            launch_application=AsyncMock())
        manager = SimpleNamespace(__aenter__=AsyncMock(return_value=service), __aexit__=AsyncMock())
        class Context:
            async def __aenter__(self): return await manager.__aenter__()
            async def __aexit__(self, *args): return await manager.__aexit__(*args)
        return service, manager, Context

    async def test_apps_normalize_and_launch_never_kills_existing(self):
        service, manager, context = self.service()
        with patch('device_management.AppServiceService', side_effect=lambda _: context()) as constructor:
            result = await self.hub.request('apps', {})
            self.assertEqual(result['apps'], [{'bundleId': 'com.apple.Preferences', 'name': 'Settings', 'version': '',
                'isFirstParty': False, 'isDeveloper': False, 'isAppClip': False}])
            await self.hub.request('launch', {'bundleId': 'com.apple.Preferences'})
            service.launch_application.assert_awaited_once_with('com.apple.Preferences', kill_existing=False)
            self.assertEqual(constructor.call_count, 2)  # a fresh channel per request
            self.assertEqual(manager.__aexit__.await_count, 2)
            for bundle in ('', 'com.apple/x', 'com.apple;kill', 42, 'a' * 513):
                with self.assertRaises(ValueError): await self.hub.request('launch', {'bundleId': bundle})
            self.assertEqual(service.launch_application.await_count, 1)

    async def test_metal_hud_restart_is_bound_to_selected_device_and_fixed_environment(self):
        service, manager, context = self.service()
        service.launch_application.return_value = [{'processIdentifier': 123}]
        with patch('device_management.AppServiceService', side_effect=lambda _: context()) as constructor:
            result = await self.hub.request('restart_with_metal_hud', {'bundleId': 'com.example.game'})
            constructor.assert_called_once_with(self.owner.rsd)
            service.launch_application.assert_awaited_once_with('com.example.game',
                kill_existing=True, start_suspended=False, environment={'MTL_HUD_ENABLED': '1'})
            self.assertEqual(result, {'status': 'launched', 'bundleId': 'com.example.game',
                'process': [{'processIdentifier': 123}]})
            self.assertEqual(manager.__aexit__.await_count, 1)
            for arguments in ({}, {'bundleId': ''}, {'bundleId': 'com.example/game'},
                              {'bundleId': 'com.example;kill'}, {'bundleId': 42},
                              {'bundleId': 'a' * 513},
                              {'bundleId': 'com.example.game', 'environment': {'DYLD_INSERT_LIBRARIES': 'x'}},
                              {'bundleId': 'com.example.game', 'killExisting': False}):
                with self.subTest(arguments=arguments), self.assertRaises(ValueError):
                    await self.hub.request('restart_with_metal_hud', arguments)
            self.assertEqual(constructor.call_count, 1)
            self.assertEqual(service.launch_application.await_count, 1)

    async def test_metal_hud_restart_failure_closes_channel_without_ordinary_launch_fallback(self):
        service, manager, context = self.service()
        service.launch_application.side_effect = OSError('device rejected launch')
        with patch('device_management.AppServiceService', side_effect=lambda _: context()) as constructor:
            with self.assertRaises(OSError):
                await self.hub.request('restart_with_metal_hud', {'bundleId': 'com.example.game'})
        self.assertEqual(constructor.call_count, 1)
        self.assertEqual(service.launch_application.await_count, 1)
        self.assertEqual(manager.__aexit__.await_count, 1)

    async def test_correlated_result_and_obsolete_generation(self):
        frame = {'kind': 'hub_request', 'requestId': 'a' * 32, 'action': 'apps'}
        self.hub.request = AsyncMock(return_value={'apps': []})
        self.hub.dispatch(frame, 6)
        await asyncio.gather(*list(self.hub.tasks.values()))
        self.hub.request.assert_not_awaited()
        result = self.owner.ipc.emit.call_args.args[0]
        self.assertEqual((result['success'], result['code']), (False, 'SESSION_CHANGED'))
        self.hub.dispatch(frame, 7)
        await asyncio.gather(*list(self.hub.tasks.values()))
        result = self.owner.ipc.emit.call_args.args[0]
        self.assertTrue(result['success'])
        self.assertEqual(result['result'], {'apps': []})

    async def test_cancel_closes_inflight_operation_before_ack(self):
        started, closed = asyncio.Event(), asyncio.Event()
        async def request(*args):
            try:
                started.set()
                await asyncio.Future()
            finally: closed.set()
        self.hub.request = request
        frame = {'kind': 'hub_request', 'requestId': 'b' * 32, 'action': 'launch'}
        self.hub.dispatch(frame, 7)
        await started.wait()
        task = next(iter(self.hub.tasks.values()))
        self.hub.dispatch({**frame, 'kind': 'cancel_hub'}, 7)
        await asyncio.gather(task, return_exceptions=True)
        self.assertTrue(closed.is_set())
        result = self.owner.ipc.emit.call_args.args[0]
        self.assertEqual((result['success'], result['code']), (False, 'CANCELLED'))
        self.assertEqual(self.hub.tasks, {})

    async def test_removed_media_actions_never_reach_device(self):
        self.hub.management.request = AsyncMock()
        for action in ('media_start', 'media_stop', 'media_status', 'record_start', 'record_stop'):
            frame = {'kind': 'hub_request', 'requestId': 'd' * 32, 'action': action}
            self.hub.dispatch(frame, 7)
            await asyncio.gather(*list(self.hub.tasks.values()))
            result = self.owner.ipc.emit.call_args.args[0]
            self.assertEqual((result['success'], result['code']), (False, 'INVALID_REQUEST'))
        self.hub.management.request.assert_not_awaited()

    async def test_coredevice_mirror_is_private_and_requires_ios_27(self):
        self.owner.rsd = SimpleNamespace(product_version='18.7.8')
        with patch('device_hub.CoreDeviceMirror') as mirror:
            with self.assertRaises(HubError) as failure:
                await self.hub.request('mirror_start', {'pipe': 'unused'})
            self.assertEqual(failure.exception.code, 'COREDEVICE_MIRROR_UNSUPPORTED')
            mirror.assert_not_called()
        self.assertFalse(ACTIONS['mirror_start']['public'])
        self.assertFalse(ACTIONS['mirror_stop']['public'])

    async def test_mirror_requires_first_frame_and_stop_targets_its_pipe(self):
        self.owner.rsd = SimpleNamespace(product_version='27.2.1')
        mirror = SimpleNamespace(start=AsyncMock(), close=AsyncMock(), reader=None, shares_gate=True,
            pipeline=SimpleNamespace(pipe_name='current', frames=1, queue_drops=0, display_drops=0,
                                     received_video=1, decoded_video=1, receiver=SimpleNamespace(gaps=0),
                                     decoder_restarts=[0]))
        with patch('device_hub.CoreDeviceMirror', return_value=mirror):
            self.assertEqual(await self.hub.request('mirror_start', {'pipe': 'current'}), {'decodedFrames': 1})
            await self.hub.request('mirror_stop', {'pipe': 'retired'})
            mirror.close.assert_not_awaited()
            stopped = await self.hub.request('mirror_stop', {'pipe': 'current'})
            self.assertTrue(stopped['sharesControlMedia'])
            mirror.close.assert_awaited_once()
            self.assertIsNone(self.hub.mirror)

    async def test_cancelled_stop_retries_the_same_session_on_a_fresh_channel(self):
        session_id = uuid.uuid4()
        started, resume = asyncio.Event(), asyncio.Event()
        async def stop_original(*_, **__):
            started.set()
            await resume.wait()
            raise TimeoutError
        query = media_channel(media_status(session_id))
        original = media_channel(stop=stop_original)
        retry = media_channel(media_status(str(session_id)))
        fresh = media_channel()
        verification = media_channel()
        channels = [query, original, retry, fresh, verification]
        with patch('device_hub.DisplayService', side_effect=channels):
            task = asyncio.create_task(stop_display_media(self.owner.rsd, session_id))
            await asyncio.wait_for(started.wait(), 1)
            task.cancel()
            await asyncio.sleep(0)
            self.assertFalse(task.done())
            resume.set()
            with self.assertRaises(asyncio.CancelledError):
                await task
        original.invoke.assert_awaited_once_with(STOP_FEATURE, {'stopAll': True}, action_identifier=STOP_ACTION)
        fresh.invoke.assert_awaited_once_with(STOP_FEATURE, {'stopAll': True}, action_identifier=STOP_ACTION)
        for channel in channels:
            channel.connect.assert_awaited_once()
            channel.close.assert_awaited_once()
            self.assertEqual(channel.invoke.await_count + channel.get_media_stream_server_status.await_count, 1)

    async def test_idle_server_still_receives_stop_to_clear_remote_observation(self):
        service = media_channel()
        with patch('device_hub.DisplayService', side_effect=[media_channel(), service, media_channel()]):
            self.assertTrue(await stop_display_media(self.owner.rsd, uuid.uuid4()))
        service.invoke.assert_awaited_once_with(STOP_FEATURE, {'stopAll': True}, action_identifier=STOP_ACTION)

    async def test_stop_does_not_end_another_clients_media_session(self):
        session_id = uuid.uuid4()
        service = media_channel(media_status(session_id, uuid.uuid4()))
        with patch('device_hub.DisplayService', return_value=service) as factory:
            self.assertFalse(await stop_display_media(self.owner.rsd, session_id))
        factory.assert_called_once()
        service.invoke.assert_not_awaited()

    async def test_mirror_releases_phone_media_before_decoder_and_socket(self):
        mirror = CoreDeviceMirror(self.owner, 'iPhoneMirror.CoreDevice.' + 'd' * 32, 0, 0)
        mirror.session_id = uuid.uuid4()
        calls = []
        async def stop(*_, **__):
            calls.append('phone')
        async def close_service(): calls.append('service')
        async def close_pipeline(): calls.append('decoder')
        mirror.service = SimpleNamespace(close=close_service)
        mirror.transport = SimpleNamespace(close=lambda: calls.append('socket'))
        mirror.pipeline.close = close_pipeline
        with patch('device_hub.DisplayService', side_effect=[
                media_channel(media_status(mirror.session_id)), media_channel(stop=stop), media_channel()]):
            self.assertEqual(await asyncio.gather(mirror.close(), mirror.close()), [True, True])
        self.assertEqual(calls, ['phone', 'service', 'decoder', 'socket'])

    async def test_rotation_requires_a_stable_interface_direction(self):
        service_name = 'com.apple.springboardservices.shim.remote'
        self.owner.rsd = SimpleNamespace(peer_info={'Services': {service_name: {}}})
        mirror = CoreDeviceMirror(self.owner, 'iPhoneMirror.CoreDevice.' + 'b' * 32, 0, 0)
        mirror.pipeline.interface_orientation = 1
        samples = iter((4, 4, 1, 4, 4, 4))
        directions = []
        updated = asyncio.Event()
        async def read():
            directions.append(mirror.pipeline.interface_orientation)
            return next(samples)
        service = SimpleNamespace(RSD_SERVICE_NAME=service_name, connect=AsyncMock(),
                                  close=AsyncMock(), get_interface_orientation=read)
        with patch('device_hub.SpringBoardServicesService', return_value=service) as factory:
            factory.RSD_SERVICE_NAME = service_name
            task = asyncio.create_task(mirror._observe_orientation(updated))
            try:
                await asyncio.wait_for(updated.wait(), 2)
                self.assertEqual(directions, [1] * 6)
                self.assertEqual(mirror.pipeline.interface_orientation, 4)
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)

    async def test_rebind_cancels_old_operation_before_rsd_replacement(self):
        started, cleaned = asyncio.Event(), asyncio.Event()
        async def request(*args):
            try:
                started.set()
                await asyncio.Future()
            finally:
                cleaned.set()
        self.hub.request = request
        frame = {'kind': 'hub_request', 'requestId': 'c' * 32, 'action': 'install_app'}
        self.hub.dispatch(frame, 7)
        await started.wait()
        await self.hub.suspend()
        self.assertTrue(cleaned.is_set())
        self.assertEqual(self.hub.tasks, {})
        self.assertEqual(self.owner.ipc.emit.call_args.args[0]['code'], 'CANCELLED')


if __name__ == '__main__': unittest.main()
