"""Power-command routing and ownership checks; never contacts a real device."""
import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge
from pymobiledevice3.services.diagnostics import DiagnosticsService


class DeviceActionTests(unittest.IsolatedAsyncioTestCase):
    async def run_action(self, action='restart', *, paired=True, target=None, failure=None):
        udid = '00008101-0000000000000001'
        ipc = SimpleNamespace(emit=AsyncMock(), close=AsyncMock())
        session = SimpleNamespace(_start_capture_mux=AsyncMock(), _close_capture_mux=Mock())
        lockdown = SimpleNamespace(udid=target or udid, paired=paired, close=AsyncMock())
        diagnostics = SimpleNamespace(connect=AsyncMock(), sleep=AsyncMock(), restart=AsyncMock(),
                                      shutdown=AsyncMock(), close=AsyncMock())
        if failure:
            getattr(diagnostics, action).side_effect = failure
        factory = AsyncMock(return_value=lockdown)
        with patch.object(bridge, 'BridgeChannel', return_value=ipc), \
             patch.object(bridge, 'TouchSession', return_value=session), \
             patch.object(bridge, 'create_using_usbmux', factory), \
             patch('pymobiledevice3.services.diagnostics.DiagnosticsService', return_value=diagnostics) as service:
            result = await bridge.device_action_async(udid, action)
        factory.assert_awaited_once_with(serial=udid, connection_type='USB', autopair=False)
        session._start_capture_mux.assert_awaited_once_with(attempts=1)
        session._close_capture_mux.assert_called_once()
        lockdown.close.assert_awaited_once()
        ipc.close.assert_awaited_once()
        report = ipc.emit.await_args_list[-1].args[0]
        self.assertEqual((report['udid'], report['action'], report['success']), (udid, action, result))
        return result, diagnostics, service, report

    async def test_each_action_targets_only_requested_device(self):
        for action in ('restart', 'shutdown'):
            with self.subTest(action=action):
                result, diagnostics, service, _ = await self.run_action(action)
                self.assertTrue(result)
                for method in ('sleep', 'restart', 'shutdown'):
                    self.assertEqual(getattr(diagnostics, method).await_count, int(method == action))
                diagnostics.close.assert_awaited_once()

    async def test_identity_mismatch_and_missing_trust_never_send_power(self):
        for parameters in ({'target': 'different-device'}, {'paired': False}):
            result, diagnostics, service, _ = await self.run_action(**parameters)
            self.assertFalse(result)
            service.assert_not_called()
            for method in ('sleep', 'restart', 'shutdown'):
                getattr(diagnostics, method).assert_not_awaited()

    async def test_uncertain_failure_never_retries(self):
        result, diagnostics, _, report = await self.run_action('restart', failure=ConnectionError('device disconnected'))
        self.assertFalse(result)
        self.assertIn('disconnected', report['message'])
        diagnostics.restart.assert_awaited_once()
        diagnostics.close.assert_awaited_once()

    async def test_unknown_action_rejected_before_device_access(self):
        for action in ('sleep', 'erase'):
            with self.subTest(action=action), patch.object(bridge, 'BridgeChannel') as ipc:
                with self.assertRaises(ValueError):
                    await bridge.device_action_async('00008101', action)
                ipc.assert_not_called()


if __name__ == '__main__':
    unittest.main()
