"""Real SDK RemoteXPC teardown at retry and media-auth boundaries."""
import asyncio
import socket
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as b
from pymobiledevice3.remote.remote_service import RemoteService
from pymobiledevice3.remote.remotexpc import RemoteXPCConnection


class RemoteCleanupTests(unittest.IsolatedAsyncioTestCase):
    def stalled_service(self):
        raw, peer = socket.socketpair()
        self.addCleanup(raw.close)
        self.addCleanup(peer.close)
        closing = asyncio.Event()
        async def wait_closed():
            closing.set()
            await asyncio.Event().wait()
        # Model a transport whose graceful close needs a peer response. Its
        # abort releases a real socket; close alone deliberately retains it.
        writer = SimpleNamespace(close=Mock(), wait_closed=wait_closed,
                                 transport=SimpleNamespace(abort=Mock(side_effect=raw.close)))
        connection = RemoteXPCConnection(('test-phone', 1))
        connection._writer = writer
        parent = SimpleNamespace(service=SimpleNamespace(close=AsyncMock()))
        service = RemoteService(parent, 'test.hid')
        service._service = connection
        return service, raw, closing

    async def test_candidate_cleanup_timeout_aborts_only_candidate_connection(self):
        service, raw, _ = self.stalled_service()
        session = b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        with patch.object(b, 'HID_CLEANUP_TIMEOUT_SECONDS', .01):
            await session._close_remote_service(service, 'rejected_hid_source')
        self.assertEqual(-1, raw.fileno())
        service.rsd.service.close.assert_not_awaited()
        self.assertEqual('cleanup_stage_failed', session.ipc.emit.call_args.args[0]['code'])

    async def test_cancelled_candidate_cleanup_aborts_and_propagates_cancellation(self):
        service, raw, closing = self.stalled_service()
        session = b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        task = asyncio.create_task(session._close_remote_service(service, 'hid'))
        await asyncio.wait_for(closing.wait(), 1)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual(-1, raw.fileno())
        service.rsd.service.close.assert_not_awaited()

    async def test_failed_media_auth_aborts_stalled_display_before_fallback(self):
        display, raw, _ = self.stalled_service()
        display.__aenter__ = AsyncMock()
        display.start_video_stream = AsyncMock(side_effect=RuntimeError('media rejected 9021'))
        receiver = SimpleNamespace(port=1, close=Mock())
        timeout = asyncio.timeout
        with patch.object(b, 'DisplayService', return_value=display), \
             patch('pymobiledevice3.remote.core_device.screen_stream.open_media_receiver',
                   return_value=(receiver, 'test-host')), \
             patch.object(b.asyncio, 'timeout', side_effect=lambda _: timeout(.01)):
            with self.assertRaisesRegex(RuntimeError, '9021'):
                async with b.touch_session(SimpleNamespace(service=SimpleNamespace(address=('test-phone', 1)))):
                    self.fail('failed authentication cannot yield HID')
        self.assertEqual(-1, raw.fileno())
        receiver.close.assert_called_once()
        display.rsd.service.close.assert_not_awaited()

    async def test_cancel_during_media_hid_close_still_releases_receiver_and_display(self):
        hid, _, closing = self.stalled_service()
        hid.__aenter__ = AsyncMock()
        display = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
                                  start_video_stream=AsyncMock(return_value=None))
        drained = asyncio.Event()
        async def receive():
            try: await asyncio.Event().wait()
            finally: drained.set()
        receiver = SimpleNamespace(port=1, close=Mock(), recv=receive)
        async def run():
            async with b.touch_session(SimpleNamespace(service=SimpleNamespace(address=('phone', 1)))):
                pass
        with patch.object(b, 'DisplayService', return_value=display), \
             patch.object(b, 'UniversalHIDServiceService', return_value=hid), \
             patch('pymobiledevice3.remote.core_device.screen_stream.open_media_receiver',
                   return_value=(receiver, 'host')):
            task = asyncio.create_task(run())
            await asyncio.wait_for(closing.wait(), 1)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        receiver.close.assert_called_once()
        display.__aexit__.assert_awaited_once()
        self.assertTrue(drained.is_set())

    async def test_cancel_during_session_hid_close_still_releases_remaining_resources(self):
        hid, _, closing = self.stalled_service()
        session = b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        session.hid, session._owns_hid = hid, True
        display = session.display = SimpleNamespace(__aexit__=AsyncMock())
        rsd = session.rsd = SimpleNamespace(__aexit__=AsyncMock())
        dial_plane = session.dial_plane = SimpleNamespace(__aexit__=AsyncMock())
        receiver = session.transport = SimpleNamespace(close=Mock())
        task = asyncio.create_task(session._cleanup())
        await asyncio.wait_for(closing.wait(), 1)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        display.__aexit__.assert_awaited_once()
        rsd.__aexit__.assert_awaited_once()
        dial_plane.__aexit__.assert_awaited_once()
        receiver.close.assert_called_once()
        self.assertIsNone(session.hid)
        self.assertFalse(session._owns_hid)

    async def test_pasteboard_lock_cannot_block_or_cancel_remaining_session_teardown(self):
        for cancel in (False, True):
            with self.subTest(cancel=cancel):
                session = b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
                display = session.display = SimpleNamespace(__aexit__=AsyncMock())
                pasteboard = session._pasteboard_service = SimpleNamespace(__aexit__=AsyncMock())
                await session._pasteboard_lock.acquire()
                with patch.object(b, 'HID_CLEANUP_TIMEOUT_SECONDS', .01):
                    task = asyncio.create_task(session._cleanup())
                    try:
                        if cancel:
                            await asyncio.sleep(.001)
                            task.cancel()
                            with self.assertRaises(asyncio.CancelledError): await task
                        else:
                            await asyncio.wait_for(task, .5)
                    finally:
                        session._pasteboard_lock.release()
                pasteboard.__aexit__.assert_awaited_once()
                display.__aexit__.assert_awaited_once()
                self.assertIsNone(session._pasteboard_service)
