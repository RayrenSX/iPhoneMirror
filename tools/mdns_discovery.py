"""Owned, bounded mDNS browsing for the bridge's pinned Bonjour protocol.

Keep the SDK's wire parser and result types. Socket lifetime, optional address
families, interface fanout and query retries belong to this bridge instead of
patching the installed SDK or changing the machine's network configuration.
"""
from __future__ import annotations

import asyncio
import contextlib
import ipaddress
import logging
import socket
import struct
from collections import defaultdict

import ifaddr
from pymobiledevice3.bonjour import (
    Address, ServiceInstance, _Adapters, build_query, encode_name, parse_mdns_message,
    MDNS_PORT, MDNS_MCAST_V4, MDNS_MCAST_V6, QTYPE_A, QTYPE_AAAA,
    QTYPE_PTR, QTYPE_SRV, QTYPE_TXT, REMOTEPAIRING_SERVICE_NAME,
)

log = logging.getLogger('iphoneMirror.mdns')
QUERY_RETRY_SECONDS = 1.0
MAX_QUEUED_DATAGRAMS = 256
MAX_DISCOVERY_NAMES = 256
_DNS_CASE_TABLE = str.maketrans('ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')


class MdnsDiscoveryError(OSError):
    """No local discovery listener could be opened; pairing was not attempted."""


def _dns_name(name):
    # DNS case folding applies to ASCII octets. Unicode casefold can rewrite
    # an instance's UTF-8 label (e.g. ß -> ss), breaking follow-up queries.
    return name.translate(_DNS_CASE_TABLE)


class _DatagramProtocol(asyncio.DatagramProtocol):
    def __init__(self, queue):
        self.queue = queue

    def datagram_received(self, data, addr):
        # A busy LAN must not grow discovery memory without a bound.
        if not self.queue.full():
            self.queue.put_nowait((data, addr))


def _ipv4_interface_addresses():
    addresses = set()
    try:
        for adapter in ifaddr.get_adapters():
            for entry in adapter.ips:
                if isinstance(entry.ip, str):
                    address = ipaddress.IPv4Address(entry.ip)
                    if not (address.is_loopback or address.is_unspecified):
                        addresses.add(str(address))
    except OSError:
        log.debug('IPv4 adapter enumeration failed; using the default interface.')
    return sorted(addresses) or ['0.0.0.0']


async def _bind(queue, family):
    sock = socket.socket(family, socket.SOCK_DGRAM)
    try:
        memberships, join_error = 0, None
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        if hasattr(socket, 'SO_REUSEPORT'):
            with contextlib.suppress(OSError):
                sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
        if family == socket.AF_INET:
            sock.bind(('0.0.0.0', MDNS_PORT))
            sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 255)
            for address in _ipv4_interface_addresses():
                try:
                    sock.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP,
                        socket.inet_aton(MDNS_MCAST_V4) + socket.inet_aton(address))
                    memberships += 1
                except OSError as error:
                    join_error = error
        else:
            # Avoid a dual-stack listener preventing the IPv4 listener's bind.
            sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
            sock.bind(('::', MDNS_PORT))
            sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_MULTICAST_HOPS, 255)
            group = socket.inet_pton(socket.AF_INET6, MDNS_MCAST_V6)
            for index, _ in socket.if_nameindex():
                try:
                    sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_JOIN_GROUP,
                                    group + struct.pack('@I', index))
                    memberships += 1
                except OSError as error:
                    join_error = error
        if not memberships:
            # Binding UDP/5353 alone cannot receive our multicast replies.
            # Let the other family work, or expose the local setup failure.
            raise OSError('No usable local mDNS multicast interface for address family '
                          + str(family)) from join_error
        transport, _ = await asyncio.get_running_loop().create_datagram_endpoint(
            lambda: _DatagramProtocol(queue), sock=sock)
        return transport, sock
    except BaseException:
        # Until endpoint handoff succeeds, this coroutine owns the raw socket.
        sock.close()
        raise


async def _bind_ipv4(queue):
    return await _bind(queue, socket.AF_INET)


async def _bind_ipv6_all_ifaces(queue):
    return await _bind(queue, socket.AF_INET6)


async def _open_mdns_sockets():
    queue = asyncio.Queue(maxsize=MAX_QUEUED_DATAGRAMS)
    transports, failures = [], []
    try:
        for bind in (_bind_ipv4, _bind_ipv6_all_ifaces):
            try:
                transports.append(await bind(queue))
            except OSError as error:
                failures.append(error)
                log.info('Optional mDNS address family unavailable: %s', error)
        if not transports:
            raise MdnsDiscoveryError('Failed to open local mDNS listeners (UDP/5353); check '
                'enabled network adapters, local discovery access and port binding. '
                + '; '.join(str(error) for error in failures)) from failures[-1]
        return transports, queue
    except BaseException:
        for transport, _ in transports:
            transport.close()
        raise


async def _send_query_all(transports, packet):
    for transport, sock in transports:
        if sock.family == socket.AF_INET:
            # Default multicast egress can be Ethernet/VPN while the phone is
            # reachable on Wi-Fi/hotspot. Join and query each local IPv4 link.
            for address in _ipv4_interface_addresses():
                try:
                    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(address))
                    # Proactor's sendto queues behind pending writes, so a
                    # later IP_MULTICAST_IF change reroutes earlier datagrams.
                    # The endpoint owns this nonblocking socket for reception;
                    # submit each small UDP query directly while its interface
                    # option is current. EWOULDBLOCK is retried next query round.
                    sock.sendto(packet, (MDNS_MCAST_V4, MDNS_PORT))
                except OSError:
                    log.debug('mDNS IPv4 query failed on one interface.', exc_info=True)
        else:
            try:
                indices = socket.if_nameindex()
            except OSError:
                log.debug('mDNS IPv6 interface enumeration failed.', exc_info=True)
                continue
            for index, _ in indices:
                try:
                    transport.sendto(packet, (MDNS_MCAST_V6, MDNS_PORT, 0, index))
                except OSError:
                    log.debug('mDNS IPv6 query failed on one interface.', exc_info=True)


async def browse_service(service_type, timeout=4.0):
    service_type = _dns_name(service_type.rstrip('.')) + '.'
    loop = asyncio.get_running_loop()
    deadline = loop.time() + max(0, timeout)
    transports, queue = await _open_mdns_sockets()
    targets = set()
    services, properties = defaultdict(dict), {}
    addresses = defaultdict(dict)
    try:
        try:
            adapters = _Adapters()
        except OSError:
            # Descriptions are only hints. IPv4/global addresses and the scope
            # delivered by an IPv6 datagram remain usable without that list.
            adapters = None
            log.info('mDNS adapter descriptions unavailable; keeping packet routes.')
        next_query = loop.time()
        while loop.time() < deadline:
            now = loop.time()
            if now >= next_query:
                queries = {(service_type, QTYPE_PTR)}
                # DNS-SD replies may split PTR/SRV/address records. Resolve
                # incomplete instances instead of assuming one complete packet.
                for instance in targets:
                    if not services.get(instance):
                        queries.add((instance, QTYPE_SRV))
                    for host, _ in services.get(instance, {}):
                        known = {QTYPE_AAAA if ':' in address.ip else QTYPE_A
                                 for address in addresses.get(host, {}).values()}
                        # One family may be advertised separately or its first
                        # datagram lost. Having IPv4 must not hide an IPv6 route
                        # (or vice versa) when the first route is unreachable.
                        queries.update((host, kind) for kind in (QTYPE_A, QTYPE_AAAA)
                                       if kind not in known)
                for name, kind in sorted(queries):
                    if loop.time() >= deadline:
                        break
                    await _send_query_all(transports, build_query(name, kind, unicast=False))
                next_query = now + QUERY_RETRY_SECONDS
            remaining = min(deadline, next_query) - loop.time()
            if remaining <= 0:
                continue
            try:
                packet, sender = await asyncio.wait_for(queue.get(), remaining)
            except TimeoutError:
                continue
            try:
                records = parse_mdns_message(packet)
                for record in records:
                    for key in ('name', 'ptrdname', 'target'):
                        if key in record and len(encode_name(record[key])) > 255:
                            raise ValueError('DNS name exceeds the wire limit')
            except (ValueError, IndexError, struct.error):
                log.debug('Ignored malformed mDNS datagram.')
                continue
            for record in records:
                name, kind = _dns_name(record['name']), record['type']
                live = record.get('ttl', 0) > 0
                if kind == QTYPE_PTR and name == service_type:
                    target = _dns_name(record['ptrdname'])
                    if not live:
                        targets.discard(target)
                    elif target.endswith(service_type) and len(targets) < MAX_DISCOVERY_NAMES:
                        targets.add(target)
                elif (kind == QTYPE_SRV and name.endswith(service_type)
                      and record.get('target') and record.get('port')):
                    key = (_dns_name(record['target']), record['port'])
                    if not live:
                        services.get(name, {}).pop(key, None)
                        if not services.get(name):
                            services.pop(name, None)
                    elif (name in services or len(services) < MAX_DISCOVERY_NAMES) and len(services[name]) < 16:
                        services[name][key] = None
                elif kind == QTYPE_TXT and name.endswith(service_type):
                    if not live:
                        properties.pop(name, None)
                    elif name in properties or len(properties) < MAX_DISCOVERY_NAMES:
                        properties[name] = record.get('txt', {})
                elif kind in (QTYPE_A, QTYPE_AAAA) and record.get('address'):
                    ip = record['address']
                    if not live:
                        for key in list(addresses.get(name, {})):
                            if key[0] == ip:
                                addresses[name].pop(key)
                        if not addresses.get(name):
                            addresses.pop(name, None)
                        continue
                    family = socket.AF_INET6 if kind == QTYPE_AAAA else socket.AF_INET
                    scope = sender[3] if len(sender) == 4 else None
                    link_local = ipaddress.ip_address(ip).is_link_local
                    iface = None
                    if family == socket.AF_INET6 and link_local and scope:
                        iface = str(scope)
                    elif adapters is not None:
                        iface = adapters.pick_iface_for_ip(ip, family, scope)
                    # Global addresses need no scope. The bridge resolves an
                    # IPv4-delivered link-local AAAA using peer subnet hints.
                    if iface is None and link_local and family == socket.AF_INET6:
                        continue
                    if name not in addresses and len(addresses) >= MAX_DISCOVERY_NAMES:
                        referenced = {host for endpoints in services.values() for host, _ in endpoints}
                        if name in referenced:
                            # Keep early address packets for split replies, but
                            # unrelated printers/cast traffic cannot occupy all
                            # bounded slots needed by a learned target service.
                            unrelated = next((host for host in addresses if host not in referenced), None)
                            if unrelated is not None:
                                addresses.pop(unrelated)
                    if (name in addresses or len(addresses) < MAX_DISCOVERY_NAMES) and len(addresses[name]) < 32:
                        addresses[name][(ip, iface)] = Address(ip=ip, iface=iface)
    finally:
        for transport, _ in transports:
            transport.close()
    return [ServiceInstance(instance=instance, host=host.rstrip('.'), port=port,
                addresses=list(addresses.get(host, {}).values()), properties=properties.get(instance, {}))
            for instance in sorted(targets) for host, port in services.get(instance, {})]


async def browse_remotepairing(timeout=4.0):
    return await browse_service(REMOTEPAIRING_SERVICE_NAME, timeout)
