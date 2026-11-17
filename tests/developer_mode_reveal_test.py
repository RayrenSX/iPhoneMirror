import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


class DeveloperModeRevealTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.service = SimpleNamespace(send_recv_plist=AsyncMock(return_value={'success': True}),
                                       close=AsyncMock())
        self.lockdown = SimpleNamespace(
            udid='phone-a', get_developer_mode_status=AsyncMock(return_value=False),
            start_lockdown_service=AsyncMock(return_value=self.service))
        self.ipc = SimpleNamespace(emit=AsyncMock())
        self.session = bridge.TouchSession(self.ipc, 120, udid='phone-a')
        self.session._is_personalized_ddi_mounted = AsyncMock(return_value=True)
        self.session._mount_personalized_ddi = AsyncMock()

    async def assert_failure(self, code):
        expected = bridge.BridgeUserActionRequired if code == 'developer_mode_revealed' else bridge.BridgePrerequisiteError
        with self.assertRaises(expected) as raised:
            await self.session._preflight_developer_environment(self.lockdown)
        self.assertEqual(raised.exception.code, code)
        self.session._is_personalized_ddi_mounted.assert_not_awaited()
        self.session._mount_personalized_ddi.assert_not_awaited()

    async def test_usb_reveals_only_and_stops_before_ddi_or_input(self):
        await self.assert_failure('developer_mode_revealed')
        self.lockdown.start_lockdown_service.assert_awaited_once_with('com.apple.amfi.lockdown')
        self.service.send_recv_plist.assert_awaited_once_with({'action': 0})
        self.service.close.assert_awaited_once()
        self.assertFalse(any(call.args[0].get('event') == 'ready'
                             for call in self.ipc.emit.await_args_list))

    async def test_main_reports_user_action_without_error_or_termination(self):
        ipc = SimpleNamespace(emit=AsyncMock(), close=AsyncMock(),
                              wait_for_disconnect=AsyncMock(side_effect=asyncio.Event().wait))
        session = SimpleNamespace(
            connect=AsyncMock(side_effect=bridge.BridgeUserActionRequired(
                'developer_mode_revealed', 'Enable on the phone and retry.')),
            _cleanup=AsyncMock(), _mux_checkpoint=None,
            device_hub=SimpleNamespace(close=AsyncMock()))
        with patch.object(bridge, 'BridgeChannel', return_value=ipc), \
             patch.object(bridge, 'TouchSession', return_value=session):
            await bridge.main_async(120, 'phone-a', 'usb')
        ipc.emit.assert_awaited_once_with({'event': 'user_action_required',
            'code': 'developer_mode_revealed', 'message': 'Enable on the phone and retry.'})
        ipc.close.assert_awaited_once()
        session._cleanup.assert_awaited_once()
        session.device_hub.close.assert_awaited_once()

    async def test_already_enabled_continues_without_reveal(self):
        self.lockdown.get_developer_mode_status.return_value = True
        await self.session._preflight_developer_environment(self.lockdown)
        self.lockdown.start_lockdown_service.assert_not_awaited()
        self.session._is_personalized_ddi_mounted.assert_awaited_once()

    async def test_wireless_never_reveals(self):
        self.session.transport_mode = 'wireless'
        await self.assert_failure('developer_mode_required')
        self.lockdown.start_lockdown_service.assert_not_awaited()

    async def test_rejected_or_malformed_response_does_not_claim_success(self):
        for response in ({'success': False}, {'Error': 'Denied'}, {}, None, {'success': 'true'}):
            with self.subTest(response=response):
                self.service.send_recv_plist.return_value = response
                await self.assert_failure('developer_mode_reveal_failed')
        self.assertEqual(self.service.close.await_count, 5)

    async def test_unavailable_amfi_is_not_reported_as_missing_ddi(self):
        self.lockdown.start_lockdown_service.side_effect = bridge.InvalidServiceError('InvalidService', 'phone-a', '18.0')
        await self.assert_failure('developer_mode_reveal_failed')

    async def test_device_errors_keep_actionable_codes(self):
        for error, code in [(bridge.NotPairedError(), 'apple_device_not_trusted'),
                            (bridge.PasswordRequiredError(), 'apple_device_locked'),
                            (ConnectionError(), 'apple_connection_lost')]:
            with self.subTest(code=code):
                self.service.send_recv_plist.side_effect = error
                await self.assert_failure(code)
        self.assertEqual(self.service.close.await_count, 3)

    async def test_query_failure_does_not_attempt_reveal(self):
        self.lockdown.get_developer_mode_status.side_effect = RuntimeError('unknown status')
        await self.assert_failure('developer_mode_check_failed')
        self.lockdown.start_lockdown_service.assert_not_awaited()

    async def test_timeout_closes_service(self):
        async def wait(_):
            await asyncio.Event().wait()
        self.service.send_recv_plist.side_effect = wait
        with patch.object(bridge, 'PERSONALIZED_DDI_INVENTORY_TIMEOUT_SECONDS', 0.02):
            await self.assert_failure('developer_mode_reveal_timeout')
        self.service.close.assert_awaited_once()

    async def test_cancel_closes_service(self):
        entered = asyncio.Event()
        async def wait(_):
            entered.set()
            await asyncio.Event().wait()
        self.service.send_recv_plist.side_effect = wait
        task = asyncio.create_task(self.session._preflight_developer_environment(self.lockdown))
        await entered.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.service.close.assert_awaited_once()

    async def test_wrong_device_does_not_query_or_reveal(self):
        self.lockdown.udid = 'phone-b'
        with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
            await self.session._connect_with_lockdown(self.lockdown)
        self.assertEqual(raised.exception.code, 'device_identity_mismatch')
        self.lockdown.get_developer_mode_status.assert_not_awaited()
        self.lockdown.start_lockdown_service.assert_not_awaited()


if __name__ == '__main__':
    unittest.main()
