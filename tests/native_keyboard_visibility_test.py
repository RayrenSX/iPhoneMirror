"""Native iOS keyboard visibility owns only the bridge-created HID keyboard."""
import asyncio
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge
from usb_recovery_test import Hid, Ipc


class VisibilityHid(Hid):
    def __init__(self, system_keyboard=True):
        super().__init__()
        self.system_keyboard = system_keyboard
        self.created = None
        self.removed = []

    async def list_connected_services(self):
        result = await super().list_connected_services()
        if not self.system_keyboard:
            result['services'] = [item for item in result['services'] if item['_ServiceID'] != 512]
        return result

    async def create_keyboard_service(self, **_):
        self.created = 0x100002001
        return self.created

    async def remove_keyboard_service(self):
        if self.created is not None:
            self.removed.append(self.created)
            self.created = None


class NativeKeyboardVisibilityTests(unittest.IsolatedAsyncioTestCase):
    async def test_keyboard_service_never_claims_or_removes_system_surface(self):
        hid = object.__new__(bridge.UniversalHIDServiceService)
        hid._hid_request = AsyncMock(return_value={'serviceID': 512})
        with self.assertRaises(ValueError):
            await hid.create_keyboard_service()
        self.assertIsNone(getattr(hid, '_created_keyboard_service_id', None))
        hid._created_keyboard_service_id = 512
        hid._hid_request.reset_mock()
        await hid.remove_keyboard_service()
        hid._hid_request.assert_not_awaited()
        self.assertIsNone(hid._created_keyboard_service_id)

    async def session(self, visible=True, system_keyboard=True, transport='usb'):
        session = bridge.TouchSession(Ipc(), 120, 'test-device', transport=transport,
                                      show_native_keyboard=visible)
        session.hid = VisibilityHid(system_keyboard)
        session.auth_mode = 'direct'
        session.gate_open = True
        session._owns_hid = True
        await session._verify_touch_surface(session.hid)
        await session._emit_ready()
        return session

    async def test_default_keeps_system_keyboard_surface_without_hardware_attach(self):
        for transport in ('usb', 'wireless'):
            with self.subTest(transport=transport):
                session = bridge.TouchSession(Ipc(), 120, 'test-device', transport=transport)
                hid = Hid()
                hid.create_keyboard_service = AsyncMock(return_value=0x100002001)
                session.hid = hid
                session.auth_mode = 'direct'
                session.gate_open = True
                await session._verify_touch_surface(hid)
                await session._emit_ready()
                hid.create_keyboard_service.assert_not_awaited()
                self.assertEqual(512, session.keyboard_service_id)
                await session._send_keyboard_report([4])
                await session._send_keyboard_report([])
                self.assertEqual([(512, []), (512, [4]), (512, [])], hid.keys)

    async def test_visibility_toggles_release_and_remove_only_owned_keyboard(self):
        for transport in ('usb', 'wireless'):
            session = await self.session(transport=transport)
            hid = session.hid
            self.assertTrue(await session.set_native_keyboard_visible(False))
            owned = hid.created
            self.assertNotEqual(512, owned)
            self.assertEqual(owned, session.keyboard_service_id)
            await session._send_keyboard_report([0xE3, 4])
            self.assertTrue(session._held_keyboard_usages)
            self.assertTrue(await session.set_native_keyboard_visible(True))
            self.assertEqual([owned], hid.removed)
            self.assertEqual((owned, []), hid.keys[-2])
            self.assertEqual((512, []), hid.keys[-1])
            self.assertEqual(512, session.keyboard_service_id)
            self.assertEqual(frozenset(), session._held_keyboard_usages)
            self.assertTrue(await session.set_native_keyboard_visible(True))
            self.assertEqual([owned], hid.removed)
            await session._send_keyboard_report([5])
            self.assertEqual((512, [5]), hid.keys[-1])
            self.assertTrue(await session.set_native_keyboard_visible(False))
            self.assertEqual(owned, session.keyboard_service_id)
            self.assertFalse(session.show_native_keyboard)

    async def test_failed_registration_can_be_explicitly_retried(self):
        session = await self.session()
        hid = session.hid
        hid.create_keyboard_service = AsyncMock(side_effect=[RuntimeError('attach refused'), 0x100003001])
        with self.assertRaises(RuntimeError):
            await session.set_native_keyboard_visible(False)
        self.assertTrue(session.show_native_keyboard)
        self.assertEqual(512, session.keyboard_service_id)
        self.assertTrue(await session.set_native_keyboard_visible(False))
        self.assertEqual(0x100003001, session.keyboard_service_id)
        self.assertEqual(2, hid.create_keyboard_service.await_count)

    async def test_explicit_hidden_start_registers_before_ready(self):
        session = await self.session(visible=False)
        self.assertFalse(session.show_native_keyboard)
        self.assertEqual(session.hid.created, session.keyboard_service_id)
        ready = next(event for event in session.ipc.events if event['event'] == 'ready')
        self.assertTrue(ready['nativeKeyboardSupported'])
        self.assertFalse(ready['nativeKeyboardVisible'])

    async def test_rejected_hidden_start_reports_actual_visible_route(self):
        session = bridge.TouchSession(Ipc(), 120, 'test-device', show_native_keyboard=False)
        hid = VisibilityHid()
        hid.create_keyboard_service = AsyncMock(side_effect=NotImplementedError('no external keyboard'))
        session.hid = hid
        session.auth_mode = 'direct'
        session.gate_open = True
        await session._verify_touch_surface(hid)
        await session._emit_ready()
        ready = next(event for event in session.ipc.events if event['event'] == 'ready')
        self.assertFalse(ready['nativeKeyboardSupported'])
        self.assertTrue(ready['nativeKeyboardVisible'])
        self.assertEqual(512, session.keyboard_service_id)

    async def test_no_system_keyboard_reports_unsupported_and_keeps_typing(self):
        session = await self.session(system_keyboard=False)
        ready = next(event for event in session.ipc.events if event['event'] == 'ready')
        self.assertFalse(ready['nativeKeyboardSupported'])
        self.assertFalse(ready['nativeKeyboardVisible'])
        self.assertFalse(await session.set_native_keyboard_visible(True))
        self.assertFalse(await session.set_native_keyboard_visible(False))
        await session._send_keyboard_report([4])
        self.assertEqual((session.hid.created, [4]), session.hid.keys[-1])

    async def test_ack_is_correlated_and_reports_actual_applied_visibility(self):
        session = await self.session()
        await session._native_keyboard_request({'schema': bridge.MESSAGE_SCHEMA,
            'kind': 'native_keyboard_visibility', 'requestId': 'request1', 'visible': False})
        event = session.ipc.events[-1]
        self.assertEqual('native_keyboard_result', event['event'])
        self.assertEqual('request1', event['requestId'])
        self.assertEqual(session._generation, event['generation'])
        self.assertTrue(event['success'])
        self.assertFalse(event['visible'])
        with self.assertRaises(ValueError):
            await session._native_keyboard_request({'schema': bridge.MESSAGE_SCHEMA,
                'requestId': 'request2', 'visible': 1})

    async def test_failed_final_fence_marks_route_uncertain(self):
        session = await self.session()
        session._ping_hid = AsyncMock(side_effect=ConnectionResetError('lost after mutation'))
        await session._native_keyboard_request({'schema': bridge.MESSAGE_SCHEMA,
            'kind': 'native_keyboard_visibility', 'requestId': 'request1', 'visible': False})
        event = session.ipc.events[-1]
        self.assertFalse(event['success'])
        self.assertTrue(event['uncertain'])
        self.assertEqual('ConnectionResetError', event['code'])

    async def test_visibility_waiter_does_not_mutate_a_replacement_generation(self):
        session = await self.session()
        await session._keyboard_lock.acquire()
        pending = asyncio.create_task(session.set_native_keyboard_visible(False))
        await asyncio.sleep(0)
        session._generation += 1
        session._keyboard_lock.release()
        self.assertFalse(await pending)
        self.assertIsNone(session.hid.created)
        self.assertTrue(session.show_native_keyboard)
        old_generation = session._generation - 1
        await session._native_keyboard_request({'schema': bridge.MESSAGE_SCHEMA,
            'requestId': 'oldrequest', 'visible': False}, old_generation)
        self.assertFalse(session.ipc.events[-1]['success'])
        self.assertEqual(old_generation, session.ipc.events[-1]['generation'])
        self.assertIsNone(session.hid.created)

    async def test_refresh_keeps_native_keyboard_visible(self):
        from unittest.mock import patch
        session = await self.session()
        fresh = VisibilityHid()
        with patch.object(bridge, 'UniversalHIDServiceService', return_value=fresh):
            self.assertTrue(await session._refresh_direct_hid(idle_only=True))
        self.assertIs(fresh, session.hid)
        self.assertIsNone(fresh.created)
        self.assertEqual(512, session.keyboard_service_id)
        await session._send_keyboard_report([4])
        self.assertEqual((512, [4]), fresh.keys[-1])

    async def test_idle_refresh_rejects_hidden_mode_registration_fallback(self):
        from unittest.mock import patch
        session = await self.session(visible=False)
        old = session.hid
        old_id = session.keyboard_service_id
        old.__aexit__ = AsyncMock()
        generation = session._generation
        fresh = VisibilityHid()
        fresh.create_keyboard_service = AsyncMock(side_effect=NotImplementedError('attach refused'))
        fresh.__aexit__ = AsyncMock()
        with patch.object(bridge, 'UniversalHIDServiceService', return_value=fresh):
            self.assertFalse(await session._refresh_direct_hid(idle_only=True),
                             'A healthy hidden route must not adopt a visible 512 fallback.')
        self.assertIs(old, session.hid)
        self.assertEqual(old_id, session.keyboard_service_id)
        self.assertFalse(session.show_native_keyboard)
        self.assertTrue(session._session_ready.is_set())
        self.assertEqual(generation, session._generation)
        old.__aexit__.assert_not_awaited()
        fresh.__aexit__.assert_awaited_once()
        await session._send_keyboard_report([4])
        self.assertEqual((old_id, [4]), old.keys[-1])
        self.assertFalse(any(event.get('code') in ('direct_hid_refreshed', 'recovery_triggered')
                             for event in session.ipc.events))

    async def test_idle_refresh_rejects_shown_mode_without_system_surface(self):
        from unittest.mock import patch
        session = await self.session()
        old = session.hid
        old.__aexit__ = AsyncMock()
        generation = session._generation
        fresh = VisibilityHid(system_keyboard=False)
        fresh.create_keyboard_service = AsyncMock(wraps=fresh.create_keyboard_service)
        async def close_fresh(*_):
            await fresh.remove_keyboard_service()
        fresh.__aexit__ = AsyncMock(side_effect=close_fresh)
        with patch.object(bridge, 'UniversalHIDServiceService', return_value=fresh):
            self.assertFalse(await session._refresh_direct_hid(idle_only=True),
                             'A healthy shown route must not adopt an external keyboard.')
        self.assertIs(old, session.hid)
        self.assertEqual(512, session.keyboard_service_id)
        self.assertTrue(session.show_native_keyboard)
        self.assertTrue(session._session_ready.is_set())
        self.assertEqual(generation, session._generation)
        old.__aexit__.assert_not_awaited()
        fresh.__aexit__.assert_awaited_once()
        self.assertIsNone(fresh.created)
        self.assertEqual([], fresh.removed)
        fresh.create_keyboard_service.assert_not_awaited()
        await session._send_keyboard_report([4])
        self.assertEqual((512, [4]), old.keys[-1])
        self.assertFalse(any(event.get('code') in ('direct_hid_refreshed', 'recovery_triggered')
                             for event in session.ipc.events))

    async def test_fault_refresh_can_fall_back_and_reports_actual_mode_in_ready(self):
        from unittest.mock import patch
        for visible in (True, False):
            with self.subTest(visible=visible):
                session = await self.session(visible=visible)
                old = session.hid
                old.__aexit__ = AsyncMock()
                fresh = VisibilityHid(system_keyboard=not visible)
                if not visible:
                    fresh.create_keyboard_service = AsyncMock(side_effect=NotImplementedError('attach refused'))
                with patch.object(bridge, 'UniversalHIDServiceService', return_value=fresh):
                    self.assertTrue(await session._refresh_direct_hid(idle_only=False))
                self.assertIs(fresh, session.hid)
                old.__aexit__.assert_awaited_once()
                await session._emit_ready(probe_sender=False)
                ready = session.ipc.events[-1]
                self.assertEqual('ready', ready['event'])
                self.assertFalse(ready['nativeKeyboardSupported'])
                self.assertEqual(not visible, ready['nativeKeyboardVisible'])


if __name__ == '__main__':
    unittest.main()
