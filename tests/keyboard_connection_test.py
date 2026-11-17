"""Register a hardware-keyboard usage without waiting for the first user key."""
import asyncio
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge
from usb_recovery_test import Hid, Ipc


class UnregisteredKeyboardHid(Hid):
    def __init__(self, service_id=0x100003001):
        super().__init__()
        self.create_keyboard_service = AsyncMock(return_value=service_id)
        self.__aexit__ = AsyncMock()

    async def list_connected_services(self):
        inventory = await super().list_connected_services()
        inventory['services'] = [item for item in inventory['services']
                                 if item['_ServiceID'] != bridge.KEYBOARD_SURFACE_CONNECTED]
        return inventory


class KeyboardConnectionTests(unittest.IsolatedAsyncioTestCase):
    def session(self, hid=None, transport='usb', auth_mode='direct'):
        # These regressions cover the explicit hidden/native-keyboard-off
        # mode, which still registers a real external keyboard before ready.
        session = bridge.TouchSession(Ipc(), 120, 'test-device', transport=transport,
                                      show_native_keyboard=False)
        session.hid = hid if hid is not None else UnregisteredKeyboardHid()
        session.auth_mode = auth_mode
        session.gate_open = True
        session._owns_hid = True
        return session

    async def test_connection_prepares_keyboard_without_a_user_key_on_all_paths(self):
        for transport in ('usb', 'wireless'):
            for auth_mode in ('direct', 'mediastream'):
                with self.subTest(transport=transport, auth_mode=auth_mode):
                    session = self.session(transport=transport, auth_mode=auth_mode)
                    hid = session.hid
                    await session._verify_touch_surface(hid)
                    self.assertIsNone(session.keyboard_service_id)
                    await session._emit_ready()
                    service_id = hid.create_keyboard_service.return_value
                    hid.create_keyboard_service.assert_awaited_once()
                    self.assertEqual([(service_id, [])], hid.keys)
                    self.assertEqual(service_id, session.keyboard_service_id)
                    self.assertTrue(session._session_ready.is_set())
                    events = session.ipc.events
                    keyboard_index = next(i for i, e in enumerate(events)
                                          if e.get('code') == 'keyboard_service_ready')
                    ready_index = next(i for i, e in enumerate(events) if e['event'] == 'ready')
                    self.assertLess(keyboard_index, ready_index)
                    self.assertEqual(str(service_id), events[keyboard_index]['message'])
                    # The real first key uses the confirmed ID without creating
                    # another keyboard or replaying a character during startup.
                    await session._send_keyboard_report([4])
                    hid.create_keyboard_service.assert_awaited_once()
                    self.assertEqual([(service_id, []), (service_id, [4])], hid.keys)
                    self.assertEqual(1, hid.inventory_reads)

    async def test_device_owned_512_still_registers_an_external_keyboard_before_ready(self):
        for transport in ('usb', 'wireless'):
            with self.subTest(transport=transport):
                hid = Hid()
                hid.create_keyboard_service = AsyncMock(return_value=0x100002001)
                session = self.session(hid, transport=transport)
                await session._verify_touch_surface(hid)
                self.assertEqual(512, session.keyboard_service_id)
                await session._emit_ready()
                hid.create_keyboard_service.assert_awaited_once()
                self.assertEqual([(0x100002001, [])], hid.keys)
                self.assertEqual(0x100002001, session.keyboard_service_id)
                await session._send_keyboard_report([4])
                hid.create_keyboard_service.assert_awaited_once()
                self.assertEqual((0x100002001, [4]), hid.keys[-1])
                self.assertEqual(1, hid.inventory_reads)

    async def test_rejected_external_attach_keeps_512_input_without_registering_on_every_key(self):
        hid = Hid()
        hid.create_keyboard_service = AsyncMock(side_effect=NotImplementedError('unsupported'))
        session = self.session(hid)
        await session._verify_touch_surface(hid)
        await session._emit_ready()
        await session._send_keyboard_report([4])
        await session._send_keyboard_report([])
        hid.create_keyboard_service.assert_awaited_once()
        self.assertEqual([(512, []), (512, [4]), (512, [])], hid.keys)
        self.assertTrue(any(e.get('code') == 'keyboard_service_unavailable' for e in session.ipc.events))
        self.assertFalse(any(e.get('code') == 'keyboard_service_ready' for e in session.ipc.events))

    async def test_idle_rotation_verifies_successor_before_its_previous_owner_closes(self):
        for transport in ('usb', 'wireless'):
            for auth_mode in ('direct', 'mediastream'):
                with self.subTest(transport=transport, auth_mode=auth_mode):
                    session = self.session(transport=transport, auth_mode=auth_mode)
                    await session._verify_touch_surface(session.hid)
                    await session._emit_ready()
                    old = session.hid
                    new = UnregisteredKeyboardHid(0x100003002)

                    async def register(**_):
                        old.__aexit__.assert_not_awaited()
                        return 0x100003002

                    async def close_old(*_):
                        self.assertIs(new, session.hid)
                        self.assertEqual(0x100003002, session.keyboard_service_id)
                        self.assertEqual([(0x100003002, [])], new.keys)

                    old.__aexit__.side_effect = close_old
                    new.create_keyboard_service.side_effect = register
                    with patch.object(bridge, 'UniversalHIDServiceService', return_value=new):
                        self.assertTrue(await session._refresh_direct_hid(idle_only=True))
                    self.assertIs(new, session.hid)
                    new.create_keyboard_service.assert_awaited_once()
                    self.assertEqual([(0x100003002, [])], new.keys)
                    self.assertEqual(1, session._generation)
                    self.assertTrue(session._session_ready.is_set())
                    old.__aexit__.assert_awaited_once()

    async def test_failed_candidate_retains_the_live_old_keyboard(self):
        session = self.session()
        await session._emit_ready()
        old, old_id = session.hid, session.keyboard_service_id
        new = UnregisteredKeyboardHid()
        new.create_keyboard_service.side_effect = ConnectionResetError('candidate closed')
        with patch.object(bridge, 'UniversalHIDServiceService', return_value=new):
            with self.assertRaises(ConnectionResetError):
                await session._refresh_direct_hid(idle_only=True)
        old.__aexit__.assert_not_awaited()
        new.__aexit__.assert_awaited_once()
        self.assertIs(old, session.hid)
        self.assertEqual(old_id, session.keyboard_service_id)
        self.assertTrue(session._session_ready.is_set())
        await session._send_keyboard_report([4])
        self.assertEqual((old_id, [4]), old.keys[-1])

    async def test_cancelled_candidate_attach_preserves_the_old_keyboard(self):
        session = self.session()
        await session._emit_ready()
        old, old_id = session.hid, session.keyboard_service_id
        new = UnregisteredKeyboardHid()
        started = asyncio.Event()

        async def register(**_):
            started.set()
            await asyncio.Event().wait()

        new.create_keyboard_service.side_effect = register
        with patch.object(bridge, 'UniversalHIDServiceService', return_value=new):
            task = asyncio.create_task(session._refresh_direct_hid(idle_only=True))
            await asyncio.wait_for(started.wait(), .5)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        old.__aexit__.assert_not_awaited()
        new.__aexit__.assert_awaited_once()
        self.assertIs(old, session.hid)
        self.assertEqual(old_id, session.keyboard_service_id)
        self.assertTrue(session._session_ready.is_set())

    async def test_cancel_during_old_close_retains_new_owner_for_cleanup(self):
        session = self.session()
        await session._emit_ready()
        old = session.hid
        new = UnregisteredKeyboardHid(0x100003002)
        closing = asyncio.Event()

        async def close_old(*_):
            closing.set()
            await asyncio.Event().wait()

        old.__aexit__.side_effect = close_old
        with patch.object(bridge, 'UniversalHIDServiceService', return_value=new):
            task = asyncio.create_task(session._refresh_direct_hid(idle_only=True))
            await asyncio.wait_for(closing.wait(), .5)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertIs(new, session.hid)
        self.assertEqual(0x100003002, session.keyboard_service_id)
        self.assertTrue(session._owns_hid)
        await session._cleanup()
        new.__aexit__.assert_awaited_once()

    async def test_fault_repair_restores_keyboard_before_publishing_new_ready(self):
        session = self.session()
        await session._verify_touch_surface(session.hid)
        await session._emit_ready()
        old = session.hid
        new = UnregisteredKeyboardHid(0x100003002)
        with patch.object(bridge, 'UniversalHIDServiceService', return_value=new):
            self.assertTrue(await session._repair_hid(ConnectionResetError('expired HID'), old))
        self.assertEqual(2, session._generation)
        self.assertEqual(0x100003002, session.keyboard_service_id)
        new.create_keyboard_service.assert_awaited_once()
        self.assertTrue(new.keys)
        self.assertTrue(all(service == 0x100003002 and usages == [] for service, usages in new.keys))
        self.assertTrue(session._session_ready.is_set())

    async def test_registration_rejection_retains_touch_and_retries_on_real_input(self):
        session = self.session()
        hid = session.hid
        service_id = 0x100003001
        hid.create_keyboard_service.side_effect = [NotImplementedError('unsupported'), service_id]
        await session._verify_touch_surface(hid)
        await session._emit_ready()
        self.assertTrue(session._session_ready.is_set())
        self.assertIsNone(session.keyboard_service_id)
        self.assertEqual(bridge.MAX_SLOTS, len(hid.reports))
        self.assertEqual([], hid.keys)
        self.assertTrue(any(e.get('code') == 'keyboard_service_unavailable' for e in session.ipc.events))
        self.assertFalse(any(e.get('code') == 'keyboard_service_ready' for e in session.ipc.events))
        await session._send_keyboard_report([4])
        self.assertEqual(2, hid.create_keyboard_service.await_count)
        self.assertEqual([(service_id, [4])], hid.keys)

    async def test_registration_transport_failure_cannot_publish_ready(self):
        for failure in (ConnectionResetError('closed'), EOFError('closed'),
                        bridge.StreamClosedError('closed'), TimeoutError('timed out')):
            with self.subTest(failure=type(failure).__name__):
                session = self.session()
                session.hid.create_keyboard_service.side_effect = failure
                await session._verify_touch_surface(session.hid)
                with self.assertRaises(type(failure)):
                    await session._emit_ready()
                self.assertFalse(session._session_ready.is_set())
                self.assertEqual(0, session._generation)
                self.assertFalse(any(e['event'] == 'ready' or e.get('code') == 'keyboard_service_unavailable'
                                     for e in session.ipc.events))

    async def test_stalled_registration_is_cancelled_without_claiming_ready(self):
        session = self.session()
        cancelled = asyncio.Event()

        async def register(**_):
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()

        session.hid.create_keyboard_service.side_effect = register
        with patch.object(bridge, 'HID_OPERATION_TIMEOUT_SECONDS', .01):
            with self.assertRaises(TimeoutError):
                await asyncio.wait_for(session._emit_ready(), .3)
        self.assertTrue(cancelled.is_set())
        self.assertFalse(session._session_ready.is_set())
        self.assertFalse(any(e['event'] == 'ready' for e in session.ipc.events))

    async def test_connection_cancellation_during_registration_never_claims_ready(self):
        session = self.session()
        started = asyncio.Event()

        async def register(**_):
            started.set()
            await asyncio.Event().wait()

        session.hid.create_keyboard_service.side_effect = register
        task = asyncio.create_task(session._emit_ready())
        await asyncio.wait_for(started.wait(), .3)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertIsNone(session.keyboard_service_id)
        self.assertFalse(any(e['event'] == 'ready' for e in session.ipc.events))
        self.assertFalse(session._hid_lifecycle_lock.locked())

    async def test_keyboard_sender_failure_never_announces_keyboard_or_session_ready(self):
        session = self.session()
        session.hid.send_keyboard = AsyncMock(side_effect=ConnectionResetError('closed'))
        with self.assertRaises(ConnectionResetError):
            await session._emit_ready()
        self.assertIsNotNone(session.keyboard_service_id)  # retained for cleanup
        self.assertFalse(any(e['event'] == 'ready' or e.get('code') == 'keyboard_service_ready'
                             for e in session.ipc.events))

    async def test_first_key_sender_failure_does_not_announce_keyboard_ready(self):
        session = self.session()
        session.hid.send_keyboard = AsyncMock(side_effect=ConnectionResetError('closed'))
        with self.assertRaises(ConnectionResetError):
            await session._send_keyboard_report([4])
        self.assertFalse(any(e.get('code') == 'keyboard_service_ready' for e in session.ipc.events))

    async def test_failed_same_connection_fence_never_announces_keyboard_ready(self):
        session = self.session()
        session._ping_hid = AsyncMock(side_effect=ConnectionResetError('no ACK'))
        with self.assertRaises(ConnectionResetError):
            await session._emit_ready()
        self.assertEqual([(session.keyboard_service_id, [])], session.hid.keys)
        self.assertFalse(any(e['event'] == 'ready' or e.get('code') == 'keyboard_service_ready'
                             for e in session.ipc.events))

    async def test_failed_registration_during_repair_keeps_the_session_recovering(self):
        session = self.session()
        await session._verify_touch_surface(session.hid)
        await session._emit_ready()
        new = UnregisteredKeyboardHid()
        new.create_keyboard_service.side_effect = ConnectionResetError('closed')
        with patch.object(bridge, 'UniversalHIDServiceService', return_value=new):
            self.assertFalse(await session._repair_hid(ConnectionResetError('expired'), session.hid))
        new.__aexit__.assert_awaited_once()
        self.assertFalse(session._session_ready.is_set())
        self.assertTrue(session._recovering)
        self.assertEqual(1, session._generation)

    async def test_cleanup_releases_and_closes_eager_keyboard_without_user_typing(self):
        session = self.session()
        hid = session.hid
        await session._verify_touch_surface(hid)
        await session._emit_ready()
        service_id = session.keyboard_service_id
        await session._cleanup()
        self.assertEqual([(service_id, []), (service_id, [])], hid.keys)
        hid.__aexit__.assert_awaited_once()
        self.assertIsNone(session.keyboard_service_id)
        self.assertEqual(frozenset(), session._held_keyboard_usages)

    async def test_first_key_racing_connection_cannot_register_duplicate_keyboard(self):
        session = self.session()
        started, release = asyncio.Event(), asyncio.Event()

        async def register(**_):
            started.set()
            await release.wait()
            return 0x100003001

        session.hid.create_keyboard_service.side_effect = register
        connection = asyncio.create_task(session._emit_ready())
        await asyncio.wait_for(started.wait(), .3)
        key = asyncio.create_task(session._send_keyboard_report([4]))
        release.set()
        await asyncio.wait_for(asyncio.gather(connection, key), .3)
        session.hid.create_keyboard_service.assert_awaited_once()
        self.assertEqual([(0x100003001, []), (0x100003001, [4])], session.hid.keys)

    async def test_invalid_keyboard_id_is_never_announced_as_available(self):
        for service_id in (None, True, 0, -1, 1 << 64):
            with self.subTest(service_id=service_id):
                session = self.session()
                session.hid.create_keyboard_service.return_value = service_id
                await session._emit_ready()
                self.assertIsNone(session.keyboard_service_id)
                self.assertEqual([], session.hid.keys)
                self.assertTrue(session._session_ready.is_set())  # touch still works
                self.assertFalse(any(e.get('code') == 'keyboard_service_ready' for e in session.ipc.events))


if __name__ == '__main__':
    unittest.main()
