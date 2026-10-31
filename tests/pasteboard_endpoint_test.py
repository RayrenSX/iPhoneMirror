"""Recover a stale pasteboard endpoint without touching HID or issuing a paste twice."""
import asyncio
import time
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge

NAME = bridge.PasteboardService.SERVICE_NAME

class PasteboardEndpointTests(unittest.IsolatedAsyncioTestCase):
    async def test_fresh_requests_keep_helper_lease_until_replacement_connects(self):
        session = self.session()
        peers = set()
        idle_exits = []
        requests = []
        class Peer:
            async def __aenter__(self):
                peers.add(self)
                return self
            async def __aexit__(self,*args):
                peers.remove(self)
                if not peers: idle_exits.append(True)
            async def get_text(self):
                requests.append(self)
                return 'text'
            async def set_text(self,text): requests.append(self)
        with patch.object(bridge,'PasteboardService',side_effect=lambda rsd:Peer()):
            for _ in range(3):
                self.assertEqual('text',await session._read_device_pasteboard())
            await session._write_device_pasteboard('host')
            self.assertEqual(4,len(set(requests)))
            self.assertEqual(1,len(peers))
            self.assertEqual([],idle_exits)
            await session._close_device_pasteboard()
        self.assertEqual(0,len(peers))
        self.assertEqual([True],idle_exits)

    async def test_previous_lease_remains_until_new_request_has_replied(self):
        session = self.session()
        peers = set()
        reads = 0
        class Peer:
            async def __aenter__(self): peers.add(self); return self
            async def __aexit__(self, *args): peers.remove(self)
            async def get_text(self):
                nonlocal reads
                reads += 1
                self_test.assertEqual(1 if reads == 1 else 2, len(peers),
                    'The helper can still be accepting the new client after its HTTP/2 handshake')
                return 'complete text'
        self_test = self
        with patch.object(bridge, 'PasteboardService', side_effect=lambda rsd: Peer()):
            await session._read_device_pasteboard()
            await session._read_device_pasteboard()
            await session._read_device_pasteboard()
            self.assertEqual(1, len(peers))
            await session._close_device_pasteboard()
        self.assertEqual(set(), peers)

    def session(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        session.rsd = SimpleNamespace(service=SimpleNamespace(address=('device', 123)),
            open_connection=AsyncMock(), udid='same-device',
            peer_info={'Services': {NAME: {'Port': '100'}, 'hid': {'Port': '200'}}})
        return session

    def discovery(self, identity='same-device', services=None):
        return SimpleNamespace(connect=AsyncMock(), send_device_handshake=AsyncMock(),
            receive_response=AsyncMock(return_value={'Properties': {'UniqueDeviceID': identity},
                'Services': services if services is not None else {NAME: {'Port': '101'}}}),
            __aexit__=AsyncMock())

    def connection_type(self, discovery):
        constructor = Mock(return_value=discovery)
        # Keep the patched symbol a type: production cleanup uses isinstance.
        connection = type('DiscoveryConnection', (), {
            '__new__': lambda cls, *args, **kwargs: constructor(*args, **kwargs)})
        return connection, constructor

    async def test_connection_failure_rediscovery_then_one_set_and_one_paste(self):
        session = self.session()
        old_rsd = session.rsd
        failed = SimpleNamespace(__aenter__=AsyncMock(side_effect=TimeoutError()), __aexit__=AsyncMock())
        live = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
            set_text=AsyncMock(return_value={'command': 'SET_REPLY'}))
        discovery = self.discovery()
        connection_type, connection = self.connection_type(discovery)
        with patch.object(bridge, 'PasteboardService', side_effect=[failed, live]) as factory, \
             patch.object(bridge, 'RemoteXPCConnection', connection_type):
            factory.SERVICE_NAME = NAME
            session._send_keyboard_report = AsyncMock()
            await session._apply_paste_text('中文')
        self.assertIs(session.rsd, old_rsd)
        self.assertEqual({'Port': '200'}, session.rsd.peer_info['Services']['hid'])
        self.assertEqual({'Port': '101'}, session.rsd.peer_info['Services'][NAME])
        connection.assert_called_once_with(('device', 123), open_connection=old_rsd.open_connection)
        live.set_text.assert_awaited_once_with('中文')
        self.assertEqual([[0xE3], [0xE3, 0x19], [0xE3], []],
                         [call.args[0] for call in session._send_keyboard_report.await_args_list])
        failed.__aexit__.assert_awaited_once()
        discovery.__aexit__.assert_awaited_once()
        self.assertFalse(session._pasteboard_refresh_required)
        await session._close_device_pasteboard()

    async def test_request_failure_refreshes_endpoint_and_recovers(self):
        for operation in ('read', 'write'):
            for error in (TimeoutError(), RuntimeError('device pasteboard text is unresolved')):
                with self.subTest(operation=operation, error=type(error).__name__):
                    session = self.session()
                    original_rsd = session.rsd
                    failed = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
                        get_text=AsyncMock(side_effect=error), set_text=AsyncMock(side_effect=error))
                    live = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
                        get_text=AsyncMock(return_value='recovered text'),
                        set_text=AsyncMock(return_value={'command': 'SET_REPLY'}))
                    discovery = self.discovery()
                    connection_type, constructor = self.connection_type(discovery)
                    with patch.object(bridge, 'PasteboardService', side_effect=[failed, live]) as factory, \
                         patch.object(bridge, 'RemoteXPCConnection', connection_type):
                        factory.SERVICE_NAME = NAME
                        async def request():
                            if operation == 'read':
                                return await session._read_device_pasteboard()
                            return await session._write_device_pasteboard('host text')
                        with self.assertRaises(type(error)):
                            await request()
                        self.assertTrue(session._pasteboard_refresh_required)
                        result = await request()
                    if operation == 'read':
                        self.assertEqual('recovered text', result)
                    self.assertIs(original_rsd, session.rsd)
                    self.assertEqual({'Port': '200'}, session.rsd.peer_info['Services']['hid'])
                    self.assertEqual({'Port': '101'}, session.rsd.peer_info['Services'][NAME])
                    constructor.assert_called_once()
                    failed.__aexit__.assert_awaited_once()
                    await session._close_device_pasteboard()
                    live.__aexit__.assert_awaited_once()
                    self.assertFalse(session._pasteboard_refresh_required)

    async def test_restarted_service_retries_setup_until_ready_before_sending_one_set(self):
        session = self.session()
        session._pasteboard_restarted_at = time.monotonic()
        failed = [SimpleNamespace(__aenter__=AsyncMock(side_effect=TimeoutError()),
                                  __aexit__=AsyncMock(), set_text=AsyncMock()) for _ in range(3)]
        live = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
                               set_text=AsyncMock(return_value={'command': 'SET_REPLY'}))
        session._refresh_pasteboard_endpoint = AsyncMock()
        with patch.object(bridge, 'PasteboardService', side_effect=failed + [live]), \
             patch.object(bridge, 'PASTEBOARD_RETRY_DELAY_SECONDS', 0):
            await session._write_device_pasteboard('new host text')
        for connection in failed:
            connection.set_text.assert_not_awaited()
            connection.__aexit__.assert_awaited_once()
        live.set_text.assert_awaited_once_with('new host text')
        self.assertEqual('new host text', session._pasteboard_last_text)

    async def test_an_old_restart_does_not_enable_indefinite_connection_retries(self):
        session = self.session()
        session._pasteboard_restarted_at = time.monotonic() - bridge.PASTEBOARD_RESTART_CONNECT_WINDOW_SECONDS - 1
        session._refresh_pasteboard_endpoint = AsyncMock()
        failed = SimpleNamespace(__aenter__=AsyncMock(side_effect=TimeoutError()), __aexit__=AsyncMock())
        with patch.object(bridge, 'PasteboardService', return_value=failed) as constructor:
            with self.assertRaises(TimeoutError):
                await session._get_device_pasteboard()
        self.assertEqual(2, constructor.call_count)

    async def test_transient_setup_timeout_is_retried_within_the_same_read(self):
        for error in (TimeoutError(), asyncio.IncompleteReadError(b'', 9)):
            with self.subTest(error=type(error).__name__):
                session = self.session()
                failed = SimpleNamespace(__aenter__=AsyncMock(side_effect=error), __aexit__=AsyncMock())
                live = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
                                       get_text=AsyncMock(return_value='complete text'))
                discovery = self.discovery()
                connection_type, constructor = self.connection_type(discovery)
                with patch.object(bridge, 'PasteboardService', side_effect=[failed, live]) as factory, \
                     patch.object(bridge, 'RemoteXPCConnection', connection_type), \
                     patch.object(bridge, 'PASTEBOARD_RETRY_DELAY_SECONDS', 0):
                    factory.SERVICE_NAME = NAME
                    await session._publish_device_pasteboard()
                constructor.assert_called_once()
                failed.__aexit__.assert_awaited_once()
                live.get_text.assert_awaited_once()
                self.assertEqual(['complete text'], [call.args[0]['text']
                    for call in session.ipc.emit.await_args_list if call.args[0]['event'] == 'clipboard_text'])

    async def test_foreign_identity_or_missing_service_never_updates_catalogue(self):
        for identity, services in [('another-device', None), ('same-device', {})]:
            with self.subTest(identity=identity, services=services):
                session = self.session()
                session._pasteboard_refresh_required = True
                discovery = self.discovery(identity, services)
                connection_type, _ = self.connection_type(discovery)
                with patch.object(bridge, 'RemoteXPCConnection', connection_type):
                    with self.assertRaises(RuntimeError):
                        await session._refresh_pasteboard_endpoint()
                self.assertEqual({'Port': '100'}, session.rsd.peer_info['Services'][NAME])
                self.assertTrue(session._pasteboard_refresh_required)
                discovery.__aexit__.assert_awaited_once()

    async def test_cancelled_rediscovery_closes_only_its_own_connection(self):
        session = self.session()
        discovery = self.discovery()
        discovery.connect.side_effect = asyncio.CancelledError()
        connection_type, _ = self.connection_type(discovery)
        with patch.object(bridge, 'RemoteXPCConnection', connection_type):
            with self.assertRaises(asyncio.CancelledError):
                await session._refresh_pasteboard_endpoint()
        discovery.__aexit__.assert_awaited_once()
        self.assertEqual({'Port': '100'}, session.rsd.peer_info['Services'][NAME])

    async def test_unavailable_service_after_refresh_never_sends_paste_chord(self):
        session = self.session()
        discovery = self.discovery(services={NAME: {'Port': '100'}})
        connection_type, _ = self.connection_type(discovery)
        instances = []
        def service(_rsd):
            instance = SimpleNamespace(__aenter__=AsyncMock(side_effect=TimeoutError()),
                __aexit__=AsyncMock(), set_text=AsyncMock())
            instances.append(instance)
            return instance
        session._send_keyboard_report = AsyncMock()
        with patch.object(bridge, 'PasteboardService', side_effect=service) as factory, \
             patch.object(bridge, 'RemoteXPCConnection', connection_type):
            factory.SERVICE_NAME = NAME
            with self.assertRaises(TimeoutError):
                await session._apply_paste_text('中文')
        session._send_keyboard_report.assert_not_awaited()
        # Two bounded SET attempts may each reopen connection setup once.
        self.assertEqual(4, len(instances))
        for instance in instances:
            instance.set_text.assert_not_awaited()
            instance.__aexit__.assert_awaited_once()
        self.assertTrue(session._pasteboard_refresh_required)

if __name__ == '__main__':
    unittest.main()
