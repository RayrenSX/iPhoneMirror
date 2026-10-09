"""Control Lockdown ownership, shared handshake deadline and cancellation."""
import asyncio
import contextlib
import socket
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as b
from pymobiledevice3 import lockdown as sdk


class LockdownBoundaryTests(unittest.IsolatedAsyncioTestCase):
    def service_client(self, connection):
        # Exercise the real named-service factory, without a phone or TLS keys.
        cls = getattr(b, 'BridgeUsbmuxLockdownClient', sdk.UsbmuxLockdownClient)
        client = object.__new__(cls)
        client.get_service_connection_attributes = AsyncMock(return_value={'Port':123, 'EnableServiceSSL':True})
        client.create_service_connection = AsyncMock(return_value=connection)
        client.ssl_file = lambda: contextlib.nullcontext('test-certificate')
        return client

    async def test_failed_named_service_ssl_closes_unreturned_connection(self):
        from pymobiledevice3.service_connection import ServiceConnection
        for error in (b.PasswordRequiredError(), asyncio.CancelledError()):
            with self.subTest(error=type(error).__name__):
                raw, peer = socket.socketpair()
                self.addCleanup(raw.close); self.addCleanup(peer.close)
                connection = ServiceConnection(raw)
                connection.ssl_start = AsyncMock(side_effect=error)
                client = self.service_client(connection)
                with self.assertRaises(type(error)):
                    await client.start_lockdown_service('test.image.service', include_escrow_bag=True)
                self.assertEqual(-1, raw.fileno())
                client.get_service_connection_attributes.assert_awaited_once_with('test.image.service', include_escrow_bag=True)
                connection.ssl_start.assert_awaited_once_with('test-certificate')

    async def test_successful_named_service_transfers_connection_without_closing(self):
        connection = SimpleNamespace(ssl_start=AsyncMock(), close=AsyncMock())
        client = self.service_client(connection)
        self.assertIs(connection, await client.start_lockdown_service('test.image.service'))
        connection.ssl_start.assert_awaited_once_with('test-certificate')
        connection.close.assert_not_awaited()

    async def test_named_service_without_ssl_preserves_plain_connection(self):
        connection = SimpleNamespace(ssl_start=AsyncMock(), close=AsyncMock())
        client = self.service_client(connection)
        client.get_service_connection_attributes.return_value = {'Port': 123}
        self.assertIs(connection, await client.start_lockdown_service('test.plain.service'))
        client.create_service_connection.assert_awaited_once_with(123)
        connection.ssl_start.assert_not_awaited()
        connection.close.assert_not_awaited()

    async def test_named_service_cleanup_timeout_preserves_ssl_failure(self):
        from pymobiledevice3.service_connection import ServiceConnection
        raw, peer = socket.socketpair()
        self.addCleanup(raw.close); self.addCleanup(peer.close)
        connection = ServiceConnection(raw)
        async def stalled(): await asyncio.Event().wait()
        writer = SimpleNamespace(close=Mock(), wait_closed=stalled,
                                 transport=SimpleNamespace(abort=Mock()))
        connection.writer = writer
        connection.ssl_start = AsyncMock(side_effect=b.PasswordRequiredError())
        with patch.object(b, 'LOCKDOWN_CLEANUP_TIMEOUT_SECONDS', .01):
            with self.assertRaises(b.PasswordRequiredError):
                await self.service_client(connection).start_lockdown_service('test.image.service')
        self.assertEqual(-1, raw.fileno())
        writer.transport.abort.assert_called_once()

    async def test_adopted_lockdown_cleanup_deadline_releases_saved_socket(self):
        from pymobiledevice3.service_connection import ServiceConnection
        raw, peer = socket.socketpair()
        self.addCleanup(raw.close); self.addCleanup(peer.close)
        connection = ServiceConnection(raw)
        async def stalled(): await asyncio.Event().wait()
        writer = SimpleNamespace(close=Mock(), wait_closed=stalled,
                                 transport=SimpleNamespace(abort=Mock()))
        connection.writer = writer
        client = object.__new__(b.BridgeUsbmuxLockdownClient)
        client.service = connection
        with self.assertRaises(TimeoutError):
            async with asyncio.timeout(.01):
                await client.close()
        self.assertEqual(-1, raw.fileno())
        writer.transport.abort.assert_called_once()

    async def test_adopted_lockdown_cleanup_cancellation_releases_saved_socket(self):
        from pymobiledevice3.service_connection import ServiceConnection
        raw, peer = socket.socketpair()
        self.addCleanup(raw.close); self.addCleanup(peer.close)
        connection = ServiceConnection(raw)
        closing = asyncio.Event()
        async def stalled():
            closing.set()
            await asyncio.Event().wait()
        writer = SimpleNamespace(close=Mock(), wait_closed=stalled,
                                 transport=SimpleNamespace(abort=Mock()))
        connection.writer = writer
        client = object.__new__(b.BridgePlistUsbmuxLockdownClient)
        client.service = connection
        task = asyncio.create_task(client.close())
        await asyncio.wait_for(closing.wait(), 1)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual(-1, raw.fileno())
        writer.transport.abort.assert_called_once()

    async def test_success_preserves_mux_protocol_buid_identity_and_ownership(self):
        for plist in (False, True):
            service = SimpleNamespace(mux_device=SimpleNamespace(serial='device-from-mux'),
                                      close=AsyncMock(), writer=None, socket=None)
            class Mux:
                async def __aenter__(self): return self
                async def __aexit__(self, *_): pass
            class PlistMux(Mux):
                async def get_buid(self): return 'apple-buid'
            expected = object()
            cls = sdk.PlistUsbmuxLockdownClient if plist else sdk.UsbmuxLockdownClient
            with self.subTest(plist=plist), \
                 patch.object(sdk.ServiceConnection, 'create_using_usbmux', AsyncMock(return_value=service)) as open_service, \
                 patch.object(sdk.usbmux, 'create_mux', AsyncMock(return_value=PlistMux() if plist else Mux())), \
                 patch.object(sdk, 'PlistMuxConnection', PlistMux), \
                 patch.object(cls, 'create', AsyncMock(return_value=expected)) as create:
                actual = await b.create_using_usbmux(serial='requested', connection_type='Network', autopair=False)
            self.assertIs(actual, expected)
            open_service.assert_awaited_once_with('requested', sdk.SERVICE_PORT, connection_type='Network')
            create.assert_awaited_once_with(service, identifier='device-from-mux',
                system_buid='apple-buid' if plist else sdk.SYSTEM_BUID, autopair=False)
            service.close.assert_not_awaited()

    async def test_failed_initialization_bounds_cleanup_and_aborts_saved_handles(self):
        async def close():
            service.writer = service.socket = None
            await asyncio.Event().wait()
        writer = SimpleNamespace(transport=SimpleNamespace(abort=Mock()))
        sock = SimpleNamespace(close=Mock())
        service = SimpleNamespace(mux_device=SimpleNamespace(serial='test-device'),
                                  close=close, writer=writer, socket=sock)
        class Mux:
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
        with patch.object(sdk.ServiceConnection, 'create_using_usbmux', AsyncMock(return_value=service)), \
             patch.object(sdk.usbmux, 'create_mux', AsyncMock(return_value=Mux())), \
             patch.object(sdk.UsbmuxLockdownClient, 'create', AsyncMock(side_effect=b.NotPairedError('not trusted'))), \
             patch.object(b, 'LOCKDOWN_CLEANUP_TIMEOUT_SECONDS', .01):
            with self.assertRaises(b.NotPairedError):
                await asyncio.wait_for(b.create_using_usbmux(serial='test-device'), .5)
        writer.transport.abort.assert_called_once()
        sock.close.assert_called_once()

    async def test_cancelled_buid_lookup_closes_already_open_device_socket(self):
        entered = asyncio.Event()
        async def stalled():
            entered.set()
            await asyncio.Event().wait()
        service = SimpleNamespace(mux_device=SimpleNamespace(serial='test-device'),
                                  close=AsyncMock(), writer=None, socket=None)
        class Mux:
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
            get_buid = staticmethod(stalled)
        with patch.object(sdk.ServiceConnection, 'create_using_usbmux', AsyncMock(return_value=service)), \
             patch.object(sdk, 'PlistMuxConnection', Mux), \
             patch.object(sdk.usbmux, 'create_mux', AsyncMock(return_value=Mux())):
            task = asyncio.create_task(b.create_using_usbmux(serial='test-device', connection_type='USB', autopair=False))
            await entered.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        service.close.assert_awaited_once()

    async def test_handshake_budget_covers_all_retry_attempts(self):
        calls = []
        async def create(**_):
            calls.append(True)
            await asyncio.sleep(.02)
            raise b.ConnectionFailedError()
        with patch.object(b, 'LOCKDOWN_CONNECT_TIMEOUT_SECONDS', .05, create=True), \
             patch.object(b, 'LOCKDOWN_RETRY_DELAY_SECONDS', 0), \
             patch.object(b, 'create_using_usbmux', create):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await b.create_lockdown_with_retry(SimpleNamespace(emit=AsyncMock()), 'test-device', 'USB')
        self.assertEqual(raised.exception.code, 'apple_device_connection_timeout')
        self.assertLess(len(calls), b.LOCKDOWN_CONNECT_ATTEMPTS)

    async def test_remote_ddi_handshake_timeout_is_actionable(self):
        s = b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, 'test-device', 'wireless')
        with patch.object(s, '_create_lockdown_with_retry', AsyncMock(side_effect=TimeoutError())), \
             patch.object(s, '_preflight_developer_environment', AsyncMock()) as prepare:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await s._prepare_ddi_for_remote_pairing()
        self.assertEqual(raised.exception.code, 'apple_device_connection_timeout')
        prepare.assert_not_awaited()

    async def test_handshake_cancel_does_not_retry_or_report_timeout(self):
        entered = asyncio.Event()
        async def create(**_):
            entered.set()
            await asyncio.Event().wait()
        with patch.object(b, 'create_using_usbmux', AsyncMock(side_effect=create)) as factory:
            task = asyncio.create_task(b.create_lockdown_with_retry(
                SimpleNamespace(emit=AsyncMock()), 'test-device', 'Network'))
            await entered.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        factory.assert_awaited_once()
