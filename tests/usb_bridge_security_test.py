"""Offline regression checks for the bridge's device and pairing boundaries."""
import asyncio
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge
from iostouch.qt import usbmuxd_server

UDID = '00008120-0123456789ABCDEF'
OTHER = '00008120-1111111111111111'


class TestPairRecordBoundary(unittest.IsolatedAsyncioTestCase):
    async def test_pair_record_only_reads_selected_device(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary) / 'Lockdown'
            root.mkdir()
            outside = Path(temporary) / 'outside.plist'
            outside.write_bytes(b'outside sentinel')
            (root / (UDID + '.plist')).write_bytes(b'own record')
            (root / (OTHER + '.plist')).write_bytes(b'other record')
            with patch.object(usbmuxd_server, '_pair_record_dir', return_value=root):
                server = usbmuxd_server.UsbmuxdServer(Mock(), UDID)
                for identifier in ('', '../outside', '..\\outside', str(outside.with_suffix('')),
                                   OTHER, 'SystemConfiguration', UDID + ':stream'):
                    self.assertIsNone(server._load_pair_record(identifier), identifier)
                self.assertEqual(server._load_pair_record(UDID), b'own record')
                self.assertEqual(server._load_pair_record(UDID.replace('-', '').lower()), b'own record')

    async def test_pair_record_cannot_escape_through_a_link(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary) / 'Lockdown'
            root.mkdir()
            outside = Path(temporary) / 'outside.plist'
            outside.write_bytes(b'outside sentinel')
            with patch.object(usbmuxd_server, '_pair_record_dir', return_value=root):
                server = usbmuxd_server.UsbmuxdServer(Mock(), UDID)
                original_resolve = Path.resolve

                def resolve(path, *args, **kwargs):
                    if path.name == UDID + '.plist':
                        return outside
                    return original_resolve(path, *args, **kwargs)

                with patch.object(Path, 'resolve', resolve):
                    self.assertIsNone(server._load_pair_record(UDID))

    async def test_invalid_save_and_delete_do_not_mutate_records(self):
        server = usbmuxd_server.UsbmuxdServer(Mock(), UDID, pair_records={UDID: b'original'})
        await server.start()
        reader, writer = await asyncio.open_connection('127.0.0.1', server.port)
        try:
            for request in (
                {'MessageType': 'SavePairRecord', 'PairRecordID': OTHER, 'PairRecordData': b'bad'},
                {'MessageType': 'DeletePairRecord', 'PairRecordID': '../outside'},
            ):
                writer.write(server._frame(1, request))
                await writer.drain()
                _, result = await asyncio.wait_for(server._read_msg(reader), 3)
                self.assertEqual(result['Number'], usbmuxd_server.RESULT_BADDEV)
            self.assertEqual(server.pair_records, {UDID: b'original'})
        finally:
            writer.close()
            await writer.wait_closed()
            await server.stop()


class TestDeviceIdentity(unittest.IsolatedAsyncioTestCase):
    async def test_single_mismatched_or_unreadable_phone_is_not_adopted(self):
        for serial in (OTHER, ''):
            device = SimpleNamespace(activated=True, serial=serial, dev=object())
            session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, udid=UDID)
            with patch.object(bridge, '_get_usb_backend', return_value=object()), \
                 patch.object(bridge, '_find_usb_devices', return_value=[device]), \
                 patch.object(bridge, '_UsbMuxTransport') as transport:
                await session._start_capture_mux()
                transport.assert_not_called()
                self.assertIsNone(session._usb_mux_server)

    async def test_recovery_rejects_single_nonmatching_phone(self):
        for serial in (OTHER, ''):
            device = Mock(iSerialNumber=1)
            session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, udid=UDID)
            error = bridge.DeviceNotFoundError(UDID)
            with patch('usb.core.find', return_value=[device]), \
                 patch('usb.util.get_string', return_value=serial), \
                 patch.object(bridge, '_get_usb_backend', return_value=object()), \
                 patch.object(bridge, '_interface_with_subclass', return_value=object()), \
                 patch.object(bridge, '_UsbMuxTransport') as transport:
                with self.assertRaises(bridge.DeviceNotFoundError):
                    await session._recover_lockdown_via_capture_mux(error)
                transport.assert_not_called()

    async def test_lockdown_identity_is_checked_before_provisioning(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, udid=UDID)
        with patch.object(session, '_provision_remote_pairing', new_callable=AsyncMock) as provision:
            with self.assertRaises(bridge.BridgePrerequisiteError) as failure:
                await session._connect_with_lockdown(SimpleNamespace(udid=OTHER))
            self.assertEqual(failure.exception.code, 'device_identity_mismatch')
            provision.assert_not_called()

    async def test_matching_lockdown_preserves_case_and_hyphen_compatibility(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, udid=UDID)
        with patch.object(session, '_provision_remote_pairing', new_callable=AsyncMock) as provision, \
             patch.object(session, '_preflight_developer_environment', side_effect=RuntimeError('verified')):
            with self.assertRaisesRegex(RuntimeError, 'verified'):
                await session._connect_with_lockdown(SimpleNamespace(udid=UDID.replace('-', '').lower()))
            provision.assert_awaited_once()


if __name__ == '__main__':
    unittest.main()
