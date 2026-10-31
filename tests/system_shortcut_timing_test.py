"""Device-side timing under an already queued burst of shortcut frames."""
import asyncio
from pathlib import Path
import sys
import time
import unittest
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


class ShortcutTimingTests(unittest.IsolatedAsyncioTestCase):
    def session(self):
        self.events = []
        session = bridge.TouchSession(None, 120, 'timing-fixture')
        session._check_media_auth = lambda: None
        session._ping_hid = AsyncMock()
        session.keyboard_service_id = 7

        async def keyboard(_service, usages, _timestamp=None):
            self.events.append(('keyboard', tuple(usages), time.monotonic()))

        async def button(page, code, state):
            self.events.append(('button', (page, code, state), time.monotonic()))

        session.hid = type('Hid', (), {'send_keyboard': staticmethod(keyboard)})()
        session.indigo = type('Indigo', (), {'send_button': staticmethod(button)})()
        return session

    async def test_queued_globe_chord_keeps_real_send_intervals(self):
        session = self.session()
        # No host delays: all four frames were queued before the IPC reader
        # resumed, e.g. after a slow HID operation or service initialization.
        await session._apply_button(*bridge.GLOBE_BUTTON, 'down')
        await session._send_keyboard_report([0x11])
        await session._send_keyboard_report([])
        await session._apply_button(*bridge.GLOBE_BUTTON, 'up')
        self.assertEqual([e[0] for e in self.events], ['button', 'keyboard', 'keyboard', 'button'])
        self.assertGreaterEqual(self.events[1][2] - self.events[0][2], .055)
        self.assertGreaterEqual(self.events[2][2] - self.events[1][2], .075)
        self.assertGreaterEqual(self.events[3][2] - self.events[2][2], .015)
        self.assertFalse(session._held_buttons)
        self.assertFalse(session._button_sent_at)

    async def test_queued_keyboard_uses_send_clock_instead_of_host_wall_clock(self):
        from pymobiledevice3.remote.core_device.hid_service import build_keyboard_report
        session = self.session()
        reports = []
        async def keyboard(_service, usages, timestamp=None):
            reports.append(build_keyboard_report(usages, timestamp))
        session.hid.send_keyboard = keyboard
        # IPC timestamps use UTC nanoseconds and can move backwards after a
        # Windows clock adjustment. HID needs its own monotonic send clock.
        with patch.object(bridge.time, 'monotonic_ns', side_effect=[123456, 223456]):
            await session._apply_keyboard({}, 1793410000000000000, [0x11])
            await session._apply_keyboard({}, 1793400000000000000, [])
        self.assertEqual([int.from_bytes(report[31:37], 'little') for report in reports],
                         [123456, 223456])

    async def test_queued_home_pulse_has_minimum_device_hold(self):
        session = self.session()
        await session._apply_button(*bridge.HOME_BUTTON, 'down')
        await session._apply_button(*bridge.HOME_BUTTON, 'up')
        self.assertGreaterEqual(self.events[1][2] - self.events[0][2], .045)

    async def test_queued_double_home_preserves_release_to_press_interval(self):
        session = self.session()
        # The host's 100 ms double-click gap can finish before the IPC reader
        # resumes. Two complete pulses must still remain distinct on Indigo.
        for _ in range(2):
            await session._apply_button(*bridge.HOME_BUTTON, 'down')
            await session._apply_button(*bridge.HOME_BUTTON, 'up')
        self.assertGreaterEqual(self.events[2][2] - self.events[1][2], .45)

    async def test_same_channel_system_chord_keeps_device_send_intervals(self):
        session = self.session()
        await session._apply_button(*bridge.GLOBE_BUTTON, 'down')
        await session._apply_button(7, 0x11, 'down')
        await session._apply_button(7, 0x11, 'up')
        await session._apply_button(*bridge.GLOBE_BUTTON, 'up')
        self.assertEqual([event[0] for event in self.events], ['button'] * 4)
        self.assertGreaterEqual(self.events[1][2] - self.events[0][2], .055)
        self.assertGreaterEqual(self.events[2][2] - self.events[1][2], .075)
        self.assertGreaterEqual(self.events[3][2] - self.events[2][2], .015)
        self.assertFalse(session._held_buttons)

    async def test_atomic_home_double_press_and_failure_release(self):
        session = self.session()
        with patch.object(bridge.asyncio, 'sleep', new=AsyncMock()) as sleep:
            await session._apply_button(*bridge.HOME_BUTTON, 'double_press')
        self.assertEqual([e[1][2] for e in self.events], [1, 2, 1, 2])
        sleep.assert_not_awaited()
        session._ping_hid.assert_not_awaited()
        self.assertFalse(session._held_buttons)
        self.events.clear()
        original = session.indigo.send_button

        async def fail_second_down(page, code, state):
            await original(page, code, state)
            if len(self.events) == 3:
                raise OSError('second press failed')

        session.indigo.send_button = fail_second_down
        with self.assertRaises(OSError):
            await session._apply_button(*bridge.HOME_BUTTON, 'double_press')
        self.assertEqual([e[1][2] for e in self.events], [1, 2, 1, 2])
        self.assertFalse(session._held_buttons)

    async def test_atomic_home_keeps_one_service_and_cleans_up_on_cancellation(self):
        session = self.session()
        original = session.indigo.send_button

        async def cancel_second_down(page, code, state):
            self.assertTrue(session._hid_lifecycle_lock.locked())
            await original(page, code, state)
            if len(self.events) == 3:
                raise asyncio.CancelledError()

        session.indigo.send_button = cancel_second_down
        with self.assertRaises(asyncio.CancelledError):
            await session._apply_button(*bridge.HOME_BUTTON, 'double_press')
        self.assertEqual([e[1][2] for e in self.events], [1, 2, 1, 2])
        self.assertFalse(session._held_buttons)
        self.assertFalse(session._button_sent_at)

    async def test_atomic_home_does_not_press_after_generation_change(self):
        session = self.session()
        original = session.indigo.send_button

        async def retire_after_first_up(page, code, state):
            await original(page, code, state)
            if state == 2:
                session._generation += 1

        session.indigo.send_button = retire_after_first_up
        with self.assertRaisesRegex(RuntimeError, 'invalidated'):
            await session._apply_button(*bridge.HOME_BUTTON, 'double_press')
        self.assertEqual([e[1][2] for e in self.events], [1, 2])
        self.assertFalse(session._held_buttons)

    def test_double_press_rejects_other_buttons(self):
        frame = dict(schema=bridge.MESSAGE_SCHEMA, kind=bridge.BUTTON_MESSAGE_KIND,
                     seq=1, usagePage=12, usageCode=64, state='double_press')
        self.assertEqual(bridge.decode_button_event(frame)[-1], 'double_press')
        frame['usageCode'] = 0xE9
        with self.assertRaises(ValueError):
            bridge.decode_button_event(frame)

    async def test_ordinary_keyboard_and_other_buttons_keep_original_path(self):
        session = self.session()
        with patch.object(bridge.asyncio, 'sleep', new=AsyncMock()) as sleep:
            await session._send_keyboard_report([4])
            await session._send_keyboard_report([])
            await session._apply_button(0x0C, 0xE9, 'down')
            await session._apply_button(0x0C, 0xE9, 'up')
        sleep.assert_not_awaited()

    async def test_cleanup_clears_timing_without_delaying_release(self):
        session = self.session()
        await session._apply_button(*bridge.HOME_BUTTON, 'down')
        await session._apply_button(*bridge.HOME_BUTTON, 'up')
        self.assertIsNotNone(session._home_released_at)
        await session._apply_button(*bridge.GLOBE_BUTTON, 'down')
        await session._send_keyboard_report([0x11])
        with patch.object(bridge.asyncio, 'sleep', new=AsyncMock()) as sleep:
            await session._release_button_state()
            await session._send_keyboard_report([])
        sleep.assert_not_awaited()
        self.assertIsNone(session._globe_letter_sent_at)
        self.assertIsNone(session._globe_letter_released_at)
        self.assertIsNone(session._home_released_at)
        self.assertFalse(session._button_sent_at)


if __name__ == '__main__':
    unittest.main()
