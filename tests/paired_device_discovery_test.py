"""Wireless inventory must prove trust and identity without a new pairing."""
import asyncio
import plistlib
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import AsyncMock, Mock, patch

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / ('tools' if (root / 'tools' / 'usb_touch_bridge.py').exists() else 'src')))
import paired_device_discovery as d

A = '00008150-0006650A0144C01C'
B = '00008150-001903580A9B401C'


class DiscoveryTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.connection = NS(close=AsyncMock())
        self.client = NS(paired=True, udid=A, all_values={'DeviceName': 'Phone'},
                         product_type='iPhone17,1', product_version='18.0', close=AsyncMock())
        # Intentionally use a randomized MAC and opaque TXT identifier.
        self.answer = NS(instance='aa:bb:cc:dd:ee:ff@phone', port=62078,
            addresses=[NS(full_ip='192.0.2.3')], properties={'identifier': 'opaque'})
        self.bridge = NS(_remote_pairing_route_hosts=lambda host, *_: [host],
                         RemotePairingTunnelService=Mock())
        self.patches = [
            patch.object(d, 'load_records', return_value=({A: {'HostID': 'known'}}, [])),
            patch.object(d, 'browse_service', AsyncMock(return_value=[self.answer])),
            patch.object(d.ServiceConnection, 'create_using_tcp', AsyncMock(return_value=self.connection)),
            patch.object(d.TcpLockdownClient, 'create', AsyncMock(return_value=self.client)),
        ]
        for p in self.patches:
            p.start()
            self.addCleanup(p.stop)

    async def test_random_mac_does_not_hide_verified_device(self):
        devices = await d.discover(self.bridge)
        self.assertEqual([A], [device['udid'] for device in devices])
        self.assertEqual('Phone', devices[0]['name'])
        self.assertFalse(d.TcpLockdownClient.create.call_args.kwargs['autopair'])
        self.client.close.assert_awaited_once()
        self.connection.close.assert_awaited_once()

    async def test_mobdev2_srv_port_is_not_the_lockdown_port(self):
        self.answer.port = 32498
        devices = await d.discover(self.bridge)
        self.assertEqual([A], [device['udid'] for device in devices])
        d.ServiceConnection.create_using_tcp.assert_awaited_once_with('192.0.2.3', 62078, keep_alive=False)
        self.assertEqual(62078, d.TcpLockdownClient.create.call_args.kwargs['port'])

    async def test_stale_records_do_not_starve_later_identities(self):
        d.load_records.return_value = ({A: {'HostID': 'stale'}, B: {'HostID': 'valid'}}, [])
        self.answer.addresses = [NS(full_ip=f'192.0.2.{i}') for i in range(1, 9)]
        attempted = []
        async def create(*args, **kwargs):
            attempted.append(kwargs['identifier'])
            if kwargs['identifier'] == A:
                await asyncio.Event().wait()
            self.client.udid = B
            return self.client
        d.TcpLockdownClient.create.side_effect = create
        devices = await d.discover(self.bridge, verification_timeout=.03)
        self.assertIn(B, attempted[:4])
        self.assertEqual([B], [device['udid'] for device in devices])

    async def test_wrong_identity_and_revoked_trust_are_not_online(self):
        for paired, udid in ((False, A), (True, B), (True, None)):
            self.client.paired, self.client.udid = paired, udid
            self.assertEqual([], await d.discover(self.bridge))

    async def test_cancellation_closes_connection_during_initialization(self):
        async def blocked(*args, **kwargs):
            await asyncio.Event().wait()
        d.TcpLockdownClient.create.side_effect = blocked
        self.assertEqual([], await d.discover(self.bridge, verification_timeout=.02))
        self.connection.close.assert_awaited_once()

    async def test_remote_verification_requires_matching_handshake_identity(self):
        service = NS(connect=AsyncMock(), close=AsyncMock(), handshake_info={'peerDeviceInfo': {'identifier': B}})
        self.bridge.RemotePairingTunnelService.return_value = service
        d.load_records.return_value = ({}, [A])
        self.assertEqual([], await d.discover(self.bridge))
        service.handshake_info['peerDeviceInfo']['identifier'] = A
        self.assertEqual([{'udid': A}], await d.discover(self.bridge))
        service.connect.assert_awaited_with(autopair=False)
        self.assertEqual(2, service.close.await_count)

    async def test_no_credentials_means_no_network_probe(self):
        d.load_records.return_value = ({}, [])
        self.assertEqual([], await d.discover(self.bridge))
        d.browse_service.assert_not_awaited()

    async def test_no_multicast_listener_reports_failure_instead_of_empty_success(self):
        d.browse_service.side_effect = d.MdnsDiscoveryError('unavailable')
        with self.assertRaises(d.MdnsDiscoveryError):
            await d.discover(self.bridge)


class RecordTests(unittest.TestCase):
    def test_loads_apple_and_local_records_but_excludes_active_devices(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            record = dict(HostID='known', HostCertificate=b'cert', HostPrivateKey=b'key')
            (root / f'{A}.plist').write_bytes(plistlib.dumps(record))
            (root / f'{B}.plist').write_bytes(b'corrupted')
            (root / 'SystemConfiguration.plist').write_bytes(plistlib.dumps(record))
            (root / f'remote_{A}.plist').write_bytes(plistlib.dumps({'private_key': b'key'}))
            with patch.object(d, 'OSUTILS', NS(pair_record_path=root)), patch.object(d, 'get_home_folder', return_value=root):
                records, remote = d.load_records(set())
                self.assertEqual([A], list(records))
                self.assertEqual([A], remote)
                self.assertEqual(({}, []), d.load_records({d.identity(A)}))


if __name__ == '__main__':
    unittest.main()
