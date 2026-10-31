"""Incremental discovery, serial HID ownership, deadlines and socket cleanup."""
import asyncio
import contextlib
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as b
import mdns_discovery as d
from mdns_discovery_test import response


def answers(*hosts):
    return [SimpleNamespace(port=1, addresses=[SimpleNamespace(full_ip=h) for h in hosts])]


class StreamingTests(unittest.IsolatedAsyncioTestCase):
    def session(self):
        return b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, 'phone', 'wireless')

    async def test_single_slow_transport_is_not_cut_off_by_stall_threshold(self):
        s = self.session()
        service = SimpleNamespace(close=AsyncMock())
        async def discover(**kw):
            kw['on_verified'](service)
            await asyncio.Event().wait()
        @contextlib.asynccontextmanager
        async def tunnel(_):
            await asyncio.sleep(.15)
            yield object()
        async def ready(_):
            s._generation += 1
            s._session_ready.set()
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['phone']), \
             patch.object(b, 'get_remote_pairing_tunnel_services', discover), \
             patch.object(b, 'HID_RECOVERY_READY_TIMEOUT_SECONDS', .5), \
             patch.object(b, 'WIRELESS_ROUTE_STALL_SECONDS', .01), \
             patch.object(s, '_bounded_tunnel', tunnel), patch.object(s, '_run_observed_tunnel', ready):
            await s._connect_via_remote_pairing()
        service.close.assert_awaited_once()

    async def test_multiple_candidates_share_one_absolute_deadline(self):
        s = self.session()
        services = [SimpleNamespace(close=AsyncMock()) for _ in range(3)]
        deadlines = []
        @contextlib.asynccontextmanager
        async def stalled(_):
            deadlines.append(s._wireless_ready_deadline)
            await asyncio.Event().wait()
            yield
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['phone']), \
             patch.object(b, 'get_remote_pairing_tunnel_services', AsyncMock(return_value=services)), \
             patch.object(b, 'HID_RECOVERY_READY_TIMEOUT_SECONDS', .35), \
             patch.object(b, 'WIRELESS_ROUTE_STALL_SECONDS', .01), \
             patch.object(s, '_bounded_tunnel', stalled):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await s._connect_via_remote_pairing()
        self.assertEqual('control_service_start_timeout', raised.exception.code)
        self.assertEqual(3, len(deadlines))
        self.assertEqual(1, len(set(deadlines)))
        for service in services:
            service.close.assert_awaited_once()

    async def test_remote_ready_closes_unused_network_before_session_ends(self):
        s = self.session()
        network, release = asyncio.Event(), asyncio.Event()
        client = SimpleNamespace(close=AsyncMock())
        service = SimpleNamespace(close=AsyncMock())
        async def lookup(_):
            await network.wait()
            return client
        async def discover(**kw):
            kw['on_verified'](service)
            await asyncio.Event().wait()
        @contextlib.asynccontextmanager
        async def tunnel(_): yield object()
        async def ready(_):
            network.set()
            await asyncio.sleep(.02)
            s._generation += 1
            s._session_ready.set()
            await release.wait()
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['phone']), \
             patch.object(b, 'get_remote_pairing_tunnel_services', discover), \
             patch.object(s, '_create_initial_lockdown', lookup), \
             patch.object(s, '_bounded_tunnel', tunnel), patch.object(s, '_run_observed_tunnel', ready):
            task = asyncio.create_task(s.connect())
            try:
                await asyncio.wait_for(s._session_ready.wait(), 1)
                for _ in range(30):
                    if client.close.await_count: break
                    await asyncio.sleep(.01)
                client.close.assert_awaited_once()
                self.assertFalse(task.done())
            finally:
                release.set()
                await task
        client.close.assert_awaited_once()

    async def test_remote_failure_can_fall_back_to_network_without_parallel_hid(self):
        s = self.session()
        entered = asyncio.Event()
        client = SimpleNamespace(close=AsyncMock())
        service = SimpleNamespace(close=AsyncMock())
        async def lookup(_):
            await entered.wait()
            return client
        @contextlib.asynccontextmanager
        async def broken(_):
            entered.set()
            raise ConnectionError('remote tunnel failed')
            yield
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['phone']), \
             patch.object(b, 'get_remote_pairing_tunnel_services', AsyncMock(return_value=[service])), \
             patch.object(s, '_create_initial_lockdown', lookup), \
             patch.object(s, '_bounded_tunnel', broken), \
             patch.object(s, '_connect_with_ddi_recovery', AsyncMock()) as control:
            await s.connect()
        control.assert_awaited_once_with(client)
        service.close.assert_awaited_once()
        client.close.assert_awaited_once()

    async def test_packet_can_start_verification_before_discovery_deadline(self):
        queue = asyncio.Queue()
        queue.put_nowait((response(), ('192.0.2.2', 5353)))
        transport = SimpleNamespace(close=lambda: None)
        service = SimpleNamespace(connect=AsyncMock(), close=AsyncMock())
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, None)], queue))), \
             patch.object(d, '_send_query_all', AsyncMock()), \
             patch.object(d, '_Adapters', return_value=None), \
             patch.object(b, 'RemotePairingTunnelService', return_value=service), \
             patch.object(b, 'REMOTE_PAIRING_DISCOVERY_TIMEOUT_SECONDS', 10):
            async with b.RemotePairingDiscovery('phone') as discovery:
                result = await asyncio.wait_for(discovery.take(), 1)
                self.assertIs(service, result)
                self.assertFalse(discovery.task.done())
        service.close.assert_awaited_once()

    async def test_wrong_device_does_not_stop_late_working_address(self):
        finished = asyncio.Event()
        async def browse(timeout, on_update):
            on_update(answers('wrong'))
            await asyncio.sleep(.02)
            on_update(answers('wrong', 'right'))
            try:
                await asyncio.Event().wait()
            finally:
                finished.set()
        wrong = SimpleNamespace(connect=AsyncMock(side_effect=b.NotPairedError()), close=AsyncMock())
        right = SimpleNamespace(connect=AsyncMock(), close=AsyncMock())
        with patch.object(b, 'browse_remotepairing', browse), \
             patch.object(b, 'RemotePairingTunnelService', side_effect=[wrong, right]) as factory:
            async with b.RemotePairingDiscovery('phone') as discovery:
                self.assertIs(right, await asyncio.wait_for(discovery.take(), 1))
                self.assertEqual(2, factory.call_count)
        self.assertTrue(finished.is_set())
        wrong.close.assert_awaited_once()
        right.close.assert_awaited_once()

    async def test_stalled_transport_switches_to_late_candidate_without_rediscovery(self):
        s, entered = self.session(), asyncio.Event()
        created, tried = [], []
        async def browse(timeout, on_update):
            on_update(answers('bad'))
            await entered.wait()
            on_update(answers('bad', 'good'))
            await asyncio.Event().wait()
        def factory(_, host, port):
            service = SimpleNamespace(hostname=host, port=port, connect=AsyncMock(), close=AsyncMock())
            created.append(service)
            return service
        @contextlib.asynccontextmanager
        async def tunnel(service):
            tried.append(service.hostname)
            if service.hostname == 'bad':
                entered.set()
                await asyncio.Event().wait()
            yield object()
        async def ready(_):
            s._generation += 1
            s._session_ready.set()
            await asyncio.sleep(.03)
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['phone']), \
             patch.object(b, 'browse_remotepairing', AsyncMock(side_effect=browse)) as browser, \
             patch.object(b, 'RemotePairingTunnelService', factory), \
             patch.object(b, 'WIRELESS_ROUTE_STALL_SECONDS', .01), \
             patch.object(s, '_bounded_tunnel', tunnel), patch.object(s, '_run_observed_tunnel', ready):
            await asyncio.wait_for(s._connect_via_remote_pairing(), 1)
        self.assertEqual(['bad', 'good'], tried)
        browser.assert_awaited_once()
        for service in created:
            service.close.assert_awaited_once()

    async def test_hid_initialization_is_not_preempted_and_ready_outlives_deadline(self):
        s = self.session()
        first, other = (SimpleNamespace(close=AsyncMock()) for _ in range(2))
        async def discover(**kw):
            kw['on_verified'](first)
            kw['on_verified'](other)
            await asyncio.Event().wait()
        tried = []
        @contextlib.asynccontextmanager
        async def tunnel(service):
            tried.append(service)
            yield object()
        async def hid(_):
            s._wireless_connect_phase = ('hid', asyncio.get_running_loop().time())
            await asyncio.sleep(.13)
            s._generation += 1
            s._session_ready.set()
            await asyncio.sleep(.2)
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['phone']), \
             patch.object(b, 'get_remote_pairing_tunnel_services', discover), \
             patch.object(b, 'HID_RECOVERY_READY_TIMEOUT_SECONDS', .25), \
             patch.object(b, 'WIRELESS_ROUTE_STALL_SECONDS', .01), \
             patch.object(s, '_bounded_tunnel', tunnel), patch.object(s, '_run_observed_tunnel', hid):
            await s._connect_via_remote_pairing()
        self.assertEqual([first], tried)
        first.close.assert_awaited_once()
        other.close.assert_awaited_once()

    async def test_discovery_and_network_lookup_start_together(self):
        s = self.session()
        network_started, cancelled = asyncio.Event(), asyncio.Event()
        service = SimpleNamespace(close=AsyncMock())
        async def network(_):
            network_started.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        async def discover(**kw):
            await network_started.wait()
            kw['on_verified'](service)
            await asyncio.Event().wait()
        @contextlib.asynccontextmanager
        async def tunnel(_): yield object()
        async def ready(_):
            s._generation += 1
            s._session_ready.set()
        with patch.object(b, 'iter_remote_paired_identifiers', return_value=['phone']), \
             patch.object(b, 'get_remote_pairing_tunnel_services', discover), \
             patch.object(s, '_create_initial_lockdown', network), \
             patch.object(s, '_bounded_tunnel', tunnel), patch.object(s, '_run_observed_tunnel', ready):
            await asyncio.wait_for(s.connect(), 1)
        self.assertTrue(cancelled.is_set())
        service.close.assert_awaited_once()

    async def test_verification_concurrency_is_bounded_and_cancel_closes_sockets(self):
        entered = asyncio.Event()
        count, peak = 0, 0
        services = []
        async def connect(**_):
            nonlocal count, peak
            count += 1
            peak = max(peak, count)
            if peak == 4: entered.set()
            try:
                await asyncio.Event().wait()
            finally:
                count -= 1
        def factory(*_):
            service = SimpleNamespace(connect=connect, close=AsyncMock())
            services.append(service)
            return service
        async def browse(timeout, on_update):
            on_update(answers(*(str(i) for i in range(20))))
            await asyncio.Event().wait()
        with patch.object(b, 'browse_remotepairing', browse), \
             patch.object(b, 'RemotePairingTunnelService', factory):
            async with b.RemotePairingDiscovery('phone'):
                await asyncio.wait_for(entered.wait(), 1)
        self.assertEqual(4, peak)
        self.assertEqual(4, len(services))
        for service in services:
            service.close.assert_awaited_once()
