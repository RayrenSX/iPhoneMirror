"""Compatibility checks for the trusted file-sharing command and iOS 17 USB status."""

import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


class PreviewSharedFilesTests(unittest.IsolatedAsyncioTestCase):
    async def invoke(self, request, *, transport='usb', paired=True, match=True):
        udid = '00008101-0000000000000001'
        ipc = SimpleNamespace(emit=AsyncMock(), close=AsyncMock())
        session = SimpleNamespace(_start_capture_mux=AsyncMock(), _close_capture_mux=Mock())
        lockdown = SimpleNamespace(
            udid=udid if match else '00008101-0000000000000002',
            paired=paired, close=AsyncMock())
        management = SimpleNamespace(request=AsyncMock(return_value={'apps': []}))
        with patch.object(bridge, 'BridgeChannel', return_value=ipc), \
             patch.object(bridge, 'TouchSession', return_value=session), \
             patch.object(bridge, 'create_using_usbmux', AsyncMock(return_value=lockdown)) as connect, \
             patch('device_management.DeviceManagement', return_value=management):
            success = await bridge.shared_files_async(udid, request, transport)
        if request.get('action') in ('shared_document_apps', 'upload_file'):
            connect.assert_awaited_once_with(
                serial=udid, connection_type='USB' if transport == 'usb' else 'Network',
                autopair=False)
            lockdown.close.assert_awaited_once()
            self.assertEqual(session._start_capture_mux.await_count, int(transport == 'usb'))
        else:
            connect.assert_not_awaited()
        session._close_capture_mux.assert_called_once()
        ipc.close.assert_awaited_once()
        return success, management.request, ipc.emit.await_args.args[0]

    async def test_lists_apps_without_starting_developer_services(self):
        success, request, report = await self.invoke({'action': 'shared_document_apps'})
        self.assertTrue(success)
        request.assert_awaited_once_with('shared_document_apps', {})
        self.assertEqual(report, {'event': 'shared_files_result', 'result': {'apps': []}})

    async def test_network_upload_is_scoped_to_selected_apps_documents(self):
        success, request, _ = await self.invoke({
            'action': 'upload_file', 'bundleId': 'example.reader', 'sourcePath': 'C:/文書.pdf',
            'root': 'media', 'path': 'somewhere',
        }, transport='wireless')
        self.assertTrue(success)
        request.assert_awaited_once_with('upload_file', {
            'root': 'documents', 'path': '', 'bundleId': 'example.reader', 'sourcePath': 'C:/文書.pdf',
        })

    async def test_never_calls_file_services_without_identity_and_trust(self):
        for options in ({'paired': False}, {'match': False}):
            with self.subTest(options=options):
                success, request, report = await self.invoke({'action': 'shared_document_apps'}, **options)
                self.assertFalse(success)
                self.assertIn('code', report)
                request.assert_not_awaited()

    async def test_rejects_unrelated_device_actions_before_opening_device(self):
        success, request, _ = await self.invoke({'action': 'delete_file'})
        self.assertFalse(success)
        request.assert_not_awaited()

    async def test_cancellation_reaches_in_flight_upload_before_result(self):
        udid = '00008101-0000000000000001'
        started = asyncio.Event()
        cleaned = asyncio.Event()

        async def upload(*args):
            started.set()
            try:
                await asyncio.Future()
            finally:
                cleaned.set()

        async def read_cancel(count):
            self.assertEqual(count, 1)
            await started.wait()
            return b'\x03'

        ipc = SimpleNamespace(emit=AsyncMock(), close=AsyncMock(), _read_exactly=read_cancel)
        session = SimpleNamespace(_start_capture_mux=AsyncMock(), _close_capture_mux=Mock())
        lockdown = SimpleNamespace(udid=udid, paired=True, close=AsyncMock())
        management = SimpleNamespace(request=AsyncMock(side_effect=upload))
        with patch.object(bridge, 'BridgeChannel', return_value=ipc), \
             patch.object(bridge, 'TouchSession', return_value=session), \
             patch.object(bridge, 'create_using_usbmux', AsyncMock(return_value=lockdown)), \
             patch('device_management.DeviceManagement', return_value=management):
            success = await asyncio.wait_for(bridge.shared_files_async(
                udid, {'action': 'upload_file'}, cancel_from_stdin=True), 5)
        self.assertFalse(success)
        self.assertTrue(cleaned.is_set())
        lockdown.close.assert_awaited_once()
        self.assertEqual(ipc.emit.await_args.args[0]['code'], 'FILE_TRANSFER_CANCELLED')

    async def test_missing_usb_proxy_distinguishes_early_ios_17(self):
        for version, code in (
            ('17.0', 'wired_control_legacy_ios17'), ('17.3.1', 'wired_control_legacy_ios17'),
            ('17.4', 'wired_control_service_unavailable'), ('18.7.8', 'wired_control_service_unavailable'),
        ):
            with self.subTest(version=version):
                session = bridge.TouchSession(AsyncMock(), 120, 'device', 'usb')
                with patch.object(bridge.CoreDeviceTunnelProxy, 'create', AsyncMock(side_effect=
                        bridge.InvalidServiceError('missing', 'device', version))), \
                     patch.object(session, '_connect_via_remote_pairing', AsyncMock()) as remote:
                    with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                        await session._run_tunnel_attempt(SimpleNamespace(product_version=version))
                self.assertEqual(raised.exception.code, code)
                remote.assert_not_awaited()


if __name__ == '__main__':
    unittest.main()
