"""Cryptex media-auth HID must share the wired session's lease recovery."""
import asyncio
import contextlib
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge
from usb_recovery_test import Hid, Ipc


class CryptexControlRecoveryTests(unittest.IsolatedAsyncioTestCase):
    def session(self):
        session = bridge.TouchSession(Ipc(), 120, 'test-device')
        session._ddi_uses_cryptex = True
        session.hid = Hid()
        session.auth_mode = 'mediastream'
        session.gate_open = True
        return session

    async def tick(self, session, rotation_seconds=0):
        real_sleep = asyncio.sleep
        ticks = 0

        async def sleep(_):
            nonlocal ticks
            ticks += 1
            if ticks > 1:
                raise asyncio.CancelledError
            await real_sleep(0)

        with patch.object(bridge.asyncio, 'sleep', sleep), \
             patch.object(bridge, 'DIRECT_HID_ROTATION_SECONDS', rotation_seconds):
            with self.assertRaises(asyncio.CancelledError):
                await session._request_direct_hid_rotation()

    async def test_media_auth_rotates_hid_without_rebuilding_gate_tunnel_or_image(self):
        session = self.session()
        old = session.hid
        session.rsd = rsd = object()
        session._usb_mux_transport = mux = Mock(failure_reason=None)
        with patch.object(bridge, 'UniversalHIDServiceService', side_effect=Hid) as factory, \
             patch.object(session, '_run_tunnel_attempt', AsyncMock()) as tunnel, \
             patch.object(session, '_preflight_developer_environment', AsyncMock()) as image, \
             patch.object(session, '_open_gate', AsyncMock()) as gate:
            await self.tick(session)
        factory.assert_called_once_with(rsd)
        self.assertIsNot(old, session.hid)
        self.assertIs(rsd, session.rsd)
        self.assertIs(mux, session._usb_mux_transport)
        self.assertEqual('mediastream', session.auth_mode)
        self.assertTrue(session.gate_open)
        tunnel.assert_not_awaited()
        image.assert_not_awaited()
        gate.assert_not_awaited()
        await session._send_keyboard_report([4])
        self.assertEqual([(512, [4])], session.hid.keys)

    async def test_media_auth_rotation_defers_held_input_then_refreshes_after_release(self):
        for kind in ('touch', 'keyboard', 'button'):
            with self.subTest(kind=kind):
                session = self.session()
                old = session.hid
                held = (session._active_touch_ids if kind == 'touch' else session._held_buttons)
                if kind == 'keyboard':
                    session._held_keyboard_usages = frozenset({4})
                else:
                    held.add(7 if kind == 'touch' else (12, 64))
                with patch.object(bridge, 'UniversalHIDServiceService', side_effect=Hid) as factory, \
                     patch.object(session, '_ping_hid', wraps=session._ping_hid) as ping:
                    await self.tick(session)
                    factory.assert_not_called()
                    ping.assert_awaited()
                    self.assertIs(old, session.hid)
                    session._held_keyboard_usages = frozenset()
                    held.clear()
                    await self.tick(session)
                    factory.assert_called_once()

    async def test_media_auth_failure_repairs_and_verifies_a_new_input_generation(self):
        session = self.session()
        await session._emit_ready()
        old = session.hid
        with patch.object(bridge, 'UniversalHIDServiceService', side_effect=Hid):
            self.assertTrue(await session._repair_hid(ConnectionResetError('expired HID'), old))
        self.assertIsNot(old, session.hid)
        self.assertEqual(2, session._generation)
        self.assertTrue(session._session_ready.is_set())
        self.assertFalse(session._recovering)
        self.assertEqual('mediastream', session.auth_mode)
        self.assertEqual(bridge.MAX_SLOTS, len(session.hid.reports))
        self.assertEqual([(512, [])], session.hid.keys)

    async def test_media_auth_sender_failure_does_not_replay_the_old_press(self):
        session = self.session()
        await session._emit_ready()
        session._input_verified = True

        async def frames():
            yield {'kind': 'keyboard_batch', 'generation': 1, 'seq': 1}

        session.ipc.read_messages = frames
        session._apply_keyboard = AsyncMock(side_effect=ConnectionResetError('expired HID'))
        with patch.object(bridge, 'decode_keyboard_batch', return_value=(1, None, [4])), \
             patch.object(bridge, 'UniversalHIDServiceService', side_effect=Hid), \
             patch.object(session, '_request_direct_hid_rotation', AsyncMock()):
            await session._serve()
        self.assertEqual(2, session._generation)
        session._apply_keyboard.assert_awaited_once()
        self.assertTrue(session._session_ready.is_set())

    async def test_idle_media_auth_health_failure_repairs_without_stopping_sender(self):
        session = self.session()
        await session._emit_ready()
        old = session.hid
        old.service._receive_frame = AsyncMock(side_effect=ConnectionResetError('expired HID'))
        session._serve_task = Mock()
        with patch.object(bridge, 'UniversalHIDServiceService', side_effect=Hid):
            await self.tick(session, rotation_seconds=3600)
        self.assertIsNot(old, session.hid)
        self.assertEqual(2, session._generation)
        self.assertTrue(session._session_ready.is_set())
        session._serve_task.cancel.assert_not_called()

    async def test_failed_media_auth_repair_leaves_transport_recovery_pending(self):
        session = self.session()
        await session._emit_ready()
        with patch.object(session, '_refresh_direct_hid', AsyncMock(side_effect=ConnectionResetError())), \
             patch.object(session, '_emit_ready', AsyncMock()) as ready:
            self.assertFalse(await session._repair_hid(ConnectionResetError(), session.hid))
        ready.assert_not_awaited()
        self.assertFalse(session._session_ready.is_set())
        self.assertTrue(session._recovering)
        self.assertTrue(session._hid_transport_failed)

    async def test_wireless_media_auth_renews_idle_hid_on_the_same_gate(self):
        session = self.session()
        session.transport_mode = 'wireless'
        with patch.object(session, '_refresh_direct_hid', AsyncMock()) as refresh, \
             patch.object(session, '_ping_hid', wraps=session._ping_hid) as ping:
            await self.tick(session)
        refresh.assert_awaited_once_with(idle_only=True)
        ping.assert_not_awaited()

    async def test_replacement_hid_closes_before_media_gate_on_stop_and_cancellation(self):
        for cancel in (False, True):
            with self.subTest(cancel=cancel):
                session = self.session()
                events = []
                original = Hid()
                replacement = Hid()
                replacement.__aexit__ = AsyncMock(side_effect=lambda *_: events.append('replacement_closed'))

                @contextlib.asynccontextmanager
                async def media(_):
                    try:
                        yield original
                    finally:
                        events.append('gate_closed')

                class Dial:
                    dial = AsyncMock()
                    def __init__(self, *_): pass
                    async def __aenter__(self): return self

                class Rsd:
                    udid = 'test-device'
                    def __init__(self, *_, **kwargs): pass
                    async def __aenter__(self): return self

                async def serve():
                    await session._refresh_direct_hid()
                    if cancel:
                        raise asyncio.CancelledError

                result = SimpleNamespace(client=SimpleNamespace(tun=SimpleNamespace(set_peer=Mock())),
                                         address='phone', port=1)
                with patch('pymobiledevice3.remote.userspace_tunnel.UserspaceDialPlane', Dial), \
                     patch.object(bridge, 'RemoteServiceDiscoveryService', Rsd), \
                     patch.object(bridge, 'touch_session', media), \
                     patch.object(bridge, 'UniversalHIDServiceService', return_value=replacement), \
                     patch.object(session, '_run_serve', serve), \
                     patch.object(session, '_cleanup', AsyncMock()):
                    if cancel:
                        with self.assertRaises(asyncio.CancelledError):
                            await session._connect_with_tunnel_result(result)
                    else:
                        await session._connect_with_tunnel_result(result)
                self.assertEqual(['replacement_closed', 'gate_closed'], events)
                self.assertIsNone(session.hid)
                self.assertFalse(session._owns_hid)


if __name__ == '__main__':
    unittest.main()
