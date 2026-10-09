import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


class SetupCheckTests(unittest.IsolatedAsyncioTestCase):
    async def test_checks_existing_configuration_without_pairing_or_writes(self):
        client = SimpleNamespace(udid='phone-a', get_developer_mode_status=AsyncMock(return_value=True),
                                 get_enable_wifi_connections=AsyncMock(return_value=True), close=AsyncMock())
        with patch.object(bridge, 'create_using_usbmux', AsyncMock(return_value=client)) as create:
            result = await bridge.check_setup_async('phone-a', True)
        self.assertEqual(result, {'state': 'Completed', 'udid': 'phone-a'})
        create.assert_awaited_once_with(serial='phone-a', connection_type='USB', autopair=False)
        client.close.assert_awaited_once()

    async def test_missing_developer_mode_and_wireless_setting_are_incomplete(self):
        for developer, wifi, code in [(False, True, 'developer_mode_required'), (True, False, 'wireless_pairing_required')]:
            client = SimpleNamespace(udid='phone-a', get_developer_mode_status=AsyncMock(return_value=developer),
                                     get_enable_wifi_connections=AsyncMock(return_value=wifi), close=AsyncMock())
            with patch.object(bridge, 'create_using_usbmux', AsyncMock(return_value=client)):
                result = await bridge.check_setup_async('phone-a', True)
            self.assertEqual(result, {'state': 'Incomplete', 'code': code})
            client.close.assert_awaited_once()

    async def test_wrong_device_and_revoked_trust_are_invalid(self):
        client = SimpleNamespace(udid='other-device', close=AsyncMock())
        with patch.object(bridge, 'create_using_usbmux', AsyncMock(return_value=client)):
            self.assertEqual((await bridge.check_setup_async('phone-a'))['state'], 'Invalid')
        with patch.object(bridge, 'create_using_usbmux', AsyncMock(side_effect=bridge.NotPairedError())):
            self.assertEqual((await bridge.check_setup_async('phone-a'))['code'], 'apple_device_not_trusted')

    async def test_query_cancellation_closes_device(self):
        entered = asyncio.Event()
        async def wait():
            entered.set()
            await asyncio.Event().wait()
        client = SimpleNamespace(udid='phone-a', get_developer_mode_status=wait, close=AsyncMock())
        with patch.object(bridge, 'create_using_usbmux', AsyncMock(return_value=client)):
            task = asyncio.create_task(bridge.check_setup_async('phone-a'))
            await entered.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        client.close.assert_awaited_once()


if __name__ == '__main__':
    unittest.main()
