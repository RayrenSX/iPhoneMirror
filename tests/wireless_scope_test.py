"""Windows Bonjour friendly names must resolve to numeric IPv6 scope IDs."""
import asyncio
import socket
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as b


class WirelessScopeTests(unittest.IsolatedAsyncioTestCase):
    def normalized(self, host, adapters):
        return getattr(b, '_remote_pairing_host', lambda value, _: value)(host, adapters)

    def test_long_friendly_scope_uses_real_numeric_ipv6_resolution(self):
        adapter = SimpleNamespace(name='{test-guid}', nice_name='Microsoft Wi-Fi Direct Virtual Adapter', index=7)
        host = 'fe80::1c23:9655:cd8b:8edb%' + adapter.nice_name
        with patch.object(socket, 'if_nametoindex', side_effect=OSError('not an internal name')):
            normalized = self.normalized(host, [adapter])
        # A real OS resolver reproduces IDNA's label-length failure on the old
        # path. This never connects to any address or changes an interface.
        rows = socket.getaddrinfo(normalized, 49152, socket.AF_INET6, socket.SOCK_STREAM)
        self.assertEqual(7, rows[0][4][3])
        self.assertEqual('fe80::1c23:9655:cd8b:8edb%7', normalized)

    def test_numeric_scope_internal_name_and_ipv4_are_preserved(self):
        self.assertEqual('192.0.2.1', self.normalized('192.0.2.1', []))
        self.assertEqual('fe80::1%19', self.normalized('fe80::1%19', []))
        with patch.object(socket, 'if_nametoindex', return_value=9):
            self.assertEqual('fe80::1%9', self.normalized('fe80::1%ethernet_32769', []))

    async def test_discovery_normalizes_scope_and_deduplicates_routes(self):
        friendly = 'Microsoft Wi-Fi Direct Virtual Adapter'
        answers = [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip='fe80::1%' + friendly),
                    SimpleNamespace(full_ip='fe80::1%7')])]
        service = SimpleNamespace(connect=AsyncMock(), close=AsyncMock())
        factory = Mock(return_value=service)
        adapter = SimpleNamespace(name='{test-guid}', nice_name=friendly, index=7)
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch.object(b, 'RemotePairingTunnelService', factory), \
             patch('ifaddr.get_adapters', return_value=[adapter]), \
             patch.object(socket, 'if_nametoindex', side_effect=OSError()):
            self.assertEqual([service], await b.get_remote_pairing_tunnel_services(1, 'selected'))
        factory.assert_called_once_with('selected', 'fe80::1%7', 1)
        service.connect.assert_awaited_once_with(autopair=False)

    async def test_ambiguous_scope_is_not_guessed_or_reported_as_missing_broadcast(self):
        answers = [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip='fe80::1%duplicate')])]
        adapters = [SimpleNamespace(name=str(i), nice_name='duplicate', index=i) for i in (7, 8)]
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch.object(b, 'RemotePairingTunnelService') as factory, \
             patch('ifaddr.get_adapters', return_value=adapters), \
             patch.object(socket, 'if_nametoindex', side_effect=OSError()):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await b.get_remote_pairing_tunnel_services(1, 'selected')
        self.assertEqual('wireless_remote_pairing_failed', raised.exception.code)
        factory.assert_not_called()

    async def test_transient_route_handshake_reopens_once_without_pairing(self):
        answers = [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip='192.0.2.1')])]
        first = SimpleNamespace(connect=AsyncMock(side_effect=b.ConnectionTerminatedError()), close=AsyncMock())
        second = SimpleNamespace(connect=AsyncMock(), close=AsyncMock())
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch.object(b, 'RemotePairingTunnelService', side_effect=[first, second]), \
             patch('ifaddr.get_adapters', return_value=[]):
            self.assertEqual([second], await b.get_remote_pairing_tunnel_services(1, 'selected'))
        first.close.assert_awaited_once()
        second.connect.assert_awaited_once_with(autopair=False)
        second.close.assert_not_awaited()

    async def test_cancelled_route_handshake_does_not_retry(self):
        entered = asyncio.Event()
        async def pending(**_):
            entered.set()
            await asyncio.Event().wait()
        answers = [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip='192.0.2.1')])]
        service = SimpleNamespace(connect=AsyncMock(side_effect=pending), close=AsyncMock())
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch.object(b, 'RemotePairingTunnelService', return_value=service) as factory, \
             patch('ifaddr.get_adapters', return_value=[]):
            task = asyncio.create_task(b.get_remote_pairing_tunnel_services(1, 'selected'))
            await entered.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual(1, factory.call_count)
        service.close.assert_awaited_once()

    async def test_untrusted_route_does_not_retry(self):
        answers = [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip='192.0.2.1')])]
        service = SimpleNamespace(connect=AsyncMock(side_effect=b.NotPairedError()), close=AsyncMock())
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch.object(b, 'RemotePairingTunnelService', return_value=service) as factory:
            with self.assertRaises(b.BridgePrerequisiteError):
                await b.get_remote_pairing_tunnel_services(1, 'selected')
        self.assertEqual(1, factory.call_count)
        service.close.assert_awaited_once()

    async def test_route_retry_uses_remaining_budget_and_closes_timed_out_connection(self):
        async def reset(**_):
            await asyncio.sleep(.025)
            raise b.ConnectionTerminatedError()
        async def stalled(**_): await asyncio.Event().wait()
        first = SimpleNamespace(connect=reset, close=AsyncMock())
        second = SimpleNamespace(connect=stalled, close=AsyncMock())
        answers = [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip='192.0.2.1')])]
        limits = []
        real_wait_for = asyncio.wait_for
        async def observed_wait(operation, timeout):
            if timeout < 1: limits.append(timeout)
            return await real_wait_for(operation, timeout)
        with patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
             patch.object(b, 'RemotePairingTunnelService', side_effect=[first, second]) as factory, \
             patch.object(b, 'REMOTE_PAIRING_ROUTE_TIMEOUT_SECONDS', .08), \
             patch.object(b, 'REMOTE_PAIRING_ROUTE_RETRY_DELAY_SECONDS', .005), \
             patch.object(b.asyncio, 'wait_for', observed_wait):
            with self.assertRaises(b.BridgePrerequisiteError):
                await b.get_remote_pairing_tunnel_services(1, 'selected')
        self.assertEqual(2, factory.call_count)
        self.assertEqual(2, len(limits))
        self.assertLess(limits[1], limits[0] - .02)
        first.close.assert_awaited_once()
        second.close.assert_awaited_once()

    def test_ipv4_advertisement_corrects_stale_linklocal_adapter_hint(self):
        def adapter(index, v4, name):
            return SimpleNamespace(index=index, name=str(index), nice_name=name, ips=[
                SimpleNamespace(ip=('fe80::1', 0, index), network_prefix=64),
                SimpleNamespace(ip=v4, network_prefix=24)])
        adapters = [adapter(19, '169.254.1.1', 'stale'), adapter(4, '192.0.2.1', 'active')]
        peers = [SimpleNamespace(full_ip='fe80::2%stale'), SimpleNamespace(full_ip='192.0.2.2')]
        with patch.object(socket, 'if_nametoindex', side_effect=OSError()):
            self.assertEqual(['fe80::2%4'], b._remote_pairing_route_hosts(peers[0].full_ip, peers, adapters))

    async def test_duplicate_friendly_names_use_service_subnet_before_rejecting_scope(self):
        def adapter(index, v4):
            return SimpleNamespace(index=index, name=str(index), nice_name='Duplicate virtual adapter', ips=[
                SimpleNamespace(ip=('fe80::1', 0, index), network_prefix=64),
                SimpleNamespace(ip=v4, network_prefix=24)])
        adapters = [adapter(19, '169.254.1.1'), adapter(4, '192.0.2.1')]
        answers = [SimpleNamespace(port=49152, addresses=[
            SimpleNamespace(full_ip='fe80::2%Duplicate virtual adapter'),
            SimpleNamespace(full_ip='192.0.2.2')])]
        for blocked_v4 in (False, True):
            def factory(_, host, port):
                return SimpleNamespace(connect=AsyncMock(side_effect=OSError('IPv4 route blocked')
                    if blocked_v4 and ':' not in host else None), close=AsyncMock())
            with self.subTest(blocked_v4=blocked_v4), \
                 patch.object(b, 'browse_remotepairing', AsyncMock(return_value=answers)), \
                 patch.object(b, 'RemotePairingTunnelService', side_effect=factory) as create, \
                 patch('ifaddr.get_adapters', return_value=adapters), \
                 patch.object(socket, 'if_nametoindex', side_effect=OSError()):
                services = await b.get_remote_pairing_tunnel_services(1, 'selected')
            self.assertEqual(1 if blocked_v4 else 2, len(services))
            self.assertIn(('selected', 'fe80::2%4', 49152), [call.args for call in create.call_args_list])

    def test_ipv6_only_friendly_hint_verifies_eligible_scopes(self):
        adapters = [SimpleNamespace(index=i, name=str(i), nice_name='hint' if i == 7 else 'other',
                    ips=[SimpleNamespace(ip=('fe80::1', 0, i), network_prefix=64)]) for i in (7, 8)]
        peers = [SimpleNamespace(full_ip='fe80::2%hint')]
        with patch.object(socket, 'if_nametoindex', side_effect=OSError()):
            self.assertEqual(['fe80::2%7', 'fe80::2%8'], b._remote_pairing_route_hosts(peers[0].full_ip, peers, adapters))
