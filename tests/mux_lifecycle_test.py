"""Cancellation and partial-connection ownership in the capture mux server."""
import asyncio
import queue
import sys
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from iostouch.qt.usbmuxd_server import UsbmuxdServer, UsbmuxdThread
from iostouch.qt.usbmux_usb import MuxError


class MuxThreadLifecycleTests(unittest.TestCase):
    def test_start_timeout_never_advertises_unbound_address_and_retires_thread(self):
        server = UsbmuxdThread(Mock(), 'test-device')
        cancelled = threading.Event()
        async def stalled():
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        server.server.start = stalled
        original_wait = server._started.wait
        # Shorten only this instance's startup wait, not all thread events.
        server._started.wait = lambda timeout=None: original_wait(.03)
        try:
            with self.assertRaises(MuxError):
                server.start()
            self.assertFalse(server._thread.is_alive())
            self.assertTrue(cancelled.is_set())
        finally:
            server.stop()

    def test_stop_during_startup_closes_listener_created_before_cancellation(self):
        server = UsbmuxdThread(Mock(), 'test-device')
        listening = threading.Event()
        original_start = server.server.start
        async def stalled_after_bind():
            await original_start()
            listening.set()
            await asyncio.Event().wait()
        server.server.start = stalled_after_bind
        result = []
        def launch():
            try:
                result.append(server.start())
            except BaseException as error:
                result.append(error)
        caller = threading.Thread(target=launch)
        caller.start()
        try:
            self.assertTrue(listening.wait(1))
            server.stop()
            caller.join(2)
            self.assertFalse(caller.is_alive())
            self.assertIsInstance(result[0], MuxError)
            self.assertIsNone(server.server._server)
            self.assertFalse(server._thread.is_alive())
        finally:
            server.stop()
            caller.join(2)


class MuxClientLifecycleTests(unittest.IsolatedAsyncioTestCase):
    request = {'DeviceID': 1, 'PortNumber': 0x7ef2}

    async def test_failed_success_reply_releases_device_connection(self):
        conn = SimpleNamespace(sport=1, close=Mock())
        server = UsbmuxdServer(SimpleNamespace(connect=lambda _: conn), 'test-device')
        error = ConnectionResetError('client closed before connect reply')
        writer = Mock(drain=AsyncMock(side_effect=error))
        with self.assertRaises(ConnectionResetError) as raised:
            await server._handle_connect(1, self.request, Mock(), writer)
        self.assertIs(raised.exception, error)
        conn.close.assert_called_once()

    async def test_cancelled_connect_closes_late_worker_result(self):
        started, release, closed = threading.Event(), threading.Event(), threading.Event()
        conn = SimpleNamespace(sport=1, close=Mock(side_effect=closed.set))
        def connect(_):
            started.set()
            release.wait(3)
            return conn
        server = UsbmuxdServer(SimpleNamespace(connect=connect), 'test-device')
        writer = Mock(drain=AsyncMock())
        task = asyncio.create_task(server._handle_connect(1, self.request, Mock(), writer))
        try:
            self.assertTrue(await asyncio.to_thread(started.wait, 1))
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
            release.set()
            self.assertTrue(await asyncio.to_thread(closed.wait, 1),
                            'a cancelled client abandoned a late successful USB connection')
            writer.write.assert_not_called()
            conn.close.assert_called_once()
        finally:
            release.set()
            if not task.done():
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)

    async def test_cancelled_bridge_closes_connection_and_both_pumps(self):
        reading = asyncio.Event()
        released = threading.Event()
        async def read(_):
            reading.set()
            await asyncio.Event().wait()
        def recv(*args, **kwargs):
            released.wait(2)
            return b''
        conn = SimpleNamespace(sport=1, dport=2, closed=False,
                               close=Mock(side_effect=released.set), recv=recv)
        server = UsbmuxdServer(Mock(), 'test-device')
        before = asyncio.all_tasks()
        task = asyncio.create_task(server._bridge(SimpleNamespace(read=read), Mock(), conn))
        try:
            await asyncio.wait_for(reading.wait(), 1)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
            conn.close.assert_called_once()
            remaining = asyncio.all_tasks() - before - {task}
            self.assertFalse(remaining, 'bridge pumps outlived their cancelled owner')
        finally:
            released.set()
            remaining = asyncio.all_tasks() - before
            for child in remaining:
                child.cancel()
            await asyncio.gather(*remaining, return_exceptions=True)

    async def test_real_socket_exchange_and_client_eof_retire_connection(self):
        incoming = queue.Queue()
        closed = threading.Event()
        sent = []
        def send(data):
            sent.append(data)
            incoming.put(b'pong')
        def recv(*args, **kwargs):
            try:
                return incoming.get(timeout=.1)
            except queue.Empty:
                return b''
        def close():
            conn.closed = True
            closed.set()
            incoming.put(b'')
        conn = SimpleNamespace(sport=1, dport=2, closed=False, close_reason='',
                               bytes_rx=0, bytes_tx=0, send=send, recv=recv,
                               close=Mock(side_effect=close))
        server = UsbmuxdServer(SimpleNamespace(connect=lambda _: conn), 'test-device')
        port = await server.start()
        writer = None
        try:
            reader, writer = await asyncio.open_connection('127.0.0.1', port)
            writer.write(server._frame(7, dict(self.request, MessageType='Connect')))
            await writer.drain()
            tag, result = await asyncio.wait_for(server._read_msg(reader), 1)
            self.assertEqual((tag, result['Number']), (7, 0))
            writer.write(b'ping')
            await writer.drain()
            self.assertEqual(await asyncio.wait_for(reader.readexactly(4), 1), b'pong')
            self.assertEqual(sent, [b'ping'])
            writer.close()
            await writer.wait_closed()
            self.assertTrue(await asyncio.to_thread(closed.wait, 1))
            conn.close.assert_called_once()
        finally:
            if writer is not None:
                writer.close()
                await writer.wait_closed()
            await asyncio.wait_for(server.stop(), 1)
