"""Read-only direction and sharing of a single capture-usbmux owner."""
import asyncio
import contextlib
import io
import os
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import screen_orientation as direction
import usb_touch_bridge as bridge

UDID = '00008120-001E29591A040032'


class DirectionTests(unittest.IsolatedAsyncioTestCase):
    async def test_direction_failure_retains_shared_broker_until_parent_disconnects(self):
        for failure in (ConnectionError('springboard stopped'), TimeoutError('direction timed out'), 0):
            with self.subTest(failure=repr(failure)):
                disconnected = asyncio.Event()
                ipc = SimpleNamespace(wait_for_disconnect=disconnected.wait, close=AsyncMock())
                session = SimpleNamespace(_start_capture_mux=AsyncMock(), _close_capture_mux=Mock())
                lockdown = SimpleNamespace(udid=UDID, close=AsyncMock())
                tried_service = asyncio.Event()
                class Springboard:
                    connect = AsyncMock()
                    close = AsyncMock()
                    async def get_interface_orientation(self):
                        tried_service.set()
                        if isinstance(failure, BaseException): raise failure
                        return failure
                fake_bridge = SimpleNamespace(BridgeChannel=lambda: ipc,
                    TouchSession=lambda *args, **kwargs: session,
                    create_using_usbmux=AsyncMock(return_value=lockdown), _udid_matches=bridge._udid_matches)
                output = io.StringIO()
                with patch.dict(os.environ, {'USBMUXD_SOCKET_ADDRESS': '127.0.0.1:31234'}), \
                     patch.object(direction, 'SpringBoardServicesService', lambda _: Springboard()), \
                     contextlib.redirect_stdout(output):
                    task = asyncio.create_task(direction.observe_orientation(fake_bridge, UDID))
                    try:
                        await asyncio.wait_for(tried_service.wait(), .5)
                        await asyncio.sleep(.03)
                        self.assertIn('IM_MUX 31234', output.getvalue())
                        self.assertIn('IM_ORIENTATION_UNAVAILABLE', output.getvalue())
                        self.assertFalse(task.done(), 'direction failure must not terminate the shared mux owner')
                        session._close_capture_mux.assert_not_called()
                        disconnected.set()
                        await asyncio.wait_for(task, .5)
                    finally:
                        task.cancel()
                        await asyncio.gather(task, return_exceptions=True)
                session._close_capture_mux.assert_called_once()
                lockdown.close.assert_awaited_once()
                Springboard.close.assert_awaited_once()

    async def test_valid_readings_resume_after_unknown_and_disconnected_service(self):
        disconnected = asyncio.Event()
        recovered = asyncio.Event()
        ipc = SimpleNamespace(wait_for_disconnect=disconnected.wait, close=AsyncMock())
        session = SimpleNamespace(_start_capture_mux=AsyncMock(), _close_capture_mux=Mock())
        lockdowns = [SimpleNamespace(udid=UDID, close=AsyncMock()) for _ in range(2)]
        services = [
            SimpleNamespace(connect=AsyncMock(), close=AsyncMock(),
                get_interface_orientation=AsyncMock(side_effect=[
                    3, ValueError('unknown orientation'), 0, 4, ConnectionError('service stopped')])),
            SimpleNamespace(connect=AsyncMock(), close=AsyncMock(),
                get_interface_orientation=AsyncMock(side_effect=lambda: recovered.set() or 3)),
        ]
        fake_bridge = SimpleNamespace(BridgeChannel=lambda: ipc,
            TouchSession=lambda *args, **kwargs: session,
            create_using_usbmux=AsyncMock(side_effect=lockdowns), _udid_matches=bridge._udid_matches)
        output = io.StringIO()
        with patch.dict(os.environ, {'USBMUXD_SOCKET_ADDRESS': '127.0.0.1:31234'}), \
             patch.object(direction, 'SpringBoardServicesService', side_effect=services), \
             contextlib.redirect_stdout(output):
            task = asyncio.create_task(direction.observe_orientation(fake_bridge, UDID))
            try:
                await asyncio.wait_for(recovered.wait(), 3)
                await asyncio.sleep(.03)
                session._close_capture_mux.assert_not_called()
                disconnected.set()
                await asyncio.wait_for(task, .5)
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
        self.assertEqual(output.getvalue().splitlines(), [
            'IM_MUX 31234', 'IM_ORIENTATION 3', 'IM_ORIENTATION_UNAVAILABLE',
            'IM_ORIENTATION 4', 'IM_ORIENTATION_UNAVAILABLE', 'IM_ORIENTATION 3',
        ])
        session._start_capture_mux.assert_awaited_once_with(attempts=1)
        session._close_capture_mux.assert_called_once()
        for lockdown, service in zip(lockdowns, services):
            lockdown.close.assert_awaited_once()
            service.close.assert_awaited_once()

    def test_identity_and_loopback_only(self):
        self.assertEqual(direction.capture_mux_environment_key(UDID),
                         direction.capture_mux_environment_key(UDID.replace('-', '').lower()))
        for value in ('192.168.1.1:1234', '127.0.0.1:0', '127.0.0.1:65536',
                      '127.0.0.1:1234/path', '127.0.0.1:１２３４', None):
            self.assertIsNone(direction.capture_mux_address(value))
        with self.assertRaises(ValueError):
            direction.capture_mux_environment_key('../other-device')

    async def test_borrowing_never_claims_a_second_usb_interface(self):
        events = SimpleNamespace(emit=AsyncMock())
        session = bridge.TouchSession(events, 120, udid=UDID)
        key = direction.capture_mux_environment_key(UDID)
        client = SimpleNamespace(udid=UDID, close=AsyncMock())
        with patch.dict(os.environ, {key: '127.0.0.1:31234', 'USBMUXD_SOCKET_ADDRESS': 'old'}), \
             patch.object(bridge, 'create_using_usbmux', AsyncMock(return_value=client)), \
             patch.object(bridge, '_get_usb_backend', Mock(side_effect=AssertionError('must not claim USB'))):
            await session._start_capture_mux(attempts=1)
            await session._start_capture_mux(attempts=1)
            self.assertEqual(os.environ['USBMUXD_SOCKET_ADDRESS'], '127.0.0.1:31234')
            client.close.assert_awaited_once()
            session._close_capture_mux()
            self.assertEqual(os.environ['USBMUXD_SOCKET_ADDRESS'], 'old')

    async def test_stale_or_wrong_device_broker_cannot_fall_through_to_claim(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, udid=UDID)
        key = direction.capture_mux_environment_key(UDID)
        client = SimpleNamespace(udid='another-device', close=AsyncMock())
        with patch.dict(os.environ, {key: '127.0.0.1:31234', 'USBMUXD_SOCKET_ADDRESS': 'old'}), \
             patch.object(bridge, 'create_using_usbmux', AsyncMock(return_value=client)), \
             patch.object(bridge, '_get_usb_backend', Mock(side_effect=AssertionError('must not claim USB'))):
            with self.assertRaises(ConnectionError):
                await session._start_capture_mux(attempts=1)
            self.assertEqual(os.environ['USBMUXD_SOCKET_ADDRESS'], 'old')
            client.close.assert_awaited_once()

    async def test_borrowed_broker_recovery_never_claims_owner_usb_interface(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120, udid=UDID)
        session._usb_mux_borrowed = True
        discovery_error = bridge.DeviceNotFoundError(UDID)
        with patch.object(bridge, '_get_usb_backend', Mock(side_effect=AssertionError('must not claim owner USB'))):
            with self.assertRaises(bridge.DeviceNotFoundError) as caught:
                await session._recover_lockdown_via_capture_mux(discovery_error)
        self.assertIs(caught.exception, discovery_error)

    async def test_stdin_close_releases_only_orientation_resources(self):
        done = asyncio.Event()
        ipc = SimpleNamespace(wait_for_disconnect=done.wait, close=AsyncMock())
        session = SimpleNamespace(_start_capture_mux=AsyncMock(), _close_capture_mux=Mock())
        lockdown = SimpleNamespace(udid=UDID, close=AsyncMock())
        class Springboard:
            connect = AsyncMock()
            close = AsyncMock()
            async def get_interface_orientation(self):
                done.set()
                return 4
        fake_bridge = SimpleNamespace(BridgeChannel=lambda: ipc,
            TouchSession=lambda *args, **kwargs: session,
            create_using_usbmux=AsyncMock(return_value=lockdown), _udid_matches=bridge._udid_matches)
        output = io.StringIO()
        with patch.dict(os.environ, {'USBMUXD_SOCKET_ADDRESS': '127.0.0.1:31234'}), \
             patch.object(direction, 'SpringBoardServicesService', lambda _: Springboard()), \
             contextlib.redirect_stdout(output):
            await direction.observe_orientation(fake_bridge, UDID)
        self.assertIn('IM_MUX 31234', output.getvalue())
        self.assertIn('IM_ORIENTATION 4', output.getvalue())
        lockdown.close.assert_awaited_once()
        session._close_capture_mux.assert_called_once()
        ipc.close.assert_awaited_once()
        fake_bridge.create_using_usbmux.assert_awaited_once_with(
            serial=UDID, connection_type='USB', autopair=False)


if __name__ == '__main__':
    unittest.main()
