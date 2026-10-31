"""Ordered modifier prefixes and health checks that leave input responsive."""
import asyncio
from pathlib import Path
import sys
import time
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


class ShortcutVerificationTests(unittest.IsolatedAsyncioTestCase):
    def session(self, frames=()):
        self.events = []
        self.ping_holds = []
        async def emit(event):
            self.events.append(('status', event))
        async def messages():
            for frame in frames:
                yield frame
        session = bridge.TouchSession(SimpleNamespace(emit=emit, read_messages=messages), 120, 'shortcut-fixture')
        session.transport_mode = 'wireless'
        session._check_media_auth = lambda: None
        session.keyboard_service_id = 7
        async def button(page, code, state):
            self.events.append(('button', (page, code, state), time.monotonic()))
        async def keyboard(service, usages, timestamp):
            self.events.append(('keyboard', tuple(usages), time.monotonic()))
        async def ping(service=None):
            self.ping_holds.append(set(session._held_buttons))
            self.events.append(('ping',))
        session.indigo = SimpleNamespace(send_button=button)
        session.hid = SimpleNamespace(send_keyboard=keyboard)
        session._ping_hid = AsyncMock(side_effect=ping)
        session._keep_touch_alive = AsyncMock()
        return session

    def button(self, state, button=bridge.GLOBE_BUTTON):
        return dict(schema=bridge.MESSAGE_SCHEMA, kind=bridge.BUTTON_MESSAGE_KIND,
                    seq=1, usagePage=button[0], usageCode=button[1], state=state)

    def keyboard(self, usages):
        return dict(schema=bridge.MESSAGE_SCHEMA, kind=bridge.KEYBOARD_MESSAGE_KIND, seq=1, usages=usages)

    async def serve(self, session):
        with patch.object(session, '_poll_device_pasteboard', AsyncMock()), \
             patch.object(session, '_request_direct_hid_rotation', AsyncMock()):
            await session._serve()

    async def test_verification_does_not_hold_globe_between_letter_up_and_globe_up(self):
        session = self.session([self.button('down'), self.keyboard([0x11]),
                                self.keyboard([]), self.button('up')])
        session._input_verified = True
        await self.serve(session)
        self.assertEqual(self.ping_holds, [{bridge.GLOBE_BUTTON}])
        ping = next(i for i, event in enumerate(self.events) if event[0] == 'ping')
        letter = next(i for i, event in enumerate(self.events) if event[0] == 'keyboard' and event[1])
        self.assertLess(ping, letter, 'Globe acknowledgement must precede the letter')
        self.assertFalse(session._held_buttons)
        self.assertFalse(session._held_keyboard_usages)

    async def test_first_input_verifies_after_home_is_released(self):
        session = self.session([self.button('down', bridge.HOME_BUTTON), self.button('up', bridge.HOME_BUTTON)])
        await self.serve(session)
        self.assertEqual(self.ping_holds, [set()])
        up = next(i for i, event in enumerate(self.events) if event[0] == 'button' and event[1][-1] == 2)
        ping = next(i for i, event in enumerate(self.events) if event[0] == 'ping')
        self.assertLess(up, ping)
        self.assertTrue(session._input_verified)

    async def test_single_channel_chord_cannot_wait_for_a_ping_while_globe_is_down(self):
        session = self.session()
        session._ping_hid = AsyncMock(side_effect=AssertionError('A reply wait split the ordered button chord'))
        await session._apply_button(*bridge.GLOBE_BUTTON, 'down')
        await session._apply_button(7, 0x11, 'down')
        await session._apply_button(7, 0x11, 'up')
        await session._apply_button(*bridge.GLOBE_BUTTON, 'up')
        self.assertEqual([event[1] for event in self.events if event[0] == 'button'],
                         [(*bridge.GLOBE_BUTTON, 1), (7, 0x11, 1), (7, 0x11, 2), (*bridge.GLOBE_BUTTON, 2)])
        self.assertFalse(session._held_buttons)
        session._ping_hid.assert_not_awaited()

    async def test_valid_ping_reply_can_outlive_the_one_way_write_deadline(self):
        session = self.session()
        sent = []
        async def receive():
            await asyncio.sleep(.04)
            return bridge.PingFrame(0, flags=['ACK'], opaque_data=sent[-1][-8:])
        service = SimpleNamespace(_request_lock=asyncio.Lock(),
            writer=SimpleNamespace(write=sent.append, drain=AsyncMock()),
            _receive_frame=receive, _apply_flow_control_frame=lambda _: None)
        peer = SimpleNamespace(service=service)
        with patch.object(bridge, 'HID_OPERATION_TIMEOUT_SECONDS', .02), \
             patch.object(bridge, 'HID_PING_TIMEOUT_SECONDS', .08, create=True):
            await bridge.TouchSession._ping_hid(session, peer)

    async def test_globe_is_acknowledged_on_its_own_channel_before_letter_down(self):
        session = self.session()
        device = {'globe': False}
        async def queued_button(page, code, state):
            self.events.append(('queued_button', state))
        async def acknowledge(service=None):
            self.assertIs(service, session.indigo)
            device['globe'] = True
        async def keyboard(_service, usages, _timestamp):
            self.assertTrue(device['globe'], 'Keyboard reached the peer before its Globe prefix')
        session.indigo.send_button = queued_button
        session.hid.send_keyboard = keyboard
        session._ping_hid = AsyncMock(side_effect=acknowledge)
        await session._apply_button(*bridge.GLOBE_BUTTON, 'down')
        await session._send_keyboard_report([0x06])
        session._ping_hid.assert_awaited_once_with(session.indigo)

    async def test_slow_background_ping_does_not_lock_out_new_button_input(self):
        session = self.session()
        started, finish = asyncio.Event(), asyncio.Event()
        async def slow_ping(_service=None):
            started.set()
            await finish.wait()
        session._ping_hid = AsyncMock(side_effect=slow_ping)
        with patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .001):
            task = asyncio.create_task(session._request_direct_hid_rotation())
            try:
                await started.wait()
                await asyncio.wait_for(session._apply_button(*bridge.HOME_BUTTON, 'down'), .1)
                await asyncio.wait_for(session._apply_button(*bridge.HOME_BUTTON, 'up'), .15)
                self.assertFalse(finish.is_set())
            finally:
                finish.set()
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)

    async def test_ping_budget_includes_waiting_for_another_reader(self):
        session = self.session()
        lock = asyncio.Lock()
        await lock.acquire()
        service = SimpleNamespace(_request_lock=lock)
        session.hid.service = service
        with patch.object(bridge, 'HID_OPERATION_TIMEOUT_SECONDS', .02), \
             patch.object(bridge, 'HID_PING_TIMEOUT_SECONDS', .02, create=True):
            task = asyncio.create_task(bridge.TouchSession._ping_hid(session, session.hid))
            try:
                done, _ = await asyncio.wait([task], timeout=.15)
                self.assertIn(task, done, 'Request-lock queueing escaped the HID operation deadline')
                with self.assertRaises(TimeoutError): await task
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
                lock.release()

    async def test_failed_globe_ack_never_admits_a_letter_and_releases_the_prefix(self):
        session = self.session([self.button('down'), self.keyboard([0x06]), self.keyboard([]), self.button('up')])
        session._ping_hid = AsyncMock(side_effect=TimeoutError('Globe peer did not acknowledge'))
        session.indigo.__aexit__ = AsyncMock()
        session._close_remote_service = AsyncMock()
        await self.serve(session)
        self.assertFalse([event for event in self.events if event[0] == 'keyboard'])
        self.assertIn(bridge.GLOBE_BUTTON, session._held_buttons)
        await session._cleanup()
        buttons = [event[1][-1] for event in self.events if event[0] == 'button']
        self.assertEqual([1, 2], buttons)
        self.assertFalse(session._held_buttons)

    async def test_new_button_connection_clears_unreleased_buttons_from_previous_transport(self):
        session = self.session()
        session.indigo = None
        pressed = {bridge.HOME_BUTTON, bridge.GLOBE_BUTTON}
        events = []
        async def button(page, code, state):
            events.append((page, code, state))
            if state == 2: pressed.discard((page, code))
        indigo = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(), send_button=button)
        with patch.object(bridge, 'IndigoHIDService', return_value=indigo):
            await session._ensure_indigo()
            await session._ensure_indigo()
        self.assertFalse(pressed, 'New transport inherited a system button left pressed by a dead socket')
        self.assertEqual([(*bridge.HOME_BUTTON, 2), (*bridge.GLOBE_BUTTON, 2),
                          (7, 0x04, 2), (7, 0x06, 2), (7, 0x0B, 2), (7, 0x11, 2), (7, 0x16, 2)], events)
        session._ping_hid.assert_awaited_once_with(indigo)
        self.assertIs(indigo, session.indigo)

    async def test_unverified_button_connection_is_closed_before_it_can_accept_presses(self):
        session = self.session()
        session.indigo = None
        indigo = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(), send_button=AsyncMock())
        session._ping_hid = AsyncMock(side_effect=TimeoutError('startup peer'))
        with patch.object(bridge, 'IndigoHIDService', return_value=indigo):
            with self.assertRaises(TimeoutError): await session._ensure_indigo()
        self.assertIsNone(session.indigo)
        indigo.__aexit__.assert_awaited_once()

    async def test_old_health_failure_does_not_close_a_verified_replacement(self):
        session = self.session()
        started, finish = asyncio.Event(), asyncio.Event()
        old = session.hid
        async def old_ping(_service=None):
            if _service is not None and _service is not old:
                await asyncio.Event().wait()
            started.set()
            await finish.wait()
            raise ConnectionResetError('old peer')
        session._ping_hid = AsyncMock(side_effect=old_ping)
        with patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .001):
            task = asyncio.create_task(session._request_direct_hid_rotation())
            try:
                await started.wait()
                session.hid = SimpleNamespace()
                session._generation += 1
                session._session_ready.set()
                finish.set()
                await asyncio.sleep(.001)
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
                self.assertIsNot(old, session.hid)
                self.assertFalse(session._recovering)
                self.assertTrue(session._session_ready.is_set())
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)

    async def test_idle_wireless_hid_is_renewed_before_its_lease_expires(self):
        session = self.session()
        session.auth_mode = 'direct'
        session._refresh_direct_hid = AsyncMock(return_value=True)
        with patch.object(bridge, 'DIRECT_HID_ROTATION_SECONDS', 0), \
             patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .001):
            task = asyncio.create_task(session._request_direct_hid_rotation())
            try:
                await asyncio.sleep(.02)
                self.assertGreater(session._refresh_direct_hid.await_count, 0)
                self.assertTrue(all(call.kwargs == {'idle_only': True}
                                    for call in session._refresh_direct_hid.await_args_list))
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)

    async def test_ordinary_keyboard_release_still_gets_verified(self):
        session = self.session([self.keyboard([4]), self.keyboard([])])
        session._input_verified = True
        await self.serve(session)
        self.assertEqual(self.ping_holds, [set()])

    async def test_background_health_does_not_delay_a_pressed_system_button(self):
        session = self.session()
        session._held_buttons.add(bridge.GLOBE_BUTTON)
        session._button_sent_at[bridge.GLOBE_BUTTON] = time.monotonic()
        with patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .001):
            task = asyncio.create_task(session._request_direct_hid_rotation())
            try:
                await asyncio.sleep(.03)
                self.assertEqual(session._ping_hid.await_count, 0)
                session._held_buttons.clear()
                await asyncio.sleep(.03)
                self.assertGreater(session._ping_hid.await_count, 0)
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)

    async def test_prolonged_button_hold_keeps_its_health_monitor(self):
        session = self.session()
        session._held_buttons.add(bridge.GLOBE_BUTTON)
        session._button_sent_at[bridge.GLOBE_BUTTON] = time.monotonic() - bridge.HID_OPERATION_TIMEOUT_SECONDS - .1
        with patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .001):
            task = asyncio.create_task(session._request_direct_hid_rotation())
            try:
                await asyncio.sleep(.03)
                self.assertGreater(session._ping_hid.await_count, 0)
                self.assertIn(bridge.GLOBE_BUTTON, session._held_buttons)
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)

    async def test_dead_indigo_is_not_hidden_by_a_healthy_keyboard_channel(self):
        session = self.session()
        async def ping(service=None):
            if service is session.indigo:
                raise ConnectionResetError('button channel closed')
        session._ping_hid = AsyncMock(side_effect=ping)
        with patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .001):
            task = asyncio.create_task(session._request_direct_hid_rotation())
            try:
                await asyncio.sleep(.03)
                self.assertTrue(session._recovering, 'A live keyboard ping hid the dead Indigo button channel')
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)

    async def test_double_press_is_separate_from_preceding_home_action(self):
        session = self.session()
        await session._apply_button(*bridge.HOME_BUTTON, 'down')
        await session._apply_button(*bridge.HOME_BUTTON, 'up')
        await session._apply_button(*bridge.HOME_BUTTON, 'double_press')
        events = [event for event in self.events if event[0] == 'button']
        self.assertGreaterEqual(events[2][2] - events[1][2], .45)
        self.assertEqual([event[1][-1] for event in events[2:]], [1, 2, 1, 2])
        self.assertLess(events[-1][2] - events[2][2], .05)

    async def test_following_home_does_not_merge_with_the_double_press(self):
        session = self.session()
        await session._apply_button(*bridge.HOME_BUTTON, 'double_press')
        await session._apply_button(*bridge.HOME_BUTTON, 'down')
        events = [event for event in self.events if event[0] == 'button']
        self.assertGreaterEqual(events[4][2] - events[3][2], .45)

    async def test_recovery_during_inter_command_gap_prevents_the_entire_burst(self):
        session = self.session()
        await session._apply_button(*bridge.HOME_BUTTON, 'down')
        await session._apply_button(*bridge.HOME_BUTTON, 'up')
        self.events.clear()
        async def recover(_):
            session._generation += 1
        with patch.object(bridge.asyncio, 'sleep', side_effect=recover):
            with self.assertRaisesRegex(RuntimeError, 'invalidated'):
                await session._apply_button(*bridge.HOME_BUTTON, 'double_press')
        self.assertFalse(self.events)
        self.assertFalse(session._held_buttons)


if __name__ == '__main__':
    unittest.main()
