"""A wedged developer pasteboard helper recovers without resetting input."""
import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


class PasteboardRecoveryTests(unittest.IsolatedAsyncioTestCase):
    def session(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        session.rsd = object()
        session.hid = object()
        return session

    def hub(self, processes=None):
        if processes is None:
            processes = [{'name': 'dtpasteboardd', 'isApplication': False, 'pid': 123,
                          'realAppName': '/System/Developer/usr/libexec/dtpasteboardd'}]
        info = SimpleNamespace(invoke=AsyncMock(return_value=processes))
        control = SimpleNamespace(invoke=AsyncMock())
        hub = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
            dtx=SimpleNamespace(open_channel=AsyncMock(side_effect=[info, control])))
        return hub, info, control

    async def test_restarts_exact_helper_with_cooldown_and_preserves_input(self):
        session = self.session()
        rsd, hid, baseline = session.rsd, session.hid, session._pasteboard_last_text
        hub, info, control = self.hub()
        with patch.object(bridge, 'PasteboardRecoveryHub', return_value=hub) as constructor:
            self.assertTrue(await session._restart_failed_pasteboard())
            self.assertFalse(await session._restart_failed_pasteboard())
        constructor.assert_called_once_with(rsd)
        info.invoke.assert_awaited_once_with('runningProcesses')
        control.invoke.assert_awaited_once_with('sendSignal:toPid:', 9, 123)
        hub.__aexit__.assert_awaited_once()
        self.assertIs(session.rsd, rsd)
        self.assertIs(session.hid, hid)
        self.assertIs(session._pasteboard_last_text, baseline)
        self.assertTrue(session._pasteboard_refresh_required)

    async def test_never_signals_ambiguous_or_unrelated_processes(self):
        good = {'name': 'dtpasteboardd', 'isApplication': False, 'pid': 123,
                'realAppName': '/System/Developer/usr/libexec/dtpasteboardd'}
        for processes in ([], [good, good], [dict(good, pid=1)],
                          [dict(good, isApplication=True)], [dict(good, pid=True)],
                          [dict(good, realAppName='/Applications/Example.app/dtpasteboardd')]):
            session = self.session()
            hub, _, control = self.hub(processes)
            with patch.object(bridge, 'PasteboardRecoveryHub', return_value=hub):
                self.assertFalse(await session._restart_failed_pasteboard())
            control.invoke.assert_not_awaited()
            hub.__aexit__.assert_awaited_once()

    async def test_each_new_interrupted_read_can_recover_once_during_cooldown(self):
        session = self.session()
        for serial in (1, 2):
            session._pasteboard_full_read_serial = serial
            session._pasteboard_full_read_interrupted = True
            hub, _, control = self.hub()
            with patch.object(bridge, 'PasteboardRecoveryHub', return_value=hub):
                self.assertTrue(await session._restart_failed_pasteboard(for_write=True))
                self.assertFalse(await session._restart_failed_pasteboard(for_write=True))
            control.invoke.assert_awaited_once_with('sendSignal:toPid:', 9, 123)

    async def test_failed_interruption_recovery_keeps_backoff_for_the_same_read(self):
        session = self.session()
        session._pasteboard_full_read_serial = 1
        session._pasteboard_full_read_interrupted = True
        hub, _, control = self.hub()
        hub.__aenter__.side_effect = TimeoutError()
        with patch.object(bridge, 'PasteboardRecoveryHub', return_value=hub) as constructor:
            self.assertFalse(await session._restart_failed_pasteboard(for_write=True))
            self.assertFalse(await session._restart_failed_pasteboard(for_write=True))
        constructor.assert_called_once()
        control.invoke.assert_not_awaited()

    async def test_gives_queued_writes_and_tunnel_recovery_priority(self):
        for state in ('writer_before', 'writer_during', 'generation', 'rsd', 'recovering'):
            session = self.session()
            hub, _, control = self.hub()
            if state == 'writer_before':
                session._pasteboard_writers_waiting = 1
            async def connect():
                if state == 'writer_during': session._pasteboard_writers_waiting = 1
                if state == 'generation': session._generation += 1
                if state == 'rsd': session.rsd = object()
                if state == 'recovering': session._recovering = True
            hub.__aenter__.side_effect = connect
            with patch.object(bridge, 'PasteboardRecoveryHub', return_value=hub):
                self.assertFalse(await session._restart_failed_pasteboard())
            control.invoke.assert_not_awaited()

    async def test_timeout_and_cancellation_close_channel_and_release_lock(self):
        for cancel in (False, True):
            session = self.session()
            hub, _, control = self.hub()
            async def stalled():
                if cancel: raise asyncio.CancelledError()
                await asyncio.Event().wait()
            hub.__aenter__.side_effect = stalled
            with patch.object(bridge, 'PasteboardRecoveryHub', return_value=hub), \
                 patch.object(bridge, 'PASTEBOARD_RESTART_TIMEOUT_SECONDS', 0.01):
                if cancel:
                    with self.assertRaises(asyncio.CancelledError):
                        await session._restart_failed_pasteboard()
                else:
                    self.assertFalse(await session._restart_failed_pasteboard())
            hub.__aexit__.assert_awaited_once()
            control.invoke.assert_not_awaited()
            self.assertFalse(session._pasteboard_lock.locked())

    async def test_cancelled_real_hub_handshake_retains_connection_for_cleanup(self):
        hub = object.__new__(bridge.PasteboardRecoveryHub)
        hub._dtx = None
        hub._owns_dtx = True
        hub._service_name = bridge.PasteboardRecoveryHub.SERVICE_NAME
        hub._strip_ssl = False
        hub.sent_capabilities = {}
        async def handshake():
            raise asyncio.CancelledError()
        connection = SimpleNamespace(connect=AsyncMock(side_effect=handshake), aclose=AsyncMock())
        hub._open_dtx_connection = AsyncMock(return_value=connection)
        with self.assertRaises(asyncio.CancelledError):
            await hub.__aenter__()
        await hub.__aexit__(None, None, None)
        connection.aclose.assert_awaited_once()
        self.assertIsNone(hub._dtx)

    async def test_owned_diagnostic_transport_stops_reads_before_sdk_feeds_eof(self):
        for owned in (False, True):
            hub = object.__new__(bridge.PasteboardRecoveryHub)
            reader = asyncio.StreamReader()
            paused = False
            def pause():
                nonlocal paused
                paused = True
            async def close_sdk():
                reader.feed_eof()
                if not paused:
                    reader.feed_data(b'late sysmon sample')
            transport = SimpleNamespace(pause_reading=Mock(side_effect=pause))
            connection = SimpleNamespace(_writer=SimpleNamespace(transport=transport),
                                         aclose=AsyncMock(side_effect=close_sdk))
            hub._dtx = connection
            hub._owns_dtx = owned
            await hub.close()
            self.assertEqual(owned, paused)
            self.assertEqual(1 if owned else 0, connection.aclose.await_count)

    async def test_poll_only_recovers_after_consecutive_failures_and_then_publishes(self):
        session = self.session()
        # An isolated failure followed by success must not count toward the
        # later run of three failures. The fifth attempt is the third in a row.
        reads = iter([TimeoutError(), None, TimeoutError(), TimeoutError(), TimeoutError(), None])
        attempts = 0
        recovered_at = []
        async def publish():
            nonlocal attempts
            attempts += 1
            if attempts > 6: raise asyncio.CancelledError()
            error = next(reads)
            if error: raise error
        async def restart():
            recovered_at.append(attempts)
            return True
        session._publish_device_pasteboard = publish
        session._restart_failed_pasteboard = restart
        with patch.object(bridge, 'PASTEBOARD_POLL_INTERVAL_SECONDS', 0):
            with self.assertRaises(asyncio.CancelledError):
                await session._poll_device_pasteboard()
        self.assertEqual([5], recovered_at)
        statuses = [call.args[0].get('code') for call in session.ipc.emit.await_args_list]
        self.assertEqual(2, statuses.count('clipboard_poll_recovered'))


if __name__ == '__main__':
    unittest.main()
