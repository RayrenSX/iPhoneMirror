import asyncio
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from device_hub import DeviceHub


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
