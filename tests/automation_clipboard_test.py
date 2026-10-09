"""Exercise the production IPC reader, with device I/O replaced by fakes."""
import asyncio
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


class Events:
    def __init__(self):
        self.events = []
        self.changed = asyncio.Condition()

    async def emit(self, event):
        async with self.changed:
            self.events.append(event)
            self.changed.notify_all()

    async def result(self, request_id):
        async with self.changed:
            await asyncio.wait_for(self.changed.wait_for(lambda: any(
                e.get('event') == 'clipboard_result' and e.get('requestId') == request_id
                for e in self.events)), 2)
        return next(e for e in self.events if e.get('event') == 'clipboard_result'
                    and e.get('requestId') == request_id)


class AutomationClipboardTests(unittest.IsolatedAsyncioTestCase):
    def session(self):
        events = Events()
        session = bridge.TouchSession(events, 120, 'fake', transport='wireless')
        session.rsd = object()

        async def idle():
            await asyncio.Event().wait()

        session._poll_device_pasteboard = idle
        session._ping_hid = lambda: asyncio.sleep(0)
        return session, events

    async def test_pure_write_and_correlated_read_do_not_paste_or_sync_windows(self):
        session, events = self.session()
        value = ''

        async def write(text):
            nonlocal value
            value = text

        async def read():
            return value

        async def paste(_):
            self.fail('Pure clipboard write invoked paste')

        async def messages():
            yield {'kind': 'write_clipboard', 'requestId': 'write1', 'text': '中文 😀'}
            self.assertTrue((await events.result('write1'))['success'])
            yield {'kind': 'read_clipboard', 'requestId': 'read1'}
            self.assertEqual('中文 😀', (await events.result('read1'))['text'])
            yield {'kind': 'write_clipboard', 'requestId': 'empty', 'text': ''}
            self.assertTrue((await events.result('empty'))['success'])
            yield {'kind': 'read_clipboard', 'requestId': 'read2'}
            self.assertEqual('', (await events.result('read2'))['text'])

        session._write_device_pasteboard = write
        session._read_device_pasteboard = read
        session._apply_paste_text = paste
        session.ipc.read_messages = messages
        await asyncio.wait_for(session._serve(), 4)
        self.assertFalse(any(e['event'] == 'clipboard_text' for e in events.events))

    async def test_paste_result_waits_for_existing_paste_to_complete(self):
        session, events = self.session()
        pasted = []

        async def paste(text):
            await asyncio.sleep(.01)
            pasted.append(text)

        async def messages():
            yield {'kind': 'paste_text', 'requestId': 'paste1', 'text': 'Hello'}
            self.assertTrue((await events.result('paste1'))['success'])
            self.assertEqual(['Hello'], pasted)

        session._apply_paste_text = paste
        session.ipc.read_messages = messages
        await asyncio.wait_for(session._serve(), 3)

    async def test_cancel_waits_for_cleanup_and_correlates_failure(self):
        session, events = self.session()
        started = asyncio.Event()
        released = asyncio.Event()

        async def paste(_):
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                await asyncio.sleep(.01)
                released.set()

        async def messages():
            yield {'kind': 'paste_text', 'requestId': 'cancel1', 'text': 'Hello'}
            await started.wait()
            yield {'kind': 'cancel_clipboard', 'requestId': 'cancel1'}
            result = await events.result('cancel1')
            self.assertFalse(result['success'])
            self.assertEqual('INPUT_CANCELLED', result['code'])
            self.assertTrue(released.is_set())

        session._apply_paste_text = paste
        session.ipc.read_messages = messages
        await asyncio.wait_for(session._serve(), 3)

    async def test_failed_write_does_not_expose_exception_or_text(self):
        session, events = self.session()

        async def write(_):
            raise RuntimeError('private clipboard secret')

        async def messages():
            yield {'kind': 'write_clipboard', 'requestId': 'failure', 'text': 'private clipboard secret'}
            result = await events.result('failure')
            self.assertFalse(result['success'])
            self.assertNotIn('private', repr(result))
            self.assertEqual('CLIPBOARD_UNAVAILABLE', result['code'])

        session._write_device_pasteboard = write
        session.ipc.read_messages = messages
        await asyncio.wait_for(session._serve(), 3)

    async def test_utf8_limit_rejects_before_device_io(self):
        session, events = self.session()

        async def write(_):
            self.fail('Oversized text reached device I/O')

        async def messages():
            yield {'kind': 'write_clipboard', 'requestId': 'large', 'text': '中' * 22000}
            self.assertFalse((await events.result('large'))['success'])

        session._write_device_pasteboard = write
        session.ipc.read_messages = messages
        await asyncio.wait_for(session._serve(), 3)


if __name__ == '__main__':
    unittest.main()
