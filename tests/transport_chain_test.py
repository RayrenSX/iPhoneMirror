"""Transport selection, wireless candidates, identity and cancellation regressions."""
import asyncio
import contextlib
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as b
import mdns_discovery as mdns
from iostouch.qt import usbmux_usb


class MuxInitializationTests(unittest.TestCase):
    def test_partial_initialization_releases_claim_without_masking_failure(self):
        interface = SimpleNamespace(bInterfaceClass=0xff, bInterfaceSubClass=0xfe,
                                    bInterfaceNumber=1)
        class Configuration(list):
            bConfigurationValue = 5
        device = Mock()
        device.get_active_configuration.return_value = Configuration([interface])
        endpoint = SimpleNamespace(bEndpointAddress=0x81, wMaxPacketSize=512)
        for failure in ('endpoints', 'mux'):
            for release_fails in (False, True):
                original = RuntimeError('mux initialization failed')
                with self.subTest(failure=failure, release_fails=release_fails), \
                     patch('usb.util.claim_interface') as claim, \
                     patch('usb.util.release_interface', side_effect=OSError('device removed')
                           if release_fails else None) as release, \
                     patch('usb.util.find_descriptor', return_value=None
                           if failure == 'endpoints' else endpoint), \
                     patch.object(usbmux_usb, 'MuxDevice', side_effect=original):
                    with self.assertRaises(usbmux_usb.MuxError if failure == 'endpoints'
                                           else RuntimeError) as raised:
                        usbmux_usb.UsbMuxTransport(device, 'test-device')
                    if failure == 'mux':
                        self.assertIs(raised.exception, original)
                    claim.assert_called_once_with(device, 1)
                    release.assert_called_once_with(device, 1)

    def test_failed_claim_does_not_release_an_unowned_interface(self):
        interface = SimpleNamespace(bInterfaceClass=0xff, bInterfaceSubClass=0xfe,
                                    bInterfaceNumber=1)
        class Configuration(list):
            bConfigurationValue = 5
        device = Mock()
        device.get_active_configuration.return_value = Configuration([interface])
        with patch('usb.util.claim_interface', side_effect=OSError('busy')), \
             patch('usb.util.release_interface') as release:
            with self.assertRaisesRegex(OSError, 'busy'):
                usbmux_usb.UsbMuxTransport(device, 'test-device')
            release.assert_not_called()



class TransportTests(unittest.IsolatedAsyncioTestCase):
    async def test_no_usable_multicast_family_returns_local_error_without_pair_handshake(self):
        s = self.session()
        join_error = OSError('No usable local mDNS multicast interface')
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['test-device']), \
             patch.object(mdns, '_bind_ipv4', AsyncMock(side_effect=join_error)), \
             patch.object(mdns, '_bind_ipv6_all_ifaces', AsyncMock(side_effect=join_error)), \
             patch.object(b, 'RemotePairingTunnelService') as create:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await s._connect_via_remote_pairing()
        self.assertEqual('wireless_discovery_unavailable', raised.exception.code)
        self.assertIsInstance(raised.exception.__cause__, mdns.MdnsDiscoveryError)
        self.assertIs(join_error, raised.exception.__cause__.__cause__)
        create.assert_not_called()

    async def test_local_mdns_failure_is_not_presented_as_pairing_failure(self):
        s = self.session()
        original = getattr(b, 'MdnsDiscoveryError', OSError)('Local mDNS listeners unavailable')
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['test-device']), \
             patch.object(b, 'get_remote_pairing_tunnel_services', AsyncMock(side_effect=original)):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await s._connect_via_remote_pairing()
        self.assertEqual('wireless_discovery_unavailable', raised.exception.code)
        self.assertIs(original, raised.exception.__cause__)

    def session(self, transport='wireless'):
        return b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, 'test-device', transport)

    async def test_wireless_does_not_require_network_usbmux_when_remote_pairing_exists(self):
        for error in (b.ConnectionFailedToUsbmuxdError(), TimeoutError(),
                      b.BridgePrerequisiteError('apple_device_connection_timeout', 'handshake timed out'),
                      b.ConnectionFailedError(), b.ConnectionTerminatedError(),
                      ConnectionResetError('stale Network record'), OSError('socket unreachable')):
            s = self.session()
            with self.subTest(error=type(error).__name__), \
                 patch.object(s, '_start_capture_mux', AsyncMock()), \
                 patch.object(s, '_create_initial_lockdown', AsyncMock(side_effect=error)), \
                 patch.object(s, '_connect_via_remote_pairing', AsyncMock()) as remote:
                await s.connect()
            remote.assert_awaited_once()

    async def test_wireless_trust_and_lock_errors_do_not_try_another_transport(self):
        for error in (b.NotPairedError(), b.PasswordRequiredError()):
            s = self.session()
            with self.subTest(error=type(error).__name__), \
                 patch.object(s, '_start_capture_mux', AsyncMock()), \
                 patch.object(s, '_create_initial_lockdown', AsyncMock(side_effect=error)), \
                 patch.object(s, '_connect_via_remote_pairing', AsyncMock()) as remote:
                with self.assertRaises(type(error)): await s.connect()
            remote.assert_not_awaited()

    async def test_wired_network_service_failure_does_not_switch_transport(self):
        s = self.session('usb')
        with patch.object(s, '_start_capture_mux', AsyncMock()), \
             patch.object(s, '_create_initial_lockdown', AsyncMock(side_effect=b.ConnectionFailedToUsbmuxdError())), \
             patch.object(s, '_connect_via_remote_pairing', AsyncMock()) as remote:
            with self.assertRaises(b.ConnectionFailedToUsbmuxdError): await s.connect()
        remote.assert_not_awaited()

    async def test_wired_handshake_timeout_has_a_specific_error(self):
        s = self.session('usb')
        with patch.object(s, '_start_capture_mux', AsyncMock()), \
             patch.object(s, '_create_initial_lockdown', AsyncMock(side_effect=TimeoutError())):
            with self.assertRaises(b.BridgePrerequisiteError) as raised: await s.connect()
        self.assertEqual(raised.exception.code, 'apple_device_connection_timeout')

    async def test_optional_usb_backend_or_enumeration_failure_keeps_apple_route(self):
        for stage in ('backend', 'enumeration'):
            s = self.session('usb')
            lockdown = SimpleNamespace(close=AsyncMock())
            with self.subTest(stage=stage), \
                 patch.object(b, '_get_usb_backend', side_effect=RuntimeError('backend unavailable')
                              if stage == 'backend' else None), \
                 patch.object(b, '_find_usb_devices', side_effect=OSError('enumeration denied')), \
                 patch.object(s, '_create_initial_lockdown', AsyncMock(return_value=lockdown)) as connect, \
                 patch.object(s, '_connect_with_ddi_recovery', AsyncMock()) as control:
                await s.connect()
            connect.assert_awaited_once_with('USB')
            control.assert_awaited_once_with(lockdown)
            lockdown.close.assert_awaited_once()
            self.assertTrue(any(call.args[0].get('code') == 'capture_mux_fallback'
                                for call in s.ipc.emit.call_args_list))

    async def test_enumeration_failure_during_mux_retry_is_also_optional(self):
        s = self.session('usb')
        device = SimpleNamespace(activated=True, serial='test-device', dev=object())
        from unittest.mock import Mock
        mux = Mock(start=Mock(side_effect=ConnectionError('mux handshake failed')))
        with patch.object(b, '_get_usb_backend', return_value=object()), \
             patch.object(b, '_find_usb_devices', side_effect=[[device], OSError('USB reenumerating')]), \
             patch.object(b, '_UsbMuxTransport', return_value=mux), \
             patch.object(b, 'CAPTURE_MUX_RETRY_DELAY_SECONDS', 0):
            await s._start_capture_mux()
        mux.close.assert_called_once()
        self.assertIsNone(s._usb_mux_transport)
        self.assertTrue(any(call.args[0].get('code') == 'capture_mux_fallback'
                            for call in s.ipc.emit.call_args_list))

    async def test_optional_usb_discovery_preserves_cancellation(self):
        s = self.session('usb')
        with patch.object(b, '_get_usb_backend', side_effect=asyncio.CancelledError()), \
             patch.object(s, '_create_initial_lockdown', AsyncMock()) as connect:
            with self.assertRaises(asyncio.CancelledError): await s.connect()
        connect.assert_not_awaited()

    async def test_optional_provisioning_has_one_budget_and_closes_its_service(self):
        s = self.session('usb')
        async def connect(**_): await asyncio.sleep(.28)
        service = SimpleNamespace(connect=connect, close=AsyncMock())
        async def create(_):
            await asyncio.sleep(.05)
            return service
        with patch.object(b.RemotePairingLockdownService, 'create', create), \
             patch.object(b, 'REMOTE_PAIRING_PROVISION_TIMEOUT_SECONDS', .3):
            self.assertFalse(await s._provision_remote_pairing(object()))
        service.close.assert_awaited_once()

    async def test_usb_missing_service_never_claims_a_wireless_route_as_wired(self):
        s = self.session('usb')
        with patch.object(b.CoreDeviceTunnelProxy, 'create', AsyncMock(side_effect=
                b.InvalidServiceError('missing', 'test-device', '17.0'))), \
             patch.object(s, '_connect_via_remote_pairing', AsyncMock()) as remote:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await s._run_tunnel_attempt(object())
        self.assertEqual(raised.exception.code, 'wired_control_service_unavailable')
        remote.assert_not_awaited()

    async def test_wireless_network_tunnel_failure_tries_remote_pairing_before_ready(self):
        for error in (ConnectionError('stale Network record'),
                      b.BridgePrerequisiteError('control_service_start_timeout', 'stalled tunnel')):
            s = self.session()
            async def fail(operation):
                operation.close()
                raise error
            with self.subTest(error=type(error).__name__), \
                 patch.object(s, '_run_until_ready', fail), \
                 patch.object(s, '_connect_via_remote_pairing', AsyncMock()) as remote:
                await s._run_tunnel_attempt(object())
            remote.assert_awaited_once()

    async def test_ready_network_tunnel_failure_does_not_replay_startup_fallback(self):
        s = self.session()
        async def fail(operation):
            operation.close()
            s._generation += 1
            raise ConnectionError('established tunnel lost')
        with patch.object(s, '_run_until_ready', fail), \
             patch.object(s, '_connect_via_remote_pairing', AsyncMock()) as remote:
            with self.assertRaises(ConnectionError): await s._run_tunnel_attempt(object())
        remote.assert_not_awaited()

    async def test_wireless_retries_discovered_route_and_closes_all_candidates(self):
        s = self.session()
        services = [SimpleNamespace(close=AsyncMock()), SimpleNamespace(close=AsyncMock())]
        tried = []
        @contextlib.asynccontextmanager
        async def tunnel(service):
            tried.append(service)
            if service is services[0]: raise ConnectionError('first NIC unreachable')
            yield object()
        async def observed(_):
            s._generation += 1
            s._session_ready.set()
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['TESTDEVICE']), \
             patch.object(b, 'get_remote_pairing_tunnel_services', AsyncMock(return_value=services)) as discovery, \
             patch.object(s, '_bounded_tunnel', tunnel), patch.object(s, '_run_observed_tunnel', observed):
            await s._connect_via_remote_pairing()
        self.assertEqual(tried, services)
        self.assertEqual(discovery.call_args.kwargs['udid'], 'TESTDEVICE')
        for service in services: service.close.assert_awaited_once()

    async def test_established_wireless_failure_is_left_to_host_recovery(self):
        s = self.session()
        services = [SimpleNamespace(close=AsyncMock()), SimpleNamespace(close=AsyncMock())]
        tried = []
        @contextlib.asynccontextmanager
        async def tunnel(service):
            tried.append(service)
            yield object()
        async def observed(_):
            s._generation += 1
            s._session_ready.set()
            s._hid_transport_failed = True
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['test-device']), \
             patch.object(b, 'get_remote_pairing_tunnel_services', AsyncMock(return_value=services)), \
             patch.object(s, '_bounded_tunnel', tunnel), patch.object(s, '_run_observed_tunnel', observed):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await s._connect_via_remote_pairing()
        self.assertEqual(raised.exception.code, 'apple_connection_lost')
        self.assertEqual(tried, services[:1])

    async def test_rsd_identity_is_checked_before_hid_for_both_transports(self):
        class Dial:
            dial = AsyncMock()
            def __init__(self, *_): pass
            async def __aenter__(self): return self
        class Rsd:
            udid = 'another-device'
            def __init__(self, *_, **kwargs): pass
            async def __aenter__(self): return self
        result = SimpleNamespace(client=SimpleNamespace(tun=SimpleNamespace(set_peer=lambda _: None)), address='phone', port=1)
        for transport in ('usb', 'wireless'):
            s = self.session(transport)
            with self.subTest(transport=transport), \
                 patch('pymobiledevice3.remote.userspace_tunnel.UserspaceDialPlane', Dial), \
                 patch.object(b, 'RemoteServiceDiscoveryService', Rsd), \
                 patch.object(b, 'touch_session') as touch, patch.object(s, '_cleanup', AsyncMock()) as cleanup:
                with self.assertRaises(b.BridgePrerequisiteError) as raised:
                    await s._connect_with_tunnel_result(result)
            self.assertEqual(raised.exception.code, 'device_identity_mismatch')
            touch.assert_not_called()
            cleanup.assert_awaited_once()


class DiscoveryTests(unittest.IsolatedAsyncioTestCase):
    async def test_advertised_service_without_addresses_preserves_resolution_failure(self):
        answers = [SimpleNamespace(port=49152, addresses=[])]
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch('ifaddr.get_adapters', return_value=[]), \
             patch.object(b, 'RemotePairingTunnelService') as factory:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await b.get_remote_pairing_tunnel_services(1, 'test-device')
        self.assertEqual('wireless_address_resolution_failed', raised.exception.code)
        self.assertIn('addresses', str(raised.exception))
        factory.assert_not_called()

    async def test_no_advertised_routes_remains_a_discovery_failure(self):
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=[])), \
             patch.object(b, 'RemotePairingTunnelService') as create:
            self.assertEqual(await b.get_remote_pairing_tunnel_services(1, 'test-device'), [])
        create.assert_not_called()

    async def test_advertised_but_rejected_routes_preserve_handshake_failure(self):
        errors = [ConnectionResetError('pair verification rejected'), OSError('route unavailable')]
        services = [SimpleNamespace(connect=AsyncMock(side_effect=error), close=AsyncMock())
                    for error in errors * 2]
        answers = [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip=host)
                                                     for host in ('first', 'second')])]
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch.object(b, 'RemotePairingTunnelService', side_effect=services):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await b.get_remote_pairing_tunnel_services(1, 'test-device')
        self.assertEqual(raised.exception.code, 'wireless_remote_pairing_failed')
        self.assertIn('ConnectionResetError', str(raised.exception))
        self.assertIn('OSError', str(raised.exception))
        self.assertIn('2', str(raised.exception))
        self.assertIs(raised.exception.__cause__, errors[0])
        for service in services: service.close.assert_awaited_once()

    async def test_remote_pairing_external_cancel_aborts_writer(self):
        service = object.__new__(b.RemotePairingTunnelService)
        entered = asyncio.Event()
        aborted = []
        async def stalled():
            entered.set()
            await asyncio.Event().wait()
        service._writer = SimpleNamespace(close=lambda: None, wait_closed=stalled,
            transport=SimpleNamespace(abort=lambda: aborted.append(True)))
        service._reader = object()
        task = asyncio.create_task(service.close())
        await entered.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual(aborted, [True])
        self.assertIsNone(service._writer)

    async def test_remote_pairing_close_aborts_a_stalled_writer(self):
        service = object.__new__(b.RemotePairingTunnelService)
        aborted = []
        async def stalled(): await asyncio.Event().wait()
        service._writer = SimpleNamespace(close=lambda: None, wait_closed=stalled,
            transport=SimpleNamespace(abort=lambda: aborted.append(True)))
        service._reader = object()
        timeout = asyncio.timeout
        with patch.object(b.asyncio, 'timeout', side_effect=lambda _: timeout(.01)):
            await service.close()
        self.assertEqual(aborted, [True])
        self.assertIsNone(service._writer)
        self.assertIsNone(service._reader)

    async def test_dead_address_does_not_prevent_other_addresses_from_connecting(self):
        failed = [SimpleNamespace(connect=AsyncMock(side_effect=ConnectionError('offline')), close=AsyncMock())
                  for _ in range(2)]
        healthy = SimpleNamespace(connect=AsyncMock(), close=AsyncMock())
        dead = iter(failed)
        def route(_, host, port):
            return next(dead) if host == 'dead' else healthy
        answers = [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip=host) for host in ('dead', 'live', 'live')])]
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch.object(b, 'RemotePairingTunnelService', side_effect=route) as create:
            result = await b.get_remote_pairing_tunnel_services(1, 'test-device')
        self.assertEqual(result, [healthy])
        self.assertEqual(create.call_count, 3)
        for service in failed: service.close.assert_awaited_once()
        healthy.close.assert_not_awaited()
        healthy.connect.assert_awaited_once_with(autopair=False)

    async def test_discovery_cancellation_closes_partial_and_verified_connections(self):
        started, verified = asyncio.Event(), asyncio.Event()
        async def stall(**_):
            started.set()
            await asyncio.Event().wait()
        async def succeed(**_): verified.set()
        partial = SimpleNamespace(connect=stall, close=AsyncMock())
        healthy = SimpleNamespace(connect=succeed, close=AsyncMock())
        answers = [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip=host) for host in ('slow', 'live')])]
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch.object(b, 'RemotePairingTunnelService', side_effect=[partial, healthy]):
            task = asyncio.create_task(b.get_remote_pairing_tunnel_services(1, 'test-device'))
            await started.wait()
            await verified.wait()
            await asyncio.sleep(0)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        partial.close.assert_awaited_once()
        healthy.close.assert_awaited_once()


class InputCleanupTests(unittest.IsolatedAsyncioTestCase):
    async def test_media_session_releases_touch_and_keyboard_before_closing_hid(self):
        s = b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, 'test-device')
        events = []
        async def report(*_): events.append('touch-up')
        async def keyboard(*_): events.append('key-up')
        hid = SimpleNamespace(SERVICE_NAME='hid', send_report=report, send_keyboard=keyboard)
        @contextlib.asynccontextmanager
        async def media(_):
            try: yield hid
            finally: events.append('closed')
        class Dial:
            dial = AsyncMock()
            def __init__(self, *_): pass
            async def __aenter__(self): return self
        class Rsd:
            udid = 'test-device'
            def __init__(self, *_, **kwargs): pass
            async def __aenter__(self): return self
        async def verify(_): s.keyboard_service_id = 512
        result = SimpleNamespace(client=SimpleNamespace(tun=SimpleNamespace(set_peer=lambda _: None)), address='phone', port=1)
        with patch('pymobiledevice3.remote.userspace_tunnel.UserspaceDialPlane', Dial), \
             patch.object(b, 'RemoteServiceDiscoveryService', Rsd), patch.object(b, 'touch_session', media), \
             patch.object(s, '_verify_touch_surface', verify), patch.object(s, '_emit_ready', AsyncMock()), \
             patch.object(s, '_run_serve', AsyncMock()), patch.object(s, '_cleanup', AsyncMock()):
            await s._connect_with_tunnel_result(result)
        self.assertEqual(events, ['key-up'] + ['touch-up'] * b.MAX_SLOTS + ['closed'])

    async def test_cancelled_indigo_open_closes_partial_service(self):
        s = b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        started = asyncio.Event()
        async def stalled():
            started.set()
            await asyncio.Event().wait()
        indigo = SimpleNamespace(__aenter__=stalled, __aexit__=AsyncMock())
        with patch.object(b, 'IndigoHIDService', return_value=indigo):
            task = asyncio.create_task(s._apply_button(12, 64, 'down'))
            await started.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        indigo.__aexit__.assert_awaited_once()
        self.assertIsNone(s.indigo)

    async def test_stop_releases_a_button_whose_down_write_failed(self):
        s = b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        indigo = SimpleNamespace(send_button=AsyncMock(side_effect=[ConnectionError('partial write'), None]), __aexit__=AsyncMock())
        s.indigo = indigo
        with self.assertRaises(ConnectionError): await s._apply_button(12, 64, 'down')
        await s._cleanup()
        self.assertEqual([call.args for call in indigo.send_button.call_args_list], [(12, 64, 1), (12, 64, 2)])
        self.assertFalse(s._held_buttons)
        indigo.__aexit__.assert_awaited_once()
