"""Discovery failures must not discard a working family or leak UDP sockets."""
import asyncio
import errno
import socket
import struct
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from pymobiledevice3 import bonjour as sdk
try:
    import mdns_discovery as d
except ModuleNotFoundError:
    d = sdk  # Reproduce these failures in the currently packaged SDK first.


def response(address='192.0.2.2'):
    service = sdk.REMOTEPAIRING_SERVICE_NAME
    instance, host = 'Phone.' + service, 'phone.local.'
    records = [
        sdk._build_rr(service, sdk.QTYPE_PTR, sdk.encode_name(instance), 120, False),
        sdk._build_rr(instance, sdk.QTYPE_SRV, sdk._encode_srv(0, 0, 49152, host), 120, True),
        sdk._build_rr(host, sdk.QTYPE_AAAA if ':' in address else sdk.QTYPE_A,
                      socket.inet_pton(socket.AF_INET6 if ':' in address else socket.AF_INET, address), 120, True),
    ]
    return struct.pack('!HHHHHH', 0, 0x8400, 0, len(records), 0, 0) + b''.join(records)


class MdnsDiscoveryTests(unittest.IsolatedAsyncioTestCase):
    async def test_ipv6_disabled_retains_working_ipv4(self):
        transport, sock = Mock(), Mock(family=socket.AF_INET)
        with patch.object(d, '_bind_ipv4', AsyncMock(return_value=(transport, sock))), \
             patch.object(d, '_bind_ipv6_all_ifaces', AsyncMock(side_effect=OSError(errno.EAFNOSUPPORT, 'IPv6 disabled'))):
            transports, _ = await d._open_mdns_sockets()
        self.assertEqual([(transport, sock)], transports)
        transport.close.assert_not_called()

    async def test_ipv4_unavailable_retains_working_ipv6(self):
        transport, sock = Mock(), Mock(family=socket.AF_INET6)
        with patch.object(d, '_bind_ipv4', AsyncMock(side_effect=OSError(errno.EADDRNOTAVAIL, 'No IPv4'))), \
             patch.object(d, '_bind_ipv6_all_ifaces', AsyncMock(return_value=(transport, sock))):
            transports, _ = await d._open_mdns_sockets()
        self.assertEqual([(transport, sock)], transports)

    async def test_cancel_during_second_family_closes_first_transport(self):
        transport, sock = Mock(), Mock(family=socket.AF_INET)
        with patch.object(d, '_bind_ipv4', AsyncMock(return_value=(transport, sock))), \
             patch.object(d, '_bind_ipv6_all_ifaces', AsyncMock(side_effect=asyncio.CancelledError())):
            with self.assertRaises(asyncio.CancelledError):
                await d._open_mdns_sockets()
        transport.close.assert_called_once()

    async def test_endpoint_failure_closes_actual_unreturned_socket(self):
        for family, bind in ((socket.AF_INET, d._bind_ipv4), (socket.AF_INET6, d._bind_ipv6_all_ifaces)):
            with self.subTest(family=family):
                real_socket = socket.socket(family, socket.SOCK_DGRAM)
                loop = asyncio.get_running_loop()
                endpoint = AsyncMock(side_effect=OSError('endpoint failed'))
                try:
                    with patch.object(d.socket, 'socket', return_value=real_socket), \
                         patch.object(d, 'MDNS_PORT', 0), \
                         patch.object(loop, 'create_datagram_endpoint', endpoint):
                        with self.assertRaises(OSError): await bind(asyncio.Queue())
                    endpoint.assert_awaited_once()
                    self.assertEqual(-1, real_socket.fileno())
                finally:
                    real_socket.close()

    async def test_all_multicast_joins_failed_rejects_family_and_closes_socket(self):
        for family, bind in ((socket.AF_INET, d._bind_ipv4), (socket.AF_INET6, d._bind_ipv6_all_ifaces)):
            with self.subTest(family=family):
                raw = socket.socket(family, socket.SOCK_DGRAM)
                wrapped = Mock(wraps=raw)
                original = OSError(errno.ENODEV, 'No multicast interface')
                def option(level, kind, value):
                    if ((level, kind) == (socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP)
                            or (level, kind) == (socket.IPPROTO_IPV6, socket.IPV6_JOIN_GROUP)):
                        raise original
                    raw.setsockopt(level, kind, value)
                wrapped.setsockopt.side_effect = option
                endpoint = AsyncMock(return_value=(Mock(), None))
                try:
                    with patch.object(d.socket, 'socket', return_value=wrapped), \
                         patch.object(d, 'MDNS_PORT', 0), \
                         patch.object(d, '_ipv4_interface_addresses', return_value=['192.0.2.1']), \
                         patch.object(d.socket, 'if_nameindex', return_value=[(7, 'test')]), \
                         patch.object(asyncio.get_running_loop(), 'create_datagram_endpoint', endpoint):
                        with self.assertRaises(OSError) as raised:
                            await bind(asyncio.Queue())
                    self.assertIs(original, raised.exception.__cause__)
                    endpoint.assert_not_awaited()
                    self.assertEqual(-1, raw.fileno())
                finally:
                    raw.close()

    async def test_one_failed_multicast_join_retains_other_interface(self):
        raw, transport = Mock(), Mock()
        def option(level, kind, value):
            if kind == socket.IP_ADD_MEMBERSHIP and value.endswith(socket.inet_aton('192.0.2.1')):
                raise OSError('First link down')
        raw.setsockopt.side_effect = option
        with patch.object(d.socket, 'socket', return_value=raw), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['192.0.2.1', '198.51.100.1']), \
             patch.object(asyncio.get_running_loop(), 'create_datagram_endpoint', AsyncMock(return_value=(transport, None))):
            self.assertEqual((transport, raw), await d._bind_ipv4(asyncio.Queue()))
        raw.close.assert_not_called()

    async def test_invalid_ptr_label_does_not_abort_valid_service_discovery(self):
        service = sdk.REMOTEPAIRING_SERVICE_NAME
        # The SDK parser accepts a reserved 64-byte DNS label, then its encoder
        # rejects that learned name during a follow-up query. Use actual bytes.
        bad_name = bytes([64]) + b'x' * 64 + sdk.encode_name(service)
        bad_rr = sdk._build_rr(service, sdk.QTYPE_PTR, bad_name, 120, False)
        queue = asyncio.Queue()
        queue.put_nowait((struct.pack('!HHHHHH', 0, 0x8400, 0, 1, 0, 0) + bad_rr, ('192.0.2.1', 5353)))
        sock, transport = Mock(family=socket.AF_INET), Mock()
        def send(*_):
            if sock.sendto.call_count == 2:
                queue.put_nowait((response(), ('192.0.2.2', 5353)))
        sock.sendto.side_effect = send
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0']), \
             patch.object(d, 'QUERY_RETRY_SECONDS', .02), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'test-iface')):
            answers = await d.browse_service(service, timeout=.12)
        self.assertEqual(1, len(answers))
        transport.close.assert_called_once()

    async def test_ipv6_received_scope_does_not_depend_on_adapter_description(self):
        service = sdk.REMOTEPAIRING_SERVICE_NAME
        instance, host = 'Phone.' + service, 'phone.local.'
        records = [sdk._build_rr(service, sdk.QTYPE_PTR, sdk.encode_name(instance), 120, False),
                   sdk._build_rr(instance, sdk.QTYPE_SRV, sdk._encode_srv(0, 0, 49152, host), 120, True),
                   sdk._build_rr(host, sdk.QTYPE_AAAA, socket.inet_pton(socket.AF_INET6, 'fe80::2'), 120, True)]
        packet = struct.pack('!HHHHHH', 0, 0x8400, 0, 3, 0, 0) + b''.join(records)
        queue, transport = asyncio.Queue(), Mock()
        queue.put_nowait((packet, ('fe80::2', 5353, 0, 4)))
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, Mock(family=socket.AF_INET6))], queue))), \
             patch.object(d.socket, 'if_nameindex', return_value=[(4, 'test')]), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: None)):
            answers = await d.browse_service(service, timeout=.04)
        self.assertEqual(['fe80::2%4'], [address.full_ip for answer in answers for address in answer.addresses])
        transport.close.assert_called_once()

    async def test_adapter_inventory_os_error_retains_ipv4_response(self):
        queue, transport, sock = asyncio.Queue(), Mock(), Mock(family=socket.AF_INET)
        queue.put_nowait((response(), ('192.0.2.2', 5353)))
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0']), \
             patch.object(d, '_Adapters', side_effect=OSError('adapter description unavailable')):
            answers = await d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=.04)
        self.assertEqual(['192.0.2.2'], [address.full_ip for answer in answers for address in answer.addresses])
        transport.close.assert_called_once()

    async def test_malformed_datagram_does_not_hide_later_valid_response(self):
        queue = asyncio.Queue()
        # One declared RR but no bytes: the current parser raises ValueError.
        queue.put_nowait((struct.pack('!HHHHHH', 0, 0x8400, 0, 1, 0, 0), ('192.0.2.1', 5353)))
        queue.put_nowait((response(), ('192.0.2.2', 5353)))
        transport, sock = Mock(), Mock(family=socket.AF_INET)
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0'], create=True), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'test-iface')):
            answers = await d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=.02)
        self.assertEqual(1, len(answers))
        self.assertEqual('192.0.2.2', answers[0].addresses[0].ip)
        transport.close.assert_called_once()

    async def test_lost_initial_query_is_retried_within_original_deadline(self):
        queue, transport = asyncio.Queue(), Mock()
        def send(packet, destination):
            if sock.sendto.call_count == 2:
                queue.put_nowait((response(), ('192.0.2.2', 5353)))
        sock = Mock(family=socket.AF_INET)
        sock.sendto.side_effect = send
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0'], create=True), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'test-iface')), \
             patch.object(d, 'QUERY_RETRY_SECONDS', .01, create=True):
            started = asyncio.get_running_loop().time()
            answers = await d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=.035)
            elapsed = asyncio.get_running_loop().time() - started
        self.assertEqual(1, len(answers))
        self.assertLess(elapsed, .15)
        transport.close.assert_called_once()

    async def test_queries_reach_both_ipv4_links_and_one_bad_link_does_not_block(self):
        transport, sock = Mock(), Mock(family=socket.AF_INET)
        def option(level, kind, value):
            if value == socket.inet_aton('192.0.2.1'):
                raise OSError('disconnected adapter')
        sock.setsockopt.side_effect = option
        with patch.object(d, '_ipv4_interface_addresses', return_value=['192.0.2.1', '198.51.100.1'], create=True):
            await d._send_query_all([(transport, sock)], b'query')
        self.assertEqual(2, sock.setsockopt.call_count)
        sock.sendto.assert_called_once_with(b'query', (sdk.MDNS_MCAST_V4, 5353))

    async def test_ipv6_inventory_error_does_not_abort_working_ipv4_query(self):
        v4, v6 = Mock(), Mock()
        v4_socket = Mock(family=socket.AF_INET)
        with patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0'], create=True), \
             patch.object(d.socket, 'if_nameindex', side_effect=OSError('interface inventory unavailable')):
            await d._send_query_all([(v4, v4_socket),
                                     (v6, Mock(family=socket.AF_INET6))], b'query')
        v4_socket.sendto.assert_called_once()
        v6.sendto.assert_not_called()

    async def test_queued_ipv4_writes_do_not_all_use_last_interface(self):
        current, queued, sent = [None], [], []
        sock = Mock(family=socket.AF_INET)
        sock.setsockopt.side_effect = lambda level, kind, value: current.__setitem__(0, value)
        sock.sendto.side_effect = lambda *_: sent.append(current[0])
        transport = Mock()
        # Windows Proactor queues subsequent datagrams while one write is
        # pending; socket multicast options are read when each OS send starts.
        transport.sendto.side_effect = lambda *_: queued.append(True)
        links = ['192.0.2.1', '198.51.100.1']
        with patch.object(d, '_ipv4_interface_addresses', return_value=links):
            await d._send_query_all([(transport, sock)], b'query')
        sent.extend(current[0] for _ in queued)
        self.assertEqual([socket.inet_aton(link) for link in links], sent)

    def test_followup_name_preserves_non_ascii_dns_octets(self):
        self.assertEqual('ißphone._remotepairing._tcp.local.',
            d._dns_name('IßPhone._REMOTEPAIRING._TCP.LOCAL.'))

    async def test_all_listeners_unavailable_preserves_socket_failure(self):
        denied = OSError(errno.EACCES, 'UDP denied')
        with patch.object(d, '_bind_ipv4', AsyncMock(side_effect=denied)), \
             patch.object(d, '_bind_ipv6_all_ifaces', AsyncMock(side_effect=denied)):
            with self.assertRaises(OSError) as raised:
                await d._open_mdns_sockets()
        self.assertIs(denied, raised.exception.__cause__)

    async def test_duplicate_announcements_produce_one_endpoint(self):
        queue = asyncio.Queue()
        for _ in range(20): queue.put_nowait((response(), ('192.0.2.2', 5353)))
        transport, sock = Mock(), Mock(family=socket.AF_INET)
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0'], create=True), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'test-iface')):
            answers = await d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=.06)
        self.assertEqual(1, len(answers))
        self.assertEqual(1, len(answers[0].addresses))

    async def test_discovery_cancellation_closes_listener(self):
        queue, entered = asyncio.Queue(), asyncio.Event()
        transport, sock = Mock(), Mock(family=socket.AF_INET)
        sock.sendto.side_effect = lambda *_: entered.set()
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0'], create=True), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'test-iface')):
            task = asyncio.create_task(d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=15))
            await entered.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        transport.close.assert_called_once()

    async def test_partial_reply_is_resolved_without_extending_deadline(self):
        queue, transport = asyncio.Queue(), Mock()
        name = 'Phone.' + sdk.REMOTEPAIRING_SERVICE_NAME
        pointer = sdk._build_rr(sdk.REMOTEPAIRING_SERVICE_NAME, sdk.QTYPE_PTR, sdk.encode_name(name), 120, False)
        queue.put_nowait((struct.pack('!HHHHHH', 0, 0x8400, 0, 1, 0, 0) + pointer, ('192.0.2.2', 5353)))
        query = sdk.build_query(name.casefold(), sdk.QTYPE_SRV)
        def send(packet, *_):
            if packet == query: queue.put_nowait((response(), ('192.0.2.2', 5353)))
        sock = Mock(family=socket.AF_INET)
        sock.sendto.side_effect = send
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0'], create=True), \
             patch.object(d, 'QUERY_RETRY_SECONDS', .01, create=True), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'test-iface')):
            answers = await d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=.04)
        self.assertEqual(1, len(answers))
        self.assertIn(query, [call.args[0] for call in sock.sendto.call_args_list])

    async def test_one_address_family_does_not_stop_resolving_other_family(self):
        for initial, missing in (('192.0.2.2', '2001:db8::2'), ('2001:db8::2', '192.0.2.2')):
            with self.subTest(initial=initial):
                kind = sdk.QTYPE_AAAA if ':' in missing else sdk.QTYPE_A
                family = socket.AF_INET6 if ':' in missing else socket.AF_INET
                query = sdk.build_query('phone.local.', kind)
                rr = sdk._build_rr('phone.local.', kind, socket.inet_pton(family, missing), 120, True)
                reply = struct.pack('!HHHHHH', 0, 0x8400, 0, 1, 0, 0) + rr
                queue, transport, sock = asyncio.Queue(), Mock(), Mock(family=socket.AF_INET)
                queue.put_nowait((response(initial), ('192.0.2.2', 5353)))
                def send(packet, *_):
                    if packet == query: queue.put_nowait((reply, ('192.0.2.2', 5353)))
                sock.sendto.side_effect = send
                with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
                     patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0']), \
                     patch.object(d, 'QUERY_RETRY_SECONDS', .01), \
                     patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'test-iface')):
                    answers = await d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=.06)
                self.assertEqual({initial, missing}, {address.ip for answer in answers for address in answer.addresses})
                transport.close.assert_called_once()

    async def test_unrelated_lan_announcements_cannot_fill_selected_service_cache(self):
        other = 'Other._googlecast._tcp.local.'
        records = [sdk._build_rr(other, sdk.QTYPE_SRV, sdk._encode_srv(0, 0, 8009, 'other.local.'), 120, True),
                   sdk._build_rr(other, sdk.QTYPE_TXT, b'\x04junk', 120, True),
                   sdk._build_rr('other.local.', sdk.QTYPE_A, socket.inet_aton('198.51.100.2'), 120, True)]
        packet = struct.pack('!HHHHHH', 0, 0x8400, 0, 3, 0, 0) + b''.join(records)
        queue, transport, sock = asyncio.Queue(), Mock(), Mock(family=socket.AF_INET)
        queue.put_nowait((packet, ('198.51.100.2', 5353)))
        queue.put_nowait((response(), ('192.0.2.2', 5353)))
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0']), \
             patch.object(d, 'MAX_DISCOVERY_NAMES', 1), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'test-iface')):
            answers = await d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=.04)
        self.assertEqual(['192.0.2.2'], [address.ip for answer in answers for address in answer.addresses])
        transport.close.assert_called_once()

    async def test_split_reply_reaches_ipv6_when_ipv4_verification_fails(self):
        # Exercise the tracked DNS parser, missing-family follow-up and bridge
        # route verification together. Endpoints are controlled, with no LAN
        # socket or phone connection; rejected routes must still be released.
        import usb_touch_bridge as b
        queue, transport, sock = asyncio.Queue(), Mock(), Mock(family=socket.AF_INET)
        queue.put_nowait((response(), ('192.0.2.2', 5353)))
        query = sdk.build_query('phone.local.', sdk.QTYPE_AAAA)
        rr = sdk._build_rr('phone.local.', sdk.QTYPE_AAAA,
                          socket.inet_pton(socket.AF_INET6, '2001:db8::2'), 120, True)
        reply = struct.pack('!HHHHHH', 0, 0x8400, 0, 1, 0, 0) + rr
        def send(packet, *_):
            if packet == query: queue.put_nowait((reply, ('192.0.2.2', 5353)))
        sock.sendto.side_effect = send
        refused = []
        healthy = SimpleNamespace(connect=AsyncMock(), close=AsyncMock())
        def connect_factory(udid, host, port):
            self.assertEqual(('selected', 49152), (udid, port))
            if host == '2001:db8::2': return healthy
            self.assertEqual('192.0.2.2', host)
            service = SimpleNamespace(connect=AsyncMock(side_effect=ConnectionRefusedError()), close=AsyncMock())
            refused.append(service)
            return service
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0']), \
             patch.object(d, 'QUERY_RETRY_SECONDS', .01), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: None)), \
             patch.object(b, 'browse_remotepairing', d.browse_remotepairing), \
             patch.object(b, 'RemotePairingTunnelService', side_effect=connect_factory), \
             patch.object(b, 'REMOTE_PAIRING_ROUTE_RETRY_DELAY_SECONDS', .005), \
             patch('ifaddr.get_adapters', return_value=[]):
            services = await b.get_remote_pairing_tunnel_services(.06, 'selected')
        self.assertEqual([healthy], services)
        healthy.connect.assert_awaited_once_with(autopair=False)
        healthy.close.assert_not_awaited()
        self.assertEqual(2, len(refused))
        for service in refused:
            service.connect.assert_awaited_once_with(autopair=False)
            service.close.assert_awaited_once()
        transport.close.assert_called_once()

    async def test_goodbye_releases_cache_capacity_for_new_service(self):
        service = sdk.REMOTEPAIRING_SERVICE_NAME
        old = 'Old.' + service
        old_host = 'old.local.'
        def announce(ttl):
            records = [sdk._build_rr(service, sdk.QTYPE_PTR, sdk.encode_name(old), ttl, False),
                       sdk._build_rr(old, sdk.QTYPE_SRV, sdk._encode_srv(0, 0, 49152, old_host), ttl, True),
                       sdk._build_rr(old_host, sdk.QTYPE_A, socket.inet_aton('198.51.100.2'), ttl, True)]
            return struct.pack('!HHHHHH', 0, 0x8400, 0, 3, 0, 0) + b''.join(records)
        queue, transport, sock = asyncio.Queue(), Mock(), Mock(family=socket.AF_INET)
        for packet in (announce(120), announce(0), response()):
            queue.put_nowait((packet, ('192.0.2.2', 5353)))
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], queue))), \
             patch.object(d, '_ipv4_interface_addresses', return_value=['0.0.0.0']), \
             patch.object(d, 'MAX_DISCOVERY_NAMES', 1), \
             patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'test-iface')):
            answers = await d.browse_service(service, timeout=.04)
        self.assertEqual(['192.0.2.2'], [address.ip for answer in answers for address in answer.addresses])
        transport.close.assert_called_once()

    async def test_real_local_udp_recovers_from_lost_query_and_closes_socket(self):
        # Actual UDP endpoints, limited to loopback: no phone or LAN query.
        class Responder(asyncio.DatagramProtocol):
            received = 0
            def connection_made(self, transport): self.transport = transport
            def datagram_received(self, data, sender):
                self.received += 1
                if self.received >= 2: self.transport.sendto(response(), sender)
        loop = asyncio.get_running_loop()
        peer, protocol = await loop.create_datagram_endpoint(Responder, local_addr=('127.0.0.1', 0))
        peer_address = peer.get_extra_info('sockname')
        opened = []
        async def open_local():
            queue = asyncio.Queue(maxsize=32)
            pair = await d._bind_ipv4(queue)
            opened.append(pair)
            return [pair], queue
        async def query_local(transports, packet):
            for _, sock in transports: sock.sendto(packet, peer_address)
        try:
            with patch.object(d, 'MDNS_PORT', 0), \
                 patch.object(d, '_ipv4_interface_addresses', return_value=['127.0.0.1'], create=True), \
                 patch.object(d, '_open_mdns_sockets', side_effect=open_local), \
                 patch.object(d, '_send_query_all', side_effect=query_local), \
                 patch.object(d, 'QUERY_RETRY_SECONDS', .05, create=True), \
                 patch.object(d, '_Adapters', return_value=SimpleNamespace(pick_iface_for_ip=lambda *_: 'loopback-test')):
                answers = await d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=.3)
            self.assertEqual(1, len(answers))
            self.assertGreaterEqual(protocol.received, 2)
            for transport, sock in opened:
                self.assertTrue(transport.is_closing())
                for _ in range(20):
                    if sock.fileno() == -1: break
                    await asyncio.sleep(.01)
                self.assertEqual(-1, sock.fileno())
        finally:
            peer.close()
            for transport, sock in opened:
                transport.close()


    async def test_adapter_inventory_failure_still_closes_sockets(self):
        transport, sock = Mock(), Mock(family=socket.AF_INET)
        with patch.object(d, '_open_mdns_sockets', AsyncMock(return_value=([(transport, sock)], asyncio.Queue()))), \
             patch.object(d, '_Adapters', side_effect=RuntimeError('adapter failure')):
            with self.assertRaises(RuntimeError):
                await d.browse_service(sdk.REMOTEPAIRING_SERVICE_NAME, timeout=.01)
        transport.close.assert_called_once()


if __name__ == '__main__':
    unittest.main()
