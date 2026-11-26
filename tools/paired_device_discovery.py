"""Read-only LAN inventory of devices with existing local pairing records.

Never identify a modern iPhone by its advertised MAC, name or opaque TXT UUID.
Only a verified Lockdown session or RemotePairing pair-verify proves identity.
No USB connection, Pair request, tunnel, developer service or HID is started.
"""
from __future__ import annotations

import asyncio
import contextlib
import json
import plistlib
import re
import sys

from pymobiledevice3.common import get_home_folder
from pymobiledevice3.lockdown import TcpLockdownClient, SERVICE_PORT
from pymobiledevice3.service_connection import ServiceConnection
from pymobiledevice3.pair_records import OSUTILS
from mdns_discovery import browse_service, MdnsDiscoveryError

UDID = re.compile(r'(?:[0-9a-fA-F]{8}-?[0-9a-fA-F]{16}|[0-9a-fA-F]{40})\Z')


def identity(value):
    return value.replace('-', '').lower() if isinstance(value, str) else ''


def load_records(excluded):
    records, remote = {}, set()
    for folder in (OSUTILS.pair_record_path, get_home_folder()):
        try:
            files = sorted(folder.glob('*.plist'))
        except OSError:
            continue
        for file in files:
            is_remote = file.stem.startswith('remote_')
            udid = file.stem[7:] if is_remote else file.stem
            if not UDID.fullmatch(udid) or identity(udid) in excluded:
                continue
            try:
                if file.stat().st_size > 1024 * 1024:
                    continue
                record = plistlib.loads(file.read_bytes())
                if not isinstance(record, dict):
                    continue
                if is_remote:
                    remote.add(udid)
                elif record.get('HostID') and record.get('HostCertificate') and record.get('HostPrivateKey'):
                    records.setdefault(udid, record)
            except (OSError, ValueError, plistlib.InvalidFileException):
                continue
    return dict(list(records.items())[:64]), sorted(remote)[:64]


async def discover(bridge, excluded=(), *, timeout=3.0, verification_timeout=8):
    records, remote = load_records({identity(value) for value in excluded})
    if not records and not remote:
        return []
    found = {}
    semaphore = asyncio.Semaphore(4)
    tasks = []

    async def close(owner):
        if owner is not None:
            with contextlib.suppress(Exception):
                await asyncio.wait_for(owner.close(), 1)

    async def lockdown_probe(udid, record, host, port):
        client = connection = None
        async with semaphore:
            if identity(udid) in found:
                return
            try:
                async with asyncio.timeout(2):
                    connection = await ServiceConnection.create_using_tcp(host, port, keep_alive=False)
                    client = await TcpLockdownClient.create(connection, hostname=host, identifier=udid,
                        pair_record=record, autopair=False, port=port, keep_alive=False)
                    # The SDK also queries unauthenticated values. Neither the
                    # supplied identifier nor those values alone prove trust.
                    if client.paired and identity(client.udid) == identity(udid):
                        found[identity(udid)] = dict(udid=udid, name=client.all_values.get('DeviceName'),
                            productType=client.product_type, osVersion=client.product_version)
            except Exception:
                pass
            finally:
                await close(client)
                await close(connection)

    async def remote_probe(udid, host, port):
        service = None
        async with semaphore:
            if identity(udid) in found:
                return
            try:
                service = bridge.RemotePairingTunnelService(udid, host, port)
                async with asyncio.timeout(2):
                    # This uses only remote_<udid>.plist's peer key. With
                    # autopair disabled an unknown/revoked peer is rejected.
                    await service.connect(autopair=False)
                    peer = (service.handshake_info or {}).get('peerDeviceInfo', {})
                    if identity(peer.get('identifier')) == identity(udid):
                        found.setdefault(identity(udid), dict(udid=udid))
            except Exception:
                pass
            finally:
                await close(service)

    kinds = []
    if records:
        kinds.append(('_apple-mobdev2._tcp.local.', records))
    if remote:
        kinds.append(('_remotepairing._tcp.local.', remote))
    answers = await asyncio.gather(*(browse_service(kind, timeout) for kind, _ in kinds),
                                   return_exceptions=True)
    if all(isinstance(answer, Exception) for answer in answers):
        raise MdnsDiscoveryError('Local wireless discovery is unavailable')
    try:
        import ifaddr
        adapters = ifaddr.get_adapters()
    except OSError:
        adapters = []
    try:
        for (kind, candidates), services in zip(kinds, answers):
            if isinstance(services, Exception):
                continue
            endpoints = set()
            for answer in services:
                for address in answer.addresses:
                    with contextlib.suppress(OSError, ValueError):
                        for host in bridge._remote_pairing_route_hosts(address.full_ip, answer.addresses, adapters):
                            if len(endpoints) < 16:
                                # mobdev2 advertises presence, but its SRV port
                                # is not a reliable Lockdown endpoint (physical
                                # iOS peers can advertise 32498 while accepting
                                # trusted Lockdown sessions only on 62078).
                                endpoints.add((host, SERVICE_PORT if isinstance(candidates, dict) else answer.port))
            # Give every identity a turn on each route. A stale early record
            # must not consume the entire pass before newer phones are tried.
            for host, port in sorted(endpoints):
                for udid in candidates:
                    probe = (lockdown_probe(udid, candidates[udid], host, port)
                             if isinstance(candidates, dict) else remote_probe(udid, host, port))
                    tasks.append(asyncio.create_task(probe))
        # Bound the entire LAN pass, including semaphore waiters. Cancelling
        # the pass still runs every established client's close in its finally.
        if tasks:
            await asyncio.wait(tasks, timeout=verification_timeout)
    finally:
        for task in tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
    return sorted(found.values(), key=lambda device: device['udid'])


def run(bridge):
    try:
        line = sys.stdin.readline(16385)
        request = json.loads(line)
        excluded = request.get('excludeUdids', [])
        if len(line) > 16384 or not isinstance(excluded, list) or len(excluded) > 128 or any(
                not isinstance(value, str) or not UDID.fullmatch(value) for value in excluded):
            raise ValueError('Invalid discovery exclusions')
        devices = asyncio.run(discover(bridge, excluded))
        print(json.dumps(dict(event='paired_devices', devices=devices)), flush=True)
        return 0
    except Exception as error:
        # Do not emit addresses, credentials or pair-record contents.
        print(json.dumps(dict(event='paired_discovery_failed', code=type(error).__name__)), flush=True)
        return 1
