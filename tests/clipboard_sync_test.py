"""Device clipboard polling regressions; no physical device or desktop access."""
import asyncio
import collections
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

    def test_paste_queue_budget_covers_an_inflight_poll(self):
        # A SET is queued behind an active PULL on the same RemoteXPC service.
        # Its queue budget must include the entire PULL operation and bounded
        # cleanup, otherwise Ctrl+V fails before set_text is ever invoked.
        self.assertGreater(
            bridge.PASTEBOARD_WRITE_QUEUE_TIMEOUT_SECONDS,
            bridge.PASTEBOARD_OPERATION_TIMEOUT_SECONDS)

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

    async def test_windows_paste_retries_a_timed_out_set(self):
        session = self.session()
        completed = asyncio.Event()
        attempts = 0

        async def poll():
            await asyncio.Event().wait()

        async def write(text):
            nonlocal attempts
            attempts += 1
            if attempts == 1:
                raise TimeoutError('pasteboard reply timed out')

        async def keyboard(usages):
            if not usages:
                completed.set()

        async def read_messages():
            yield {'kind': bridge.PASTE_TEXT_MESSAGE_KIND, 'text': 'Windows text'}
            await completed.wait()

        session._poll_device_pasteboard = poll
        session._write_device_pasteboard = write
        session._send_keyboard_report = keyboard
        session._ping_hid = lambda *_args, **_kwargs: asyncio.sleep(0)
        session.ipc.read_messages = read_messages
        await session._serve()

        self.assertEqual(2, attempts)
        self.assertEqual([], [event for event in session.ipc.events
                              if event.get('code') == 'paste_failed'])

    async def test_consecutive_windows_pastes_each_retry_without_dropping_order(self):
        session = self.session()
        completed = asyncio.Event()
        attempts = collections.Counter()
        applied = []

        async def poll():
            await asyncio.Event().wait()

        async def write(text):
            attempts[text] += 1
            if attempts[text] == 1:
                raise TimeoutError(f'{text} reply timed out')
            applied.append(text)

        async def keyboard(usages):
            if not usages and len(applied) == 3:
                completed.set()

        async def read_messages():
            for text in ('one', 'two', 'three'):
                yield {'kind': bridge.PASTE_TEXT_MESSAGE_KIND, 'text': text}
            await completed.wait()

        session._poll_device_pasteboard = poll
        session._write_device_pasteboard = write
        session._send_keyboard_report = keyboard
        session._ping_hid = lambda *_args, **_kwargs: asyncio.sleep(0)
        session.ipc.read_messages = read_messages
        await session._serve()

        self.assertEqual({'one': 2, 'two': 2, 'three': 2}, dict(attempts))
        self.assertEqual(['one', 'two', 'three'], applied)
        self.assertEqual([], [event for event in session.ipc.events
                              if event.get('code') == 'paste_failed'])

    async def test_hid_timeout_never_replays_a_paste_and_releases_modifiers(self):
        for failed_report in (1, 2, 3):
            with self.subTest(failed_report=failed_report):
                session = self.session('wireless')
                finished = asyncio.Event()
                writes, reports = [], []
                original_emit = session.ipc.emit

                async def idle():
                    await asyncio.Event().wait()

                async def write(text):
                    writes.append(text)

                async def keyboard(usages):
                    reports.append(list(usages))
                    if len(reports) == failed_report:
                        # Bytes reached iOS, but the transport did not confirm.
                        raise TimeoutError('HID reply lost after sending')

                async def emit(event):
                    await original_emit(event)
                    if event.get('code') == 'paste_failed':
                        finished.set()

                async def messages():
                    yield {'kind': bridge.PASTE_TEXT_MESSAGE_KIND, 'text': 'paste once'}
                    await asyncio.wait_for(finished.wait(), 3)

                session.ipc.emit = emit
                session.ipc.read_messages = messages
                session._poll_device_pasteboard = idle
                session._write_device_pasteboard = write
                session._send_keyboard_report = keyboard
                session._ping_hid = lambda: asyncio.sleep(0)
                await asyncio.wait_for(session._serve(), 4)
                self.assertEqual(['paste once'], writes)
                self.assertEqual([], reports[-1], 'A failed Command down still needs release')
                self.assertLessEqual(reports.count([0xE3, 0x19]), 1)

    async def test_failed_or_cancelled_set_never_sends_paste_keys(self):
        for error, expected_attempts in ((TimeoutError(), 2), (asyncio.CancelledError(), 1)):
            with self.subTest(error=type(error).__name__):
                session = self.session()
                writes, reports = [], []

                async def write(text):
                    writes.append(text)
                    raise error

                async def keyboard(usages):
                    reports.append(usages)

                session._write_device_pasteboard = write
                session._send_keyboard_report = keyboard
                with self.assertRaises(type(error)):
                    await session._apply_paste_text('unavailable')
                self.assertEqual(expected_attempts, len(writes))
                self.assertEqual([], reports)

    async def test_device_text_representations(self):
        text = '中文 / café / 😀'
        for uti, payload, expected in (
            ('public.utf8-plain-text', text.encode('utf-8'), text),
            ('public.utf16-plain-text', text.encode('utf-16-le'), text),
            ('public.utf16-external-plain-text', text.encode('utf-16'), text),
            ('public.utf16-external-plain-text', b'\xfe\xff' + text.encode('utf-16-be'), text),
            ('public.url', b'https://example.com/path', 'https://example.com/path'),
            ('public.utf8-plain-text', b'', ''),
        ):
            with self.subTest(uti=uti, payload=payload):
                session = self.session()

                class TypedPasteboard(Pasteboard):
                    async def get(self, **kwargs):
                        return {'pasteboard': {'items': [{
                            'types': [uti], 'data': {uti: {'data': payload}},
                        }]}}

                with patch.object(bridge, 'PasteboardService', TypedPasteboard):
                    self.assertEqual(expected, await session._read_device_pasteboard())

    async def test_promised_text_resolves_on_fresh_connection_with_bounded_fallback(self):
        for remains_promised in (False, True):
            with self.subTest(remains_promised=remains_promised):
                session = self.session()
                connections, policies = [], []

                class PromisedPasteboard(Pasteboard):
                    def __init__(self, rsd):
                        super().__init__(rsd)
                        self.requests = 0
                        connections.append(self)

                    async def get(self, data_policy=None):
                        self.requests += 1
                        self_test.assertEqual(1, self.requests)
                        policies.append(data_policy)
                        datum = ({'isPromised': True, 'isAvailable': True}
                                 if remains_promised or len(connections) == 1 else
                                 {'data': 'resolved 中文'.encode('utf-8')})
                        return {'pasteboard': {'items': [{
                            'types': ['public.html', 'public.utf8-plain-text'],
                            'data': {'public.html': {'data': b'<p>rich text</p>'},
                                     'public.utf8-plain-text': datum},
                        }]}}

                self_test = self
                with patch.object(bridge, 'PasteboardService', PromisedPasteboard):
                    if remains_promised:
                        with self.assertRaisesRegex(RuntimeError, 'unresolved'):
                            await session._read_device_pasteboard()
                    else:
                        self.assertEqual('resolved 中文', await session._read_device_pasteboard())
                self.assertEqual([{'promiseSecondary': {}}, {'allResolved': {}}], policies)
                self.assertTrue(all(connection.closed for connection in connections))

    async def test_promised_text_fallback_shares_the_read_timeout(self):
        session = self.session()
        connections = []

        class SlowPasteboard(Pasteboard):
            def __init__(self, rsd):
                super().__init__(rsd)
                connections.append(self)

            async def get(self, data_policy=None):
                await asyncio.sleep(0.15)
                datum = ({'isPromised': True} if len(connections) == 1 else {'data': b'text'})
                return {'items': [{'types': ['public.text'], 'data': {'public.text': datum}}]}

        with patch.object(bridge, 'PasteboardService', SlowPasteboard), \
                patch.object(bridge, 'PASTEBOARD_OPERATION_TIMEOUT_SECONDS', 0.25):
            with self.assertRaises(TimeoutError):
                await session._read_device_pasteboard()
        self.assertEqual(2, len(connections))
        self.assertTrue(all(connection.closed for connection in connections))

    async def test_non_text_promises_do_not_request_resolution(self):
        session = self.session()
        policies = []

        class ImagePasteboard(Pasteboard):
            async def get(self, data_policy=None):
                policies.append(data_policy)
                return {'items': [{'types': ['public.png'],
                                   'data': {'public.png': {'isPromised': True}}}]}

        with patch.object(bridge, 'PasteboardService', ImagePasteboard):
            self.assertIsNone(await session._read_device_pasteboard())
        self.assertEqual([{'promiseSecondary': {}}], policies)

    async def test_each_pasteboard_operation_uses_a_fresh_connection(self):
        session = self.session()
        instances = []
        device = {'text': 'device text'}

        def create(rsd):
            result = Pasteboard(rsd)
            result.text = device['text']

            async def set_text(text):
                device['text'] = text
                result.text = text

            result.set_text = set_text
            instances.append(result)
            return result

        with patch.object(bridge, 'PasteboardService', side_effect=create):
            self.assertEqual('device text', await session._read_device_pasteboard())
            await session._write_device_pasteboard('Windows / 中文 / 😀')
            self.assertEqual('Windows / 中文 / 😀', await session._read_device_pasteboard())
            self.assertEqual(3, len(instances))
            self.assertTrue(all(instance.entered == 1 and instance.closed
                                for instance in instances))
            await session._close_device_pasteboard()

    async def test_pull_requests_primary_representations_only(self):
        session = self.session()

        class ProtocolPasteboard(Pasteboard):
            def __init__(self, rsd):
                super().__init__(rsd)
                self.policies = []

            async def get(self, pasteboard_name='general', data_policy=None):
                self.policies.append((pasteboard_name, data_policy))
                return {'pasteboard': {'items': [{
                    'types': ['public.utf8-plain-text'],
                    'data': {'public.utf8-plain-text': {'data': b'primary'}},
                }]}}

        service = ProtocolPasteboard(session.rsd)
        with patch.object(bridge, 'PasteboardService', return_value=service):
            self.assertEqual('primary', await session._read_device_pasteboard())
        self.assertEqual([('general', {'promiseSecondary': {}})], service.policies)
        self.assertTrue(service.closed)

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
            self.assertTrue(replacement.closed)
            self.assertIsNone(session._pasteboard_service)
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
                         [event['text'] for event in session.ipc.events if event['event'] == 'clipboard_text'])
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
            self.assertEqual([], [e for e in session.ipc.events if e['event'] == 'clipboard_text'])
            session._read_device_pasteboard = original_read
            with self.assertRaises(asyncio.CancelledError):
                await session._poll_device_pasteboard()
            self.assertEqual([], [e for e in session.ipc.events if e['event'] == 'clipboard_text'])
        await session._close_device_pasteboard()

    async def test_host_write_during_ipc_emit_keeps_new_baseline(self):
        session = self.session()
        service = Pasteboard(session.rsd)
        original_emit = session.ipc.emit
        ticks = 0

        async def emit(event):
            if event['event'] == 'clipboard_text':
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
        self.assertEqual(['device text'], [e['text'] for e in session.ipc.events if e['event'] == 'clipboard_text'])
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
            self.assertEqual([], [e for e in session.ipc.events if e['event'] == 'clipboard_text'])
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
                await asyncio.sleep(0.15)
                await super().__aexit__(*args)

        class ReadyWrite(Pasteboard):
            async def set_text(self, text):
                await asyncio.sleep(0.01)
                writes.append(text)

        async def keyboard(usages):
            reports.append(usages)

        session._send_keyboard_report = keyboard
        # Leave headroom for the event loop under the full regression suite;
        # the test needs a timed-out read, not a scheduler-sensitive deadline.
        with patch.object(bridge, 'PASTEBOARD_OPERATION_TIMEOUT_SECONDS', 0.10), \
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
        self.assertEqual(['A'], [event['text'] for event in session.ipc.events if event['event'] == 'clipboard_text'])
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

                services = iter([stuck])

                def create(_rsd):
                    try:
                        return next(services)
                    except StopIteration:
                        return ready

                with patch.object(bridge, 'PASTEBOARD_OPERATION_TIMEOUT_SECONDS', 0.02), \
                        patch.object(bridge, 'PasteboardService', side_effect=create):
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
                                 [event['text'] for event in session.ipc.events if event['event'] == 'clipboard_text'])
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

        services = iter([FailedSet(session.rsd)])

        def create(_rsd):
            try:
                return next(services)
            except StopIteration:
                return ready

        with patch.object(bridge, 'PasteboardService', side_effect=create):
            with self.assertRaises(ConnectionError):
                await session._write_device_pasteboard('unconfirmed-A')
            await session._write_device_pasteboard('confirmed-B')
            self.assertEqual(set(), session._pasteboard_unconfirmed_writes)
            self.assertEqual('confirmed-B', session._pasteboard_last_text)
            ready.text = 'unconfirmed-A'  # A new device copy after confirmed B.
            with patch.object(bridge.asyncio, 'sleep', new=stop):
                with self.assertRaises(asyncio.CancelledError):
                    await session._poll_device_pasteboard()
        self.assertEqual(['unconfirmed-A'], [event['text'] for event in session.ipc.events if event['event'] == 'clipboard_text'])
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

    async def test_explicit_read_drops_snapshots_superseded_by_paste_or_recovery(self):
        for mode in ('usb', 'wireless'):
            for change in ('paste', 'recovery', 'recovering'):
                with self.subTest(mode=mode, change=change):
                    session = self.session(mode)

                    async def read():
                        if change == 'paste':
                            session._pasteboard_write_generation += 1
                        elif change == 'recovery':
                            session._generation += 1
                        else:
                            session._recovering = True
                        return 'stale iOS text'

                    session._read_device_pasteboard = read
                    await session._publish_device_pasteboard(force=True)
                    self.assertEqual(['clipboard_read_started', 'clipboard_read_finished'],
                                     [e['event'] for e in session.ipc.events])

    async def test_read_lifecycle_covers_reply_unchanged_error_and_cancellation(self):
        session = self.session()
        values = iter(['fresh', 'fresh', TimeoutError(), asyncio.CancelledError()])

        async def read():
            self.assertEqual('clipboard_read_started', session.ipc.events[-1]['event'])
            value = next(values)
            if isinstance(value, BaseException):
                raise value
            return value

        session._read_device_pasteboard = read
        await session._publish_device_pasteboard()
        await session._publish_device_pasteboard()
        with self.assertRaises(TimeoutError):
            await session._publish_device_pasteboard()
        with self.assertRaises(asyncio.CancelledError):
            await session._publish_device_pasteboard()
        self.assertEqual([1, 2, 3, 4], [e['readId'] for e in session.ipc.events
                                      if e['event'] == 'clipboard_read_started'])
        self.assertEqual([1, 2, 3, 4], [e['readId'] for e in session.ipc.events
                                      if e['event'] == 'clipboard_read_finished'])
        self.assertEqual([{'event': 'clipboard_text', 'readId': 1, 'text': 'fresh'}],
                         [e for e in session.ipc.events if e['event'] == 'clipboard_text'])

    async def test_explicit_read_reconciles_unconfirmed_host_write(self):
        session = self.session()
        session._pasteboard_unconfirmed_writes.add('old Windows copy')

        async def read():
            return 'old Windows copy'

        session._read_device_pasteboard = read
        await session._publish_device_pasteboard(force=True)
        self.assertEqual([], [e for e in session.ipc.events if e['event'] == 'clipboard_text'])
        # A subsequent deliberate read can request unchanged content again.
        await session._publish_device_pasteboard(force=True)
        self.assertEqual(['old Windows copy'], [e['text'] for e in session.ipc.events
                                              if e['event'] == 'clipboard_text'])
if __name__ == '__main__':
    unittest.main()
