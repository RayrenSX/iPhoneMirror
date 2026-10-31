"""Shared clipboard deadlines, cancellation cleanup, and recovery after a stall."""
import asyncio
from pathlib import Path
import re
import sys
import time
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge
import automation_clipboard_test as automation
Events = automation.Events


class ClipboardTimeoutTests(unittest.IsolatedAsyncioTestCase):
    def test_host_deadline_covers_bridge_request_and_cleanup(self):
        source = (Path(__file__).resolve().parents[1] /
                  'src/App/Services/CoreDeviceTouchProtocol.cs').read_text(encoding='utf-8-sig')
        host = int(re.search(r'ClipboardRequestTimeoutSeconds\s*=\s*(\d+)', source)[1])
        cleanup = int(re.search(r'ClipboardCleanupTimeoutSeconds\s*=\s*(\d+)', source)[1])
        self.assertLess(bridge.PASTEBOARD_FULL_READ_TIMEOUT_SECONDS, bridge.PASTEBOARD_READ_TIMEOUT_SECONDS)
        self.assertLess(bridge.PASTEBOARD_READ_TIMEOUT_SECONDS, bridge.PASTEBOARD_REQUEST_TIMEOUT_SECONDS)
        self.assertGreaterEqual(cleanup, 5)
        self.assertGreaterEqual(host - bridge.PASTEBOARD_REQUEST_TIMEOUT_SECONDS, cleanup)

    async def test_stalled_rich_read_is_retired_and_next_copy_can_sync(self):
        events = Events()
        session = bridge.TouchSession(events, 120)
        session.rsd = object()
        hid = session.hid = object()
        recovered = False
        monitor = SimpleNamespace(prepare=AsyncMock(), close=AsyncMock(), started=False)
        class Service:
            def __init__(self, rsd): pass
            async def __aenter__(self): return self
            async def __aexit__(self, *args): pass
            async def get(self, data_policy=None):
                if not recovered and data_policy == {'allResolved': {}}:
                    await asyncio.Event().wait()
                return {'items': [{'types': ['public.text'], 'data': {'public.text':
                    {'data': b'next complete copy'} if recovered else {}}}],
                    'metadata': {'nonce': 'device', 'changeCount': 2 if recovered else 1}}
        async def restart():
            nonlocal recovered
            recovered = True
            session._pasteboard_full_read_interrupted = False
            return True
        session._restart_failed_pasteboard = AsyncMock(side_effect=restart)
        with patch.object(bridge, 'PasteboardService', Service), \
             patch.object(bridge, 'PasteboardConverterRecovery', return_value=monitor), \
             patch.object(bridge, 'PASTEBOARD_READ_TIMEOUT_SECONDS', .04, create=True), \
             patch.object(bridge, 'PASTEBOARD_FULL_READ_TIMEOUT_SECONDS', .5):
            started = time.monotonic()
            with self.assertRaises(TimeoutError):
                await asyncio.wait_for(session._publish_device_pasteboard(), .2)
            self.assertLess(time.monotonic() - started, .15, 'The end-to-end read watchdog did not fire')
            self.assertTrue(session._pasteboard_full_read_interrupted)
            self.assertFalse(session._pasteboard_lock.locked())
            self.assertFalse(any(e['event'] == 'clipboard_text' for e in events.events))
            await session._publish_device_pasteboard()
        session._restart_failed_pasteboard.assert_awaited_once()
        self.assertEqual(['next complete copy'], [e['text'] for e in events.events if e['event'] == 'clipboard_text'])
        self.assertIs(hid, session.hid)
        self.assertEqual(2, sum(e['event'] == 'clipboard_read_finished' for e in events.events))

    async def test_silent_rpc_cleans_up_before_failure_and_next_request_succeeds(self):
        session, events = automation.AutomationClipboardTests().session()
        released = asyncio.Event()
        observed = []
        async def paste(text):
            if text == 'silent':
                try:
                    await asyncio.Event().wait()
                finally:
                    await asyncio.sleep(.01)
                    released.set()
        async def messages():
            yield {'kind': 'paste_text', 'requestId': 'silent', 'text': 'silent'}
            result = await events.result('silent')
            observed.append((result, released.is_set()))
            yield {'kind': 'paste_text', 'requestId': 'next', 'text': 'next'}
            observed.append((await events.result('next'), released.is_set()))
        session._apply_paste_text = paste
        session.ipc.read_messages = messages
        with patch.object(bridge, 'PASTEBOARD_REQUEST_TIMEOUT_SECONDS', .03, create=True):
            await asyncio.wait_for(session._serve(), .4)
        self.assertEqual(2, len(observed))
        self.assertFalse(observed[0][0]['success'])
        self.assertTrue(observed[0][1], 'Reply preceded cancellation cleanup')
        self.assertTrue(observed[1][0]['success'])

    async def test_valid_request_uses_one_budget_for_queue_and_work(self):
        session, events = automation.AutomationClipboardTests().session()
        observed = []
        async def read():
            await asyncio.sleep(.04)
            return 'complete text'
        async def messages():
            yield {'kind': 'read_clipboard', 'requestId': 'slowvalid'}
            observed.append(await events.result('slowvalid'))
        session._read_device_pasteboard = read
        session.ipc.read_messages = messages
        with patch.object(bridge, 'PASTEBOARD_REQUEST_TIMEOUT_SECONDS', .1, create=True):
            await asyncio.wait_for(session._serve(), .4)
        self.assertEqual(1, len(observed))
        self.assertTrue(observed[0]['success'])
        self.assertEqual('complete text', observed[0]['text'])


if __name__ == '__main__':
    unittest.main()
