"""Exercise the production IPC reader's handoff across asynchronous Ctrl+V."""
import asyncio
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


class Ipc:
    def __init__(self):
        self.events = []

    async def emit(self, event):
        self.events.append(event)


class KeyboardOwnershipTests(unittest.IsolatedAsyncioTestCase):
    async def test_handoff_cancels_paste_before_next_owner_for_both_transports(self):
        for mode in ('usb', 'wireless'):
            for after_modifier in (False, True):
                with self.subTest(mode=mode, after_modifier=after_modifier):
                    session = bridge.TouchSession(Ipc(), 120, 'test-device', transport=mode)
                    session.rsd = object()
                    reached = asyncio.Event()
                    reports = []

                    async def idle():
                        await asyncio.Event().wait()

                    async def write(_text):
                        if not after_modifier:
                            reached.set()
                            await asyncio.Event().wait()

                    async def send(usages, _timestamp=None):
                        reports.append(list(usages))
                        if usages == [0xE3]:
                            reached.set()

                    async def messages():
                        yield {'kind': bridge.PASTE_TEXT_MESSAGE_KIND, 'text': 'old-owner'}
                        await asyncio.wait_for(reached.wait(), 1)
                        yield {'schema': bridge.MESSAGE_SCHEMA, 'kind': bridge.KEYBOARD_MESSAGE_KIND,
                               'seq': 1, 'usages': [], 'releaseAll': True}
                        # This represents the next owner's packet in the same
                        # IPC stream. No old Command+V may follow this marker.
                        yield {'schema': bridge.MESSAGE_SCHEMA, 'kind': bridge.KEYBOARD_MESSAGE_KIND,
                               'seq': 2, 'usages': [4]}
                        await asyncio.sleep(0.1)

                    session._poll_device_pasteboard = idle
                    session._request_direct_hid_rotation = idle
                    session._write_device_pasteboard = write
                    session._send_keyboard_report = send
                    session._ping_hid = lambda: asyncio.sleep(0)
                    session.ipc.read_messages = messages
                    await asyncio.wait_for(session._serve(), 2)
                    expected = [[0xE3], [], [], [4]] if after_modifier else [[], [4]]
                    self.assertEqual(expected, reports)
                    self.assertFalse(any(e.get('code') in ('paste_failed', 'send_failed')
                                         for e in session.ipc.events))

    async def test_physical_key_up_does_not_cancel_normal_paste(self):
        session = bridge.TouchSession(Ipc(), 120, 'test-device', transport='wireless')
        started = asyncio.Event()
        finish = asyncio.Event()
        completed = asyncio.Event()

        async def idle():
            await asyncio.Event().wait()

        async def paste(_text):
            started.set()
            await finish.wait()
            completed.set()

        async def keyboard(_frame, _timestamp, _usages):
            finish.set()

        async def messages():
            yield {'kind': bridge.PASTE_TEXT_MESSAGE_KIND, 'text': 'normal-paste'}
            await started.wait()
            yield {'schema': bridge.MESSAGE_SCHEMA, 'kind': bridge.KEYBOARD_MESSAGE_KIND,
                   'seq': 1, 'usages': []}
            await asyncio.wait_for(completed.wait(), 1)

        session._poll_device_pasteboard = idle
        session._apply_paste_text = paste
        session._apply_keyboard = keyboard
        session._ping_hid = lambda: asyncio.sleep(0)
        session.ipc.read_messages = messages
        await asyncio.wait_for(session._serve(), 2)
        self.assertTrue(completed.is_set())


if __name__ == '__main__':
    unittest.main()
