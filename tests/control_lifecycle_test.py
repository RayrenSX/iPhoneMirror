"""Regression coverage for process, pipe, tunnel and DDI failure boundaries."""
import asyncio
import io
import json
import struct
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge


def framed(value):
    payload = json.dumps(value).encode()
    return struct.pack('<I', len(payload)) + payload


class PipeLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_queued_input_does_not_hide_host_eof_during_startup_or_recovery(self):
        channel = bridge.BridgeChannel()
        channel._stdin = io.BytesIO(framed({'generation': 1}) * 3)
        await asyncio.wait_for(channel.wait_for_disconnect(), 1)
        self.assertEqual(len(channel._messages), 3)
        await channel.close()

    async def test_invalid_json_shape_is_a_protocol_failure(self):
        for value in [None, [], 'text', 123]:
            channel = bridge.BridgeChannel()
            channel._stdin = io.BytesIO(framed(value))
            with self.subTest(value=value), self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await asyncio.wait_for(channel.wait_for_disconnect(), 1)
            self.assertEqual(raised.exception.code, 'bad_frame')
            await channel.close()

    async def test_input_backlog_is_bounded_and_reports_failure(self):
        channel = bridge.BridgeChannel()
        with patch.object(channel, '_read_message', AsyncMock(return_value={'seq': 1})), \
             self.assertRaises(bridge.BridgePrerequisiteError) as raised:
            await asyncio.wait_for(channel.wait_for_disconnect(), 3)
        self.assertEqual(raised.exception.code, 'bridge_input_overflow')
        self.assertEqual(len(channel._messages), 1024)
        await channel.close()


class ProcessLifecycleTests(unittest.TestCase):
    def test_failed_startup_exits_while_parent_keeps_stdin_open(self):
        # A mocked asyncio read does not reproduce executor shutdown joining a
        # real blocked Windows pipe. Keep the parent's writer open until exit.
        script = '''
import asyncio
import usb_touch_bridge as b
async def fail(self):
    await asyncio.sleep(.1)
    raise b.BridgePrerequisiteError('developer_image_tss_rejected', 'test failure')
b.TouchSession.connect = fail
asyncio.run(b.main_async(120, 'test-device', 'usb'))
'''
        process = subprocess.Popen([sys.executable, '-c', script],
            cwd=Path(__file__).resolve().parents[1] / 'tools',
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            process.wait(timeout=12)
            output, errors = process.stdout.read(), process.stderr.read()
            self.assertEqual(process.returncode, 0, errors.decode(errors='replace'))
            self.assertIn(b'developer_image_tss_rejected', output)
            self.assertIn(b'terminated', output)
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            for stream in (process.stdin, process.stdout, process.stderr):
                stream.close()


class TunnelLifecycleTests(unittest.IsolatedAsyncioTestCase):
    def session(self, transport='wireless'):
        return bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120,
                                   udid='test-device', transport=transport)

    async def test_startup_is_bounded_but_ready_session_can_outlive_deadline(self):
        session = self.session()
        cancelled = asyncio.Event()
        async def stalled():
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        with patch.object(bridge, 'HID_RECOVERY_READY_TIMEOUT_SECONDS', .01):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await session._run_until_ready(stalled())
            self.assertEqual(raised.exception.code, 'control_service_start_timeout')
            self.assertTrue(cancelled.is_set())
            async def established():
                session._session_ready.set()
                await asyncio.sleep(.04)
            await session._run_until_ready(established())

    async def test_wireless_stalled_tunnel_is_cancelled_and_all_services_closed(self):
        session = self.session()
        services = [SimpleNamespace(close=AsyncMock()), SimpleNamespace(close=AsyncMock())]
        closed = asyncio.Event()
        class Tunnel:
            async def __aenter__(self):
                await asyncio.Event().wait()
            async def __aexit__(self, *_):
                closed.set()
        with patch.object(bridge, 'iter_remote_paired_identifiers', return_value=['test-device']), \
             patch.object(bridge, 'get_remote_pairing_tunnel_services', AsyncMock(return_value=services)), \
             patch.object(bridge, 'start_tunnel', return_value=Tunnel()), \
             patch.object(bridge, 'HID_RECOVERY_READY_TIMEOUT_SECONDS', .01):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await asyncio.wait_for(session._connect_via_remote_pairing(), 1)
        self.assertEqual(raised.exception.code, 'control_service_start_timeout')
        self.assertTrue(closed.is_set())
        for service in services:
            service.close.assert_awaited_once()

    async def test_wireless_sender_failure_is_not_silent_success(self):
        session = self.session()
        service = SimpleNamespace(close=AsyncMock())
        class Tunnel:
            async def __aenter__(self): return object()
            async def __aexit__(self, *_): pass
        async def observed(_):
            session._session_ready.set()
            session._hid_transport_failed = True
        with patch.object(bridge, 'iter_remote_paired_identifiers', return_value=['test-device']), \
             patch.object(bridge, 'get_remote_pairing_tunnel_services', AsyncMock(return_value=[service])), \
             patch.object(bridge, 'start_tunnel', return_value=Tunnel()), \
             patch.object(session, '_run_observed_tunnel', observed):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await session._connect_via_remote_pairing()
        self.assertEqual(raised.exception.code, 'apple_connection_lost')

    async def test_inventory_connection_loss_is_not_reported_as_mounted(self):
        class Mounter:
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
            async def is_image_mounted(self, *_): return True
            async def copy_devices(self): raise bridge.ConnectionTerminatedError()
        with patch.object(bridge, 'PersonalizedImageMounter', return_value=Mounter()):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await bridge.TouchSession._query_personalized_ddi_inventory(object())
        self.assertEqual(raised.exception.code, 'apple_connection_lost')

    async def test_wireless_recovery_never_reconnects_over_usb(self):
        session = self.session()
        async def failed_tunnel(_):
            session._session_ready.set()
            await session._mark_recovering(ConnectionError('wireless lost'))
        with patch.object(session, '_preflight_developer_environment', AsyncMock()), \
             patch.object(session, '_run_tunnel_attempt', failed_tunnel), \
             patch.object(session, '_reconnect_lockdown', AsyncMock()) as usb_reconnect:
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await session._connect_with_lockdown(SimpleNamespace(udid='test-device'))
        self.assertEqual(raised.exception.code, 'apple_connection_lost')
        usb_reconnect.assert_not_awaited()

    async def test_failed_sdk_teardown_still_closes_userspace_stack(self):
        session = self.session()
        tun = SimpleNamespace(close=AsyncMock())
        reader = asyncio.create_task(asyncio.Event().wait())
        result = SimpleNamespace(client=SimpleNamespace(tun=tun, _tun_read_task=reader))
        class Tunnel:
            async def __aenter__(self): return result
            async def __aexit__(self, *_): raise ConnectionError('reader already failed')
        with patch.object(bridge, 'start_tunnel', return_value=Tunnel()):
            with self.assertRaisesRegex(RuntimeError, 'original failure'):
                async with session._bounded_tunnel(object()):
                    raise RuntimeError('original failure')
        self.assertTrue(reader.cancelled())
        tun.close.assert_awaited_once()

    async def test_idle_wireless_hid_failure_stops_sender_and_requests_recovery(self):
        session = self.session()
        session.auth_mode = 'direct'
        session.gate_open = True
        session._session_ready.set()
        async def no_input():
            await asyncio.Event().wait()
            yield {}  # async generator, deliberately never yields input
        session.ipc.read_messages = no_input
        with patch.object(session, '_ping_hid', AsyncMock(side_effect=ConnectionError('HID closed'))), \
             patch.object(session, '_poll_device_pasteboard', AsyncMock()), \
             patch.object(session, '_refresh_direct_hid', AsyncMock()) as usb_rotation, \
             patch.object(bridge, 'HID_HEALTH_INTERVAL_SECONDS', .01):
            await asyncio.wait_for(session._run_serve(), 1)
        self.assertTrue(session._hid_transport_failed)
        self.assertFalse(session._session_ready.is_set())
        usb_rotation.assert_not_awaited()

    async def test_proxy_fallback_does_not_spend_old_tunnel_deadline_on_ddi(self):
        session = self.session('wireless')
        async def fallback():
            await asyncio.sleep(.04)
        with patch.object(bridge.CoreDeviceTunnelProxy, 'create', AsyncMock(
                side_effect=bridge.InvalidServiceError('InvalidService', 'test-device', '27.0'))), \
             patch.object(session, '_connect_via_remote_pairing', fallback), \
             patch.object(bridge, 'HID_RECOVERY_READY_TIMEOUT_SECONDS', .01):
            await session._run_tunnel_attempt(object())
