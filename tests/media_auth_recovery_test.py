"""Exercise the real media/HID owner with a live HID and failed media input."""
import asyncio
import sys
import time
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge
from usb_recovery_test import Hid, Ipc


class Receiver:
    port = 1

    def __init__(self, *, first_packet=True):
        self.first_packet = first_packet
        self.failure = asyncio.Event()
        self.close = Mock()

    async def recv(self):
        if self.first_packet:
            self.first_packet = False
            return b'media-packet'
        await self.failure.wait()
        raise OSError('injected media failure; HID remains healthy')


class MediaAuthRecoveryTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.displays = []
        self.receivers = []
        self.hids = []
        self.rsd = SimpleNamespace(service=SimpleNamespace(address=('phone', 1)))
        self.enterContext(patch.object(bridge, 'DisplayService', side_effect=self.display))
        self.enterContext(patch.object(bridge, 'UniversalHIDServiceService', side_effect=self.hid))
        self.enterContext(patch('pymobiledevice3.remote.core_device.screen_stream.open_media_receiver',
                                side_effect=self.receiver))
        self.enterContext(patch.object(bridge, 'MEDIA_AUTH_START_TIMEOUT_SECONDS', .03))

    def display(self, _):
        display = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
            start_video_stream=AsyncMock(return_value={'connection': {'options': {
                'avcMediaStreamOptionClientSessionID': {'uuid': uuid.uuid4()}}}}),
            stop_media_stream=AsyncMock())
        self.displays.append(display)
        return display

    def receiver(self, *_):
        receiver = Receiver()
        self.receivers.append(receiver)
        return receiver, 'host'

    def hid(self, *_):
        hid = Hid()
        hid.__aexit__ = AsyncMock()
        self.hids.append(hid)
        return hid

    def session(self, hid):
        session = bridge.TouchSession(Ipc(), 120, 'test-device')
        session.hid = hid
        session._media_health = hid._bridge_media_health
        session.auth_mode = 'mediastream'
        session.gate_open = True
        session.keyboard_service_id = 512
        session.hid._bridge_keyboard_attach_attempted = True
        session.rsd = self.rsd
        return session

    async def test_no_first_packet_cannot_open_hid_or_report_ready(self):
        receiver = Receiver(first_packet=False)
        with patch('pymobiledevice3.remote.core_device.screen_stream.open_media_receiver',
                   return_value=(receiver, 'host')):
            with self.assertRaises(bridge.MediaAuthenticationError):
                async with bridge.touch_session(self.rsd):
                    self.fail('authentication without media must not yield HID')
        self.assertEqual([], self.hids)
        receiver.close.assert_called_once()
        self.displays[0].stop_media_stream.assert_awaited_once()
        self.displays[0].__aexit__.assert_awaited_once()

    async def test_receiver_error_prevents_ping_ready_and_hid_only_repair(self):
        async with bridge.touch_session(self.rsd) as hid:
            session = self.session(hid)
            await session._emit_ready()
            session._input_verified = True
            self.receivers[0].failure.set()
            await session._media_health.reader_task
            with self.assertRaises(bridge.MediaAuthenticationError):
                await session._ping_hid()
            with self.assertRaises(bridge.MediaAuthenticationError):
                await session._emit_ready()
            with patch.object(session, '_refresh_direct_hid', AsyncMock()) as refresh:
                self.assertFalse(await session._repair_hid(ConnectionResetError(), hid))
            refresh.assert_not_awaited()
            self.assertFalse(session.gate_open)
            self.assertFalse(session._session_ready.is_set())
            self.assertFalse(session._input_verified)
            self.assertTrue(session._recovering)
            self.assertEqual(1, session._generation)

    async def test_silent_media_stops_idle_sender_even_while_hid_can_ping(self):
        async with bridge.touch_session(self.rsd) as hid:
            session = self.session(hid)
            await session._emit_ready()
            await session._ping_hid()
            session._media_health.last_packet_at = time.monotonic() - bridge.MEDIA_AUTH_SILENCE_TIMEOUT_SECONDS
            session._serve_task = Mock()
            with patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .001), \
                 patch.object(session, '_refresh_direct_hid', AsyncMock()) as refresh:
                await asyncio.wait_for(session._request_direct_hid_rotation(), .3)
            refresh.assert_not_awaited()
            session._serve_task.cancel.assert_called_once()
            self.assertTrue(session._hid_transport_failed)
            self.assertFalse(session.gate_open)
            self.assertFalse(session._session_ready.is_set())

    async def test_ended_or_cancelled_receiver_cannot_remain_authenticated(self):
        for cancelled in (False, True):
            with self.subTest(cancelled=cancelled):
                async with bridge.touch_session(self.rsd) as hid:
                    session = self.session(hid)
                    await session._emit_ready()
                    reader = session._media_health.reader_task
                    reader.cancel()
                    await asyncio.gather(reader, return_exceptions=True)
                    if not cancelled:
                        # Also cover a receiver that returned normally.
                        finished = asyncio.get_running_loop().create_future()
                        finished.set_result(None)
                        session._media_health.reader_task = finished
                    with self.assertRaises(bridge.MediaAuthenticationError):
                        await session._send_keyboard_report([4])
                    self.assertFalse(session.gate_open)
                    self.assertFalse(session._session_ready.is_set())

    async def test_media_failure_during_send_cannot_publish_input_verified(self):
        async with bridge.touch_session(self.rsd) as hid:
            session = self.session(hid)
            await session._emit_ready()
            async def frames():
                yield {'kind': 'keyboard_batch', 'generation': 1, 'seq': 1}
            async def send(*_):
                self.receivers[0].failure.set()
                await session._media_health.reader_task
            session.ipc.read_messages = frames
            hid.send_keyboard = send
            with patch.object(bridge, 'decode_keyboard_batch', return_value=(1, None, [4])), \
                 patch.object(session, '_poll_device_pasteboard', AsyncMock()), \
                 patch.object(session, '_request_direct_hid_rotation', AsyncMock()):
                await session._serve()
            self.assertFalse(any(event.get('code') == 'input_verified' for event in session.ipc.events))
            self.assertFalse(session._session_ready.is_set())
            self.assertTrue(session._recovering)
            self.assertEqual(1, session._generation)

    async def test_missing_media_before_first_ready_does_not_fall_back_to_direct_hid(self):
        session = bridge.TouchSession(Ipc(), 120, 'test-device')
        dial, rsd, result = self.tunnel_types()
        receiver = Receiver(first_packet=False)
        with patch('pymobiledevice3.remote.userspace_tunnel.UserspaceDialPlane', dial), \
             patch.object(bridge, 'RemoteServiceDiscoveryService', rsd), \
             patch('pymobiledevice3.remote.core_device.screen_stream.open_media_receiver',
                   return_value=(receiver, 'host')), \
             patch.object(session, '_initialize_touch_with_retry', AsyncMock()) as fallback:
            with self.assertRaises(bridge.MediaAuthenticationError):
                await session._connect_with_tunnel_result(result)
        fallback.assert_not_awaited()
        self.assertFalse(any(event.get('event') == 'ready' for event in session.ipc.events))
        receiver.close.assert_called_once()

    async def test_empty_media_receive_is_a_failure_rather_than_a_busy_loop(self):
        receiver = SimpleNamespace(port=1, recv=AsyncMock(return_value=b''), close=Mock())
        with patch('pymobiledevice3.remote.core_device.screen_stream.open_media_receiver',
                   return_value=(receiver, 'host')):
            with self.assertRaises(bridge.MediaAuthenticationError):
                async with bridge.touch_session(self.rsd):
                    self.fail('empty media input cannot authenticate HID')
        receiver.recv.assert_awaited_once()
        receiver.close.assert_called_once()
        self.assertEqual([], self.hids)

    async def test_cancellation_during_media_restart_closes_both_sessions_and_preserves_mux(self):
        session = bridge.TouchSession(Ipc(), 120, 'test-device')
        session._remote_pairing_provision_attempted = True
        session._preflight_developer_environment = AsyncMock()
        mux = session._usb_mux_transport = Mock(failure_reason=None)
        fresh = SimpleNamespace(udid='test-device', close=AsyncMock())
        session._reconnect_lockdown = AsyncMock(return_value=fresh)
        dial, rsd, result = self.tunnel_types()
        restarting = asyncio.Event()
        original_receiver = self.receiver
        def receiver(*args):
            opened, host = original_receiver(*args)
            if len(self.receivers) == 2:
                opened.first_packet = False
                restarting.set()
            return opened, host
        async def tunnel(_):
            await session._connect_with_tunnel_result(result, preserve_capture_mux=True)
        session._run_tunnel_attempt = tunnel
        async def frames():
            self.receivers[0].failure.set()
            await asyncio.Event().wait()
            if False:
                yield {}
        session.ipc.read_messages = frames
        with patch('pymobiledevice3.remote.userspace_tunnel.UserspaceDialPlane', dial), \
             patch.object(bridge, 'RemoteServiceDiscoveryService', rsd), \
             patch('pymobiledevice3.remote.core_device.screen_stream.open_media_receiver', side_effect=receiver), \
             patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .001), \
             patch.object(session, '_poll_device_pasteboard', AsyncMock()):
            owner = asyncio.create_task(session._connect_with_lockdown(fresh))
            try:
                await asyncio.wait_for(restarting.wait(), 2)
                owner.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await owner
            finally:
                owner.cancel()
                await asyncio.gather(owner, return_exceptions=True)
        self.assertEqual(2, len(self.receivers))
        for receiver, display in zip(self.receivers, self.displays):
            receiver.close.assert_called_once()
            display.__aexit__.assert_awaited_once()
        self.assertEqual(1, sum(e.get('event') == 'ready' for e in session.ipc.events))
        self.assertFalse(any(e.get('code') == 'recovery_completed' for e in session.ipc.events))
        self.assertIsNone(session._media_health)
        self.assertIsNone(session.hid)
        mux.close.assert_not_called()

    def tunnel_types(self):
        class Dial:
            dial = AsyncMock()
            def __init__(self, *_): pass
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
        class Rsd:
            udid = 'test-device'
            service = SimpleNamespace(address=('phone', 1))
            def __init__(self, *_, **kwargs): pass
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
        result = SimpleNamespace(client=SimpleNamespace(tun=SimpleNamespace(set_peer=Mock())),
                                 address='phone', port=1)
        return Dial, Rsd, result

    async def test_transport_owner_restarts_media_then_accepts_only_new_generation_input(self):
        for rotate_first in (False, True):
            with self.subTest(rotate_first=rotate_first):
                self.receivers.clear()
                self.displays.clear()
                self.hids.clear()
                session = bridge.TouchSession(Ipc(), 120, 'test-device')
                session._remote_pairing_provision_attempted = True
                session._ddi_uses_cryptex = True
                session._preflight_developer_environment = AsyncMock()
                mux = session._usb_mux_transport = Mock(failure_reason=None)
                fresh = SimpleNamespace(udid='test-device', close=AsyncMock())
                async def reconnect():
                    self.assertFalse(session._session_ready.is_set())
                    self.assertFalse(session.gate_open)
                    self.assertFalse(session._input_verified)
                    return fresh
                session._reconnect_lockdown = AsyncMock(side_effect=reconnect)
                dial, rsd, result = self.tunnel_types()
                async def tunnel(_):
                    await session._connect_with_tunnel_result(result, preserve_capture_mux=True)
                session._run_tunnel_attempt = tunnel
                async def frames():
                    if len(self.receivers) == 1:
                        if rotate_first:
                            await session._refresh_direct_hid()
                        self.receivers[0].failure.set()
                        await asyncio.Event().wait()  # idle stdin must be interrupted
                    yield {'kind': 'keyboard_batch', 'generation': 1, 'seq': 1}
                    yield {'kind': 'keyboard_batch', 'generation': 2, 'seq': 2}
                session.ipc.read_messages = frames
                with patch('pymobiledevice3.remote.userspace_tunnel.UserspaceDialPlane', dial), \
                     patch.object(bridge, 'RemoteServiceDiscoveryService', rsd), \
                     patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .001), \
                     patch.object(session, '_poll_device_pasteboard', AsyncMock()), \
                     patch.object(bridge, 'decode_keyboard_batch', return_value=(1, None, [4])):
                    await asyncio.wait_for(session._connect_with_lockdown(fresh), 3)
                session._reconnect_lockdown.assert_awaited_once()
                self.assertEqual(2, len(self.receivers))
                for display, receiver in zip(self.displays, self.receivers):
                    display.start_video_stream.assert_awaited_once()
                    display.stop_media_stream.assert_awaited_once()
                    display.__aexit__.assert_awaited_once()
                    receiver.close.assert_called_once()
                self.assertEqual([1, 2], [e['generation'] for e in session.ipc.events if e.get('event') == 'ready'])
                self.assertEqual(1, sum(e.get('code') == 'input_verified' for e in session.ipc.events))
                self.assertEqual(1, sum(e.get('code') == 'stale_input_dropped' for e in session.ipc.events))
                self.assertEqual(1, sum(e.get('code') == 'recovery_completed' for e in session.ipc.events))
                self.assertIn((512, [4]), self.hids[-1].keys)
                mux.close.assert_not_called()


if __name__ == '__main__':
    unittest.main()
