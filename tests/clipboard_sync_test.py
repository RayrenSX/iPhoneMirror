"""Device clipboard polling regressions; no physical device or desktop access."""
import asyncio
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


class Ipc:
    def __init__(self):
        self.events = []

    async def emit(self, event):
        self.events.append(event)


class Pasteboard:
    def __init__(self, rsd):
        self.rsd = rsd
        self.text = 'device text'
        self.closed = False
        self.entered = 0

    async def __aenter__(self):
        self.entered += 1
        return self

    async def __aexit__(self, *_args):
        self.closed = True

    async def get_text(self):
        return self.text

    async def set_text(self, text):
        self.text = text


class ClipboardTests(unittest.IsolatedAsyncioTestCase):
    def session(self, mode='usb'):
        session = bridge.TouchSession(Ipc(), 120, 'clipboard-test', transport=mode)
        session.rsd = object()
        return session

    async def test_both_transports_start_and_cancel_background_polling(self):
        for mode in ('usb', 'wireless'):
            with self.subTest(mode=mode):
                session = self.session(mode)
                started = asyncio.Event()
                ended = asyncio.Event()
                stop = asyncio.Event()

                async def poll():
                    started.set()
                    try:
                        await asyncio.Event().wait()
                    finally:
                        ended.set()

                async def read_messages():
                    await stop.wait()
                    if False:
                        yield {}

                async def rotate():
                    await asyncio.Event().wait()

                session.ipc.read_messages = read_messages
                session._poll_device_pasteboard = poll
                session._request_direct_hid_rotation = rotate
                task = asyncio.create_task(session._serve())
                try:
                    await asyncio.wait_for(started.wait(), 1)
                    stop.set()
                    await asyncio.wait_for(task, 1)
                    self.assertTrue(ended.is_set())
                finally:
                    task.cancel()
                    await asyncio.gather(task, return_exceptions=True)

    async def test_reads_and_windows_paste_reuse_one_connection(self):
        session = self.session()
        instances = []

        def create(rsd):
            result = Pasteboard(rsd)
            instances.append(result)
            return result

        with patch.object(bridge, 'PasteboardService', side_effect=create):
            self.assertEqual('device text', await session._read_device_pasteboard())
            await session._write_device_pasteboard('Windows / 中文 / 😀')
            self.assertEqual('Windows / 中文 / 😀', await session._read_device_pasteboard())
            self.assertEqual(1, len(instances))
            self.assertEqual(1, instances[0].entered)
            self.assertFalse(instances[0].closed)
            # RSD recovery must invalidate the persistent side channel.
            session.rsd = object()
            await session._read_device_pasteboard()
            self.assertEqual(2, len(instances))
            self.assertTrue(instances[0].closed)
            await session._close_device_pasteboard()
            self.assertTrue(instances[1].closed)

    async def test_timeout_discards_connection_before_retry(self):
        session = self.session()
        stuck = Pasteboard(session.rsd)
        replacement = Pasteboard(session.rsd)

        async def blocked_read():
            await asyncio.Event().wait()

        stuck.get_text = blocked_read
        with patch.object(bridge, 'PasteboardService', side_effect=[stuck, replacement]):
            with self.assertRaises(asyncio.TimeoutError):
                await asyncio.wait_for(session._read_device_pasteboard(), 0.02)
            self.assertTrue(stuck.closed)
            self.assertIsNone(session._pasteboard_service)
            self.assertEqual('device text', await session._read_device_pasteboard())
            self.assertIs(session._pasteboard_service, replacement)
            await session._close_device_pasteboard()

    async def test_reads_and_writes_are_serialized_and_queued_cancellation_is_safe(self):
        session = self.session()
        service = Pasteboard(session.rsd)
        entered = asyncio.Event()
        release = asyncio.Event()

        async def blocked_read():
            entered.set()
            await release.wait()
            return service.text

        service.get_text = blocked_read
        with patch.object(bridge, 'PasteboardService', return_value=service):
            read = asyncio.create_task(session._read_device_pasteboard())
            await entered.wait()
            write = asyncio.create_task(session._write_device_pasteboard('pasted'))
            queued = asyncio.create_task(session._read_device_pasteboard())
            await asyncio.sleep(0)
            self.assertFalse(write.done())
            queued.cancel()
            await asyncio.gather(queued, return_exceptions=True)
            self.assertFalse(service.closed)
            release.set()
            self.assertEqual('device text', await read)
            await write
            self.assertEqual('pasted', service.text)
            await session._close_device_pasteboard()

    async def test_cleanup_closes_pasteboard_before_rsd(self):
        session = self.session()
        order = []

        class Service(Pasteboard):
            async def __aexit__(self, *_args):
                order.append(self.text)

        session.rsd = Service(None)
        session.rsd.text = 'rsd'
        pasteboard = Service(session.rsd)
        pasteboard.text = 'pasteboard'
        with patch.object(bridge, 'PasteboardService', return_value=pasteboard):
            await session._read_device_pasteboard()
            await session._cleanup()
        self.assertEqual(['pasteboard', 'rsd'], order)
        self.assertIsNone(session._pasteboard_service)

    async def test_host_paste_is_not_echoed_but_later_device_changes_are(self):
        session = self.session()
        service = Pasteboard(session.rsd)
        ticks = 0

        async def sleep(_delay):
            nonlocal ticks
            ticks += 1
            if ticks == 1:
                await session._write_device_pasteboard('host-A')
                # The user can now copy B on Windows. The next poll must not
                # deliver host-A back as a device-originated update.
            elif ticks == 2:
                service.text = 'device-B'
            elif ticks == 3:
                service.text = 'host-A'  # A real subsequent device change.
            else:
                raise asyncio.CancelledError

        with patch.object(bridge, 'PasteboardService', return_value=service), \
                patch.object(bridge.asyncio, 'sleep', new=sleep):
            with self.assertRaises(asyncio.CancelledError):
                await session._poll_device_pasteboard()
        self.assertEqual(['device text', 'device-B', 'host-A'],
                         [event['text'] for event in session.ipc.events])
        await session._close_device_pasteboard()

    async def test_host_baseline_survives_poll_restart_and_inflight_read(self):
        session = self.session()
        service = Pasteboard(session.rsd)
        original_read = session._read_device_pasteboard

        async def stale_read():
            text = await original_read()
            await session._write_device_pasteboard('host-A')
            return text

        async def stop(_delay):
            raise asyncio.CancelledError

        with patch.object(bridge, 'PasteboardService', return_value=service), \
                patch.object(bridge.asyncio, 'sleep', new=stop):
            session._read_device_pasteboard = stale_read
            with self.assertRaises(asyncio.CancelledError):
                await session._poll_device_pasteboard()
            self.assertEqual([], session.ipc.events)
            session._read_device_pasteboard = original_read
            with self.assertRaises(asyncio.CancelledError):
                await session._poll_device_pasteboard()
            self.assertEqual([], session.ipc.events)
        await session._close_device_pasteboard()

    async def test_host_write_during_ipc_emit_keeps_new_baseline(self):
        session = self.session()
        service = Pasteboard(session.rsd)
        original_emit = session.ipc.emit
        ticks = 0

        async def emit(event):
            await session._write_device_pasteboard('host-A')
            await original_emit(event)

        async def sleep(_delay):
            nonlocal ticks
            ticks += 1
            if ticks == 2:
                raise asyncio.CancelledError

        session.ipc.emit = emit
        with patch.object(bridge, 'PasteboardService', return_value=service), \
                patch.object(bridge.asyncio, 'sleep', new=sleep):
            with self.assertRaises(asyncio.CancelledError):
                await session._poll_device_pasteboard()
        self.assertEqual(['device text'], [e['text'] for e in session.ipc.events])
        await session._close_device_pasteboard()

    async def test_queued_host_write_invalidates_already_read_snapshot(self):
        session = self.session()
        started = asyncio.Event()
        release = asyncio.Event()
        writers = []

        class PendingWrite(Pasteboard):
            async def set_text(self, text):
                started.set()
                await release.wait()
                self.text = text

        service = PendingWrite(session.rsd)
        original_read = session._read_device_pasteboard

        async def read_before_paste():
            text = await original_read()
            writers.append(asyncio.create_task(session._write_device_pasteboard('host-A')))
            await started.wait()  # SET has not succeeded yet.
            return text

        async def stop(_delay):
            release.set()
            await writers[0]
            raise asyncio.CancelledError

        session._read_device_pasteboard = read_before_paste
        try:
            with patch.object(bridge, 'PasteboardService', return_value=service), \
                    patch.object(bridge.asyncio, 'sleep', new=stop):
                with self.assertRaises(asyncio.CancelledError):
                    await session._poll_device_pasteboard()
            self.assertEqual([], session.ipc.events)
            self.assertEqual('host-A', session._pasteboard_last_text)
        finally:
            release.set()
            await asyncio.gather(*writers, return_exceptions=True)
            await session._close_device_pasteboard()

    async def test_read_timeout_cleanup_does_not_consume_paste_budget(self):
        session = self.session()
        entered = asyncio.Event()
        writes = []
        reports = []

        class SlowRead(Pasteboard):
            async def get_text(self):
                entered.set()
                await asyncio.Event().wait()

            async def __aexit__(self, *args):
                await asyncio.sleep(0.08)
                await super().__aexit__(*args)

        class ReadyWrite(Pasteboard):
            async def set_text(self, text):
                await asyncio.sleep(0.01)
                writes.append(text)

        async def keyboard(usages):
            reports.append(usages)

        session._send_keyboard_report = keyboard
        with patch.object(bridge, 'PASTEBOARD_OPERATION_TIMEOUT_SECONDS', 0.04), \
                patch.object(bridge, 'PasteboardService', side_effect=[
                    SlowRead(session.rsd), ReadyWrite(session.rsd)]):
            read = asyncio.create_task(session._read_device_pasteboard())
            await entered.wait()
            paste = asyncio.create_task(session._apply_paste_text('Windows paste'))
            with self.assertRaises(asyncio.TimeoutError):
                await read
            await asyncio.wait_for(paste, 2)
        self.assertEqual(['Windows paste'], writes)
        self.assertEqual([[0xE3], [0xE3, 0x19], [0xE3], []], reports)
        self.assertTrue(session._pasteboard_writes_idle.is_set())
        await session._close_device_pasteboard()

    async def test_connection_failure_before_set_does_not_suppress_device_text(self):
        session = self.session()

        class FailedConnect(Pasteboard):
            async def __aenter__(self):
                raise ConnectionError('SET was never sent')

            async def set_text(self, text):
                raise AssertionError('SET must not start after a connection failure')

        ready = Pasteboard(session.rsd)
        ready.text = 'A'

        async def stop(_delay):
            raise asyncio.CancelledError

        with patch.object(bridge, 'PasteboardService', side_effect=[FailedConnect(session.rsd), ready]):
            with self.assertRaises(ConnectionError):
                await session._write_device_pasteboard('A')
            self.assertTrue(session._pasteboard_writes_idle.is_set())
            self.assertEqual(set(), session._pasteboard_unconfirmed_writes)
            with patch.object(bridge.asyncio, 'sleep', new=stop):
                with self.assertRaises(asyncio.CancelledError):
                    await session._poll_device_pasteboard()
        self.assertEqual(['A'], [event['text'] for event in session.ipc.events])
        await session._close_device_pasteboard()

    async def test_applied_set_without_reply_is_not_echoed_after_timeout_or_cancel(self):
        for failure in ('timeout', 'cancel'):
            with self.subTest(failure=failure):
                session = self.session()
                session._pasteboard_last_text = 'previous device text'
                applied = asyncio.Event()
                phone = {'text': 'previous device text'}

                class AppliedWithoutReply(Pasteboard):
                    async def set_text(self, text):
                        phone['text'] = text
                        applied.set()
                        await asyncio.Event().wait()

                class Reconnected(Pasteboard):
                    async def get_text(self):
                        return phone['text']

                stuck = AppliedWithoutReply(session.rsd)
                ready = Reconnected(session.rsd)
                ticks = 0

                async def sleep(_delay):
                    nonlocal ticks
                    ticks += 1
                    if ticks == 2:
                        phone['text'] = 'new device text'
                    elif ticks == 3:
                        phone['text'] = 'host-old-A'  # Genuine subsequent copy.
                    elif ticks == 4:
                        raise asyncio.CancelledError

                with patch.object(bridge, 'PASTEBOARD_OPERATION_TIMEOUT_SECONDS', 0.02), \
                        patch.object(bridge, 'PasteboardService', side_effect=[stuck, ready]):
                    writer = asyncio.create_task(session._write_device_pasteboard('host-old-A'))
                    await applied.wait()
                    if failure == 'cancel':
                        writer.cancel()
                    expected = asyncio.CancelledError if failure == 'cancel' else asyncio.TimeoutError
                    with self.assertRaises(expected):
                        await writer
                    self.assertTrue(stuck.closed)
                    self.assertTrue(session._pasteboard_writes_idle.is_set())
                    self.assertEqual({'host-old-A'}, session._pasteboard_unconfirmed_writes)
                    # Reconnection/recovery must retain the uncertain origin.
                    session.rsd = object()
                    with patch.object(bridge.asyncio, 'sleep', new=sleep):
                        with self.assertRaises(asyncio.CancelledError):
                            await session._poll_device_pasteboard()
                self.assertEqual(['new device text', 'host-old-A'],
                                 [event['text'] for event in session.ipc.events])
                self.assertEqual(set(), session._pasteboard_unconfirmed_writes)
                await session._close_device_pasteboard()

    async def test_unconfirmed_set_reconciles_only_on_a_stable_text_read(self):
        for first_text in ('host-A', 'device-B', None):
            with self.subTest(first_text=first_text):
                session = self.session()

                class FailedSet(Pasteboard):
                    async def set_text(self, text):
                        raise ConnectionError('reply lost')

                # More than one failed SET can precede a successful poll. Either
                # attempt may be on the phone if the other never applied.
                with patch.object(bridge, 'PasteboardService', side_effect=lambda rsd: FailedSet(rsd)):
                    for text in ('host-A', 'host-C'):
                        with self.assertRaises(ConnectionError):
                            await session._write_device_pasteboard(text)
                reads = iter([TimeoutError(), object(), 'stale', first_text, 'device-D', 'host-A'])
                count = 0
                pending_before_read = []

                async def read():
                    nonlocal count
                    count += 1
                    if count <= 4:
                        pending_before_read.append(set(session._pasteboard_unconfirmed_writes))
                    value = next(reads, asyncio.CancelledError())
                    if isinstance(value, BaseException):
                        raise value
                    if count == 3:
                        session._pasteboard_write_generation += 1
                    return value

                async def sleep(_delay):
                    pass

                session._read_device_pasteboard = read
                with patch.object(bridge.asyncio, 'sleep', new=sleep):
                    with self.assertRaises(asyncio.CancelledError):
                        await session._poll_device_pasteboard()
                expected = ([] if first_text == 'host-A' else [first_text or '']) + ['device-D', 'host-A']
                self.assertEqual([{'host-A', 'host-C'}] * 4, pending_before_read)
                self.assertEqual(expected, [event['text'] for event in session.ipc.events
                                            if event['event'] == 'clipboard_text'])
                self.assertEqual(set(), session._pasteboard_unconfirmed_writes)

    async def test_confirmed_set_replaces_unconfirmed_candidates(self):
        session = self.session()

        class FailedSet(Pasteboard):
            async def set_text(self, text):
                raise ConnectionError('reply lost')

        ready = Pasteboard(session.rsd)

        async def stop(_delay):
            raise asyncio.CancelledError

        with patch.object(bridge, 'PasteboardService', side_effect=[FailedSet(session.rsd), ready]):
            with self.assertRaises(ConnectionError):
                await session._write_device_pasteboard('unconfirmed-A')
            await session._write_device_pasteboard('confirmed-B')
            self.assertEqual(set(), session._pasteboard_unconfirmed_writes)
            self.assertEqual('confirmed-B', session._pasteboard_last_text)
            ready.text = 'unconfirmed-A'  # A new device copy after confirmed B.
            with patch.object(bridge.asyncio, 'sleep', new=stop):
                with self.assertRaises(asyncio.CancelledError):
                    await session._poll_device_pasteboard()
        self.assertEqual(['unconfirmed-A'], [event['text'] for event in session.ipc.events])
        await session._close_device_pasteboard()

    async def test_cancelled_queued_write_does_not_close_active_reader(self):
        session = self.session()
        service = Pasteboard(session.rsd)
        entered = asyncio.Event()
        release = asyncio.Event()

        async def read():
            entered.set()
            await release.wait()
            return service.text

        service.get_text = read
        with patch.object(bridge, 'PasteboardService', return_value=service):
            reader = asyncio.create_task(session._read_device_pasteboard())
            await entered.wait()
            writer = asyncio.create_task(session._write_device_pasteboard('cancelled'))
            await asyncio.sleep(0)
            self.assertFalse(session._pasteboard_writes_idle.is_set())
            writer.cancel()
            await asyncio.gather(writer, return_exceptions=True)
            self.assertFalse(service.closed)
            self.assertTrue(session._pasteboard_writes_idle.is_set())
            self.assertEqual(set(), session._pasteboard_unconfirmed_writes)
            release.set()
            self.assertEqual('device text', await reader)
            await session._write_device_pasteboard('next paste')
            self.assertEqual('next paste', service.text)
        await session._close_device_pasteboard()

    async def test_poll_backoff_recovery_and_text_changes(self):
        session = self.session()
        values = iter([TimeoutError(), TimeoutError(), 'A', 'A', None, 'A'])
        delays = []

        async def read():
            value = next(values, asyncio.CancelledError())
            if isinstance(value, BaseException):
                raise value
            return value

        async def sleep(delay):
            delays.append(delay)

        session._read_device_pasteboard = read
        with patch.object(bridge.asyncio, 'sleep', new=sleep):
            with self.assertRaises(asyncio.CancelledError):
                await session._poll_device_pasteboard()
        self.assertEqual([1.6, 3.2, 0.8, 0.8, 0.8, 0.8], delays)
        self.assertEqual(['A', '', 'A'], [event['text'] for event in session.ipc.events
                                       if event['event'] == 'clipboard_text'])
        self.assertEqual(['clipboard_poll_failed', 'clipboard_poll_recovered'],
                         [event['code'] for event in session.ipc.events if 'code' in event])

    async def test_poll_waits_for_hid_recovery(self):
        session = self.session()
        session._recovering = True
        session._session_ready.clear()
        read_started = asyncio.Event()

        async def read():
            read_started.set()
            raise asyncio.CancelledError

        session._read_device_pasteboard = read
        poll = asyncio.create_task(session._poll_device_pasteboard())
        try:
            await asyncio.sleep(0)
            self.assertFalse(read_started.is_set())
            session._session_ready.set()
            await asyncio.wait_for(read_started.wait(), 1)
            await asyncio.gather(poll, return_exceptions=True)
        finally:
            poll.cancel()
            await asyncio.gather(poll, return_exceptions=True)


if __name__ == '__main__':
    unittest.main()
