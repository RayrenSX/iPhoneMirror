"""Device-layout regression: decode complete frames as dtuhidd does, without a phone."""
import struct
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


def decode_contacts(report):
    assert len(report) == 58 and report[0] == 9 and report[2] == 5
    contacts = {}
    for index in range(report[1]):
        flags, x, y = struct.unpack_from('<BHH', report, 3 + index * 5)
        slot = flags & 31
        assert slot not in contacts
        contacts[slot] = (bool(flags & 64), bool(flags & 128), report[40 + slot], x, y)
    return contacts


class FivePointReportTests(unittest.TestCase):
    def test_five_distinct_contacts_and_identity_table(self):
        report = bridge.build_touchscreen_frame([
            (slot, bridge.TOUCHSCREEN_STATE_CONTACT, 1000 + slot, 2000 + slot)
            for slot in range(5)], timestamp=0x010203040506)
        self.assertEqual(5, report[1])
        self.assertEqual({s: (True, True, s + 1, 1000 + s, 2000 + s) for s in range(5)}, decode_contacts(report))
        self.assertEqual(bytes.fromhex('060504030201'), report[45:51])
        self.assertEqual(b'\0' * 12, report[28:40])
        self.assertEqual(b'\0' * 7, report[51:58])

    def test_identity_is_indexed_by_slot_not_packed_record_position(self):
        report = bridge.build_touchscreen_frame([(4, bridge.TOUCHSCREEN_STATE_RELEASE, 123, 456)], 1)
        self.assertEqual({4: (False, False, 5, 123, 456)}, decode_contacts(report))

    def test_invalid_slots_and_capacity(self):
        for slots in [[], [0, 0], [-1], [5], list(range(6))]:
            with self.assertRaises(ValueError):
                bridge.build_touchscreen_frame([(s, bridge.TOUCHSCREEN_STATE_CONTACT, 0, 0) for s in slots])


class FivePointSessionTests(unittest.IsolatedAsyncioTestCase):
    async def test_usb_and_wireless_keep_other_fingers_pressed(self):
        for transport in ('usb', 'wireless'):
            session = bridge.TouchSession(None, 120, transport=transport)
            session._send_touch_report = AsyncMock()
            sm = bridge.FiveSlotStateMachine()

            async def send(points):
                await session._apply_frame(sm, {}, [
                    {'pointerId': pid, 'action': action, 'normalizedX': x, 'normalizedY': y}
                    for pid, action, x, y in points])
                return decode_contacts(session._send_touch_report.call_args.args[0])

            initial = await send([(i + 10, 'down', .1 * i, .2) for i in range(5)])
            self.assertEqual(1, session._send_touch_report.await_count)
            self.assertEqual(5, len(initial), transport)
            moved = await send([(12, 'move', .7, .8)])
            self.assertEqual(5, len(moved), transport)
            for slot in (0, 1, 3, 4):
                self.assertEqual(initial[slot], moved[slot])
            self.assertEqual((round(.7 * 65535), round(.8 * 65535)), moved[2][3:])
            self.assertTrue(session._send_touch_report.call_args.kwargs['motion'])
            released = await send([(12, 'up', .7, .8)])
            self.assertEqual((False, False), released[2][:2])
            self.assertTrue(all(value[0] for slot, value in released.items() if slot != 2))
            reused = await send([(99, 'down', .9, .9)])
            self.assertEqual(2, sm.slot_for(99))
            self.assertEqual(5, len(reused))
            await send([(100, 'down', .5, .5)])
            self.assertEqual(4, session._send_touch_report.await_count, 'Sixth finger produced a report')
            final = await send([(i, 'up', .5, .5) for i in (10, 11, 99, 13, 14)])
            self.assertTrue(all(not value[0] and not value[1] for value in final.values()))
            self.assertEqual([], sm.contacts(set()))

    async def test_recovery_clears_old_positions(self):
        sm = bridge.FiveSlotStateMachine()
        sm.assign(10)
        sm.update_position(10, 200, 300)
        sm.clear()
        sm.assign(20)
        self.assertEqual([(0, bridge.TOUCHSCREEN_STATE_CONTACT, 0, 0)], sm.contacts(set()))

    async def test_full_frame_replacement_releases_before_reusing_slot(self):
        session = bridge.TouchSession(None, 120)
        session._send_touch_report = AsyncMock()
        sm = bridge.FiveSlotStateMachine()
        for i in range(5):
            sm.assign(i)
            sm.update_position(i, i * 100, i * 200)
        await session._apply_frame(sm, {}, [
            {'pointerId': 2, 'action': 'up', 'normalizedX': .1, 'normalizedY': .2},
            {'pointerId': 99, 'action': 'down', 'normalizedX': .7, 'normalizedY': .8}])
        frames = [decode_contacts(call.args[0]) for call in session._send_touch_report.call_args_list]
        self.assertEqual(2, len(frames))
        self.assertFalse(frames[0][2][0])
        self.assertTrue(frames[1][2][0])
        self.assertEqual(2, sm.slot_for(99))
        self.assertIsNone(sm.slot_for(2))
        for slot in (0, 1, 3, 4):
            self.assertEqual(frames[0][slot], frames[1][slot])


if __name__ == '__main__':
    unittest.main()
