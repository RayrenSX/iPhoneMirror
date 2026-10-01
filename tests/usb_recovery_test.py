import asyncio
import io
import json
import os
import struct
import sys
import threading
from collections import deque
from hyperframe.frame import DataFrame, Frame, GoAwayFrame, PingFrame
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, Mock, patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge

class Ipc:
    def __init__(self): self.events = []
    async def emit(self, event): self.events.append(event)

class PingService:
    def __init__(self):
        self._request_lock=asyncio.Lock(); self._buffered_data_frames=deque(); self.writer=self; self.data=None
    def write(self,data): self.data=data
    async def drain(self): pass
    def _apply_flow_control_frame(self, frame): pass
    async def _receive_frame(self):
        f,n=Frame.parse_frame_header(memoryview(self.data[:9])); f.parse_body(memoryview(self.data[9:]));
        return PingFrame(0,flags=['ACK'],opaque_data=f.opaque_data)

class Hid:
    SERVICE_NAME = bridge.UniversalHIDServiceService.SERVICE_NAME
    def __init__(self, rsd=None): self.reports=[]; self.keys=[]; self.service=PingService(); self.inventory_reads=0
    async def __aenter__(self): return self
    async def __aexit__(self, *args): pass
    async def list_connected_services(self):
        self.inventory_reads+=1
        if self.inventory_reads>1: raise ConnectionResetError('inventory is one-shot')
        return {'services': [{'_ServiceID': 257}, {'_ServiceID': 512}]}
    async def send_report(self, service, report): self.reports.append((service, report))
    async def send_keyboard(self, service, usages, timestamp=None): self.keys.append((service, usages))
    async def create_keyboard_service(self, **kwargs): raise AssertionError('duplicate keyboard registration')

class RecoveryTests(unittest.IsolatedAsyncioTestCase):
    async def test_idle_tunnel_eof_recovers_without_hid_timeout(self):
        s = self.session()
        reader = asyncio.get_running_loop().create_future()
        result = type('Result', (), {'client': type('Client', (), {'_sock_read_task': reader})()})()
        ready = asyncio.Event(); cleaned = asyncio.Event()
        async def session(*args, **kwargs):
            await s._emit_ready(); ready.set()
            try: await asyncio.Event().wait()
            finally: cleaned.set()
        s._connect_with_tunnel_result = session
        owner = asyncio.create_task(s._run_observed_tunnel(result))
        await ready.wait(); reader.set_result(None)
        await asyncio.wait_for(owner, 0.3)
        self.assertTrue(cleaned.is_set())
        self.assertTrue(s._recovering)
        self.assertFalse(s._session_ready.is_set())
        self.assertFalse(asyncio.current_task().cancelling())
        self.assertEqual(1, sum(e.get('code') == 'transport_failure_detected' for e in s.ipc.events))

    async def test_transport_observer_shutdown_does_not_cancel_sdk_reader_or_recover(self):
        s = self.session(); started = asyncio.Event()
        reader = asyncio.get_running_loop().create_future()
        result = type('Result', (), {'client': type('Client', (), {'_sock_read_task': reader})()})()
        async def session(*args, **kwargs):
            started.set(); await asyncio.Event().wait()
        s._connect_with_tunnel_result = session
        owner = asyncio.create_task(s._run_observed_tunnel(result))
        await started.wait(); owner.cancel()
        with self.assertRaises(asyncio.CancelledError): await owner
        self.assertFalse(reader.done())
        self.assertFalse(s._recovering)
        reader.cancel()

    async def test_transport_loss_before_initial_ready_is_a_start_failure(self):
        s = self.session()
        reader = asyncio.get_running_loop().create_future(); reader.set_result(None)
        result = type('Result', (), {'client': type('Client', (), {'_sock_read_task': reader})()})()
        async def session(*args, **kwargs): await asyncio.Event().wait()
        s._connect_with_tunnel_result = session
        with self.assertRaises(ConnectionError): await s._run_observed_tunnel(result)
        self.assertFalse(s._recovering)
        self.assertFalse(s._session_ready.is_set())

    async def test_mux_failure_is_detected_while_sdk_reader_is_still_pending(self):
        s = self.session(); mux = Mock(failure_reason=None); s._usb_mux_transport = mux
        reader = asyncio.get_running_loop().create_future()
        client = type('Client', (), {'_sock_read_task': reader})()
        watcher = asyncio.create_task(s._wait_transport_failure(client))
        await asyncio.sleep(0); mux.failure_reason = 'USB reader stopped'
        self.assertIn('USB reader stopped', await asyncio.wait_for(watcher, 0.3))
        self.assertFalse(reader.done()); reader.cancel()

    async def test_known_dead_transport_skips_hid_repair(self):
        s = self.session(); s._session_ready.set()
        s._usb_mux_transport = Mock(failure_reason='USB reader stopped')
        s._refresh_direct_hid = AsyncMock()
        self.assertFalse(await s._repair_hid(TimeoutError(), s.hid))
        s._refresh_direct_hid.assert_not_awaited()
        self.assertTrue(s._recovering)
        self.assertFalse(s._session_ready.is_set())

    async def test_only_first_resumed_syn_is_short_later_attempts_keep_normal_budget(self):
        from iostouch.qt.usbmux_usb import MuxDevice, MuxConnection, ConnectionRefused
        mux = MuxDevice(lambda _: None); mux.ready.set(); mux._resume_probe_pending = True
        attempts = []
        def connect(conn, timeout):
            attempts.append((conn, timeout))
            if len(attempts) == 1: raise ConnectionRefused('first SYN unanswered')
            conn.state = 'connected'
        with patch.object(MuxConnection, 'wait_connected', connect):
            with self.assertRaises(ConnectionRefused): mux.connect(62078)
            good = mux.connect(62078)
            mux.connect(62078)
        self.assertEqual([0.75, 10.0, 10.0], [timeout for _, timeout in attempts])
        self.assertTrue(attempts[0][0].closed)
        self.assertNotIn(attempts[0][0].sport, mux._conns)
        self.assertNotEqual(attempts[0][0].sport, good.sport)

    async def test_retired_mux_server_closes_idle_clients_and_reader_thread(self):
        from iostouch.qt.usbmux_usb import MuxDevice
        from iostouch.qt.usbmuxd_server import UsbmuxdThread
        server = UsbmuxdThread(MuxDevice(lambda _: None), 'test-device')
        address = server.start()
        host, port = address.split(':')
        reader, writer = await asyncio.open_connection(host, int(port))
        try:
            await asyncio.sleep(0.05)
            await asyncio.to_thread(server.stop)
            self.assertFalse(server._thread.is_alive())
            self.assertEqual(b'', await asyncio.wait_for(reader.read(), 1))
        finally:
            writer.close()
            await writer.wait_closed()
            await asyncio.to_thread(server.stop)

    async def test_raw_mux_reports_reader_death_even_when_listener_is_open(self):
        from iostouch.qt.usbmux_usb import UsbMuxTransport, MuxDevice
        transport = UsbMuxTransport.__new__(UsbMuxTransport)
        transport.mux = MuxDevice(lambda _: None)
        transport._stop = threading.Event()
        transport._thread = Mock(is_alive=Mock(return_value=True))
        self.assertIsNone(transport.failure_reason)
        transport._thread.is_alive.return_value = False
        self.assertEqual('usbmux USB reader stopped', transport.failure_reason)
        transport.mux.abort_all('USB read failed')
        self.assertEqual('USB read failed', transport.failure_reason)

    async def test_reopened_usb_reader_preserves_protocol_without_second_version(self):
        from iostouch.qt.usbmux_usb import UsbMuxTransport, MuxDevice, PROTO_CONTROL
        previous = UsbMuxTransport.__new__(UsbMuxTransport)
        previous._thread = Mock(is_alive=Mock(return_value=False))
        previous.mux = MuxDevice(Mock())
        previous.mux.version = 2; previous.mux.tx_seq = 4321
        previous.mux._next_sport = 77; previous.mux.ready.set()
        previous.mux.abort_all('USB read failed')
        fresh = UsbMuxTransport.__new__(UsbMuxTransport)
        fresh._ep_out = Mock(wMaxPacketSize=512); fresh.bytes_out = 0
        fresh.resume_from(previous)
        self.assertIs(previous.mux, fresh.mux)
        self.assertEqual(4321, fresh.mux.tx_seq)
        self.assertEqual(77, fresh.mux._next_sport)
        self.assertIsNone(fresh.mux._failure_reason)
        fresh._ep_out.write.assert_not_called()
        with patch.object(threading, 'Thread'), \
                patch.object(fresh.mux, 'start') as version:
            fresh.start()
            version.assert_not_called()
        fresh.mux._send_packet(PROTO_CONTROL, b'probe')
        self.assertEqual(4322, fresh.mux.tx_seq)
        fresh._ep_out.write.assert_called_once()

    async def test_unverified_resumed_mux_is_not_kept_for_next_attempt(self):
        s = self.session()
        mux = Mock(failure_reason=None, resumed=True)
        s._usb_mux_transport = mux; s._usb_mux_server = Mock()
        s._create_lockdown_with_retry = AsyncMock(side_effect=ConnectionResetError('stale protocol'))
        with self.assertRaises(ConnectionResetError): await s._reconnect_lockdown()
        self.assertIsNone(s._usb_mux_transport)
        mux.close.assert_called_once()

    async def test_verified_resume_is_retained_and_exits_probe_state(self):
        s = self.session()
        mux = Mock(failure_reason=None, resumed=True)
        s._usb_mux_transport = mux; s._usb_mux_server = Mock()
        fresh = object()
        s._create_lockdown_with_retry = AsyncMock(return_value=fresh)
        self.assertIs(fresh, await s._reconnect_lockdown())
        self.assertFalse(mux.resumed)
        mux.close.assert_not_called()
        self.assertTrue(any(e.get('code') == 'capture_mux_resume_verified' for e in s.ipc.events))

    def session(self):
        s=bridge.TouchSession(Ipc(),120,'test-device')
        s._remote_pairing_provision_attempted=True
        s.hid=Hid(); s.auth_mode='direct'; s.gate_open=True
        s._preflight_developer_environment=AsyncMock()
        return s

    async def test_dead_capture_mux_is_retired_before_lockdown_retry(self):
        s = self.session()
        dead = Mock(failure_reason='USB reader exited')
        server = Mock()
        s._usb_mux_transport = dead; s._usb_mux_server = server
        s._usb_mux_previous_env = 'original:27015'
        s._start_capture_mux = AsyncMock()
        fresh = type('Lockdown', (), {'udid': 'test-device', 'close': AsyncMock()})()
        old = type('Lockdown', (), {'udid': 'test-device'})()
        async def discover(_):
            self.assertEqual('original:27015', os.environ['USBMUXD_SOCKET_ADDRESS'])
            self.assertIsNone(s._usb_mux_transport)
            dead.close.assert_called_once(); server.stop.assert_called_once()
            return fresh
        s._create_lockdown_with_retry = discover
        calls = []
        async def run(lockdown):
            calls.append(lockdown)
            await s._emit_ready()
            if len(calls) == 1: await s._mark_recovering(ConnectionResetError('USB failed'))
        s._run_tunnel_attempt = run
        with patch.dict(os.environ, {'USBMUXD_SOCKET_ADDRESS': 'dead:1234'}), \
                patch.object(bridge.asyncio, 'sleep', AsyncMock()):
            await s._connect_with_lockdown(old)
        self.assertEqual([old, fresh], calls)
        s._start_capture_mux.assert_awaited_once()

    async def test_retry_rediscovers_capture_mux_after_initial_replacement_fails(self):
        s = self.session(); s._recovering = True
        s._usb_mux_transport = Mock(failure_reason='reader failed')
        s._usb_mux_server = Mock()
        s._start_capture_mux = AsyncMock()
        s._create_lockdown_with_retry = AsyncMock(side_effect=bridge.DeviceNotFoundError('test-device'))
        fresh = object()
        s._recover_lockdown_via_capture_mux = AsyncMock(return_value=fresh)
        with patch.dict(os.environ, {'USBMUXD_SOCKET_ADDRESS': 'dead:1234'}):
            self.assertIs(fresh, await s._reconnect_lockdown())
            self.assertNotIn('USBMUXD_SOCKET_ADDRESS', os.environ)
            self.assertIs(fresh, await s._reconnect_lockdown())
        self.assertEqual(2, s._start_capture_mux.await_count)
        self.assertEqual(2, s._recover_lockdown_via_capture_mux.await_count)

    async def test_live_capture_mux_is_never_restarted_for_hid_failure(self):
        s = self.session(); mux = Mock(failure_reason=None)
        s._usb_mux_transport = mux; s._usb_mux_server = Mock()
        s._start_capture_mux = AsyncMock()
        s._create_lockdown_with_retry = AsyncMock(return_value=object())
        await s._reconnect_lockdown()
        mux.close.assert_not_called()
        s._start_capture_mux.assert_not_awaited()
        self.assertIs(mux, s._usb_mux_transport)

    async def test_refresh_keeps_advertised_keyboard_and_actual_next_send_works(self):
        s=self.session()
        with patch.object(bridge,'UniversalHIDServiceService',Hid):
            await s._refresh_direct_hid()
        await s._send_keyboard_report([])
        self.assertEqual([(512,[])],s.hid.keys)

    async def test_health_and_ready_do_not_repeat_one_shot_hid_inventory(self):
        s=self.session()
        await s._verify_touch_surface(s.hid)
        await s._emit_ready()
        for _ in range(4): await s._ping_hid()
        self.assertEqual(1,s.hid.inventory_reads)
        self.assertTrue(s._session_ready.is_set())

    async def test_ready_requires_a_successful_sender_and_response(self):
        s=self.session(); s.hid.send_report=AsyncMock(side_effect=ConnectionResetError('dead sender'))
        with self.assertRaises(ConnectionResetError): await s._emit_ready()
        self.assertFalse(any(e['event']=='ready' for e in s.ipc.events))

    async def test_ping_preserves_xpc_data_for_its_original_reader(self):
        s=self.session(); service=s.hid.service
        data=DataFrame(1, data=b'xpc response')
        receive_ack=service._receive_frame
        frames=[data]
        async def receive():
            return frames.pop(0) if frames else await receive_ack()
        service._receive_frame=receive
        await s._ping_hid()
        self.assertEqual([data],list(service._buffered_data_frames))

    async def test_ping_rejects_goaway_instead_of_claiming_ready(self):
        s=self.session()
        s.hid.service._receive_frame=AsyncMock(return_value=GoAwayFrame(0))
        with self.assertRaises(ConnectionError): await s._emit_ready()
        self.assertFalse(s._session_ready.is_set())
        self.assertFalse(any(e['event']=='ready' for e in s.ipc.events))

    async def test_fresh_lockdown_and_recovery_budget_resets_after_real_ready(self):
        s=self.session(); mux=object(); s._usb_mux_transport=mux
        old=type('Lockdown',(),{'udid':'test-device'})()
        fresh=type('Lockdown',(),{'udid':'test-device','close':AsyncMock()})()
        s._create_lockdown_with_retry=AsyncMock(return_value=fresh)
        calls=[]
        async def run(lockdown):
            calls.append(lockdown)
            self.assertIs(s._usb_mux_transport,mux)
            await s._emit_ready()
            if len(calls)<6: await s._mark_recovering(ConnectionResetError('fault'))
        s._run_tunnel_attempt=run
        with patch.object(bridge.asyncio,'sleep',AsyncMock()) as sleep:
            await s._connect_with_lockdown(old)
            sleep.assert_not_awaited()
        self.assertEqual([old]+[fresh]*5,calls)
        self.assertEqual(5,s._create_lockdown_with_retry.await_count)
        self.assertEqual(0,s._recovery_attempt)
        self.assertEqual(5,len([e for e in s.ipc.events if e.get('code')=='recovery_completed']))

    async def test_three_failed_reconnects_are_terminal_and_keep_mux(self):
        s=self.session(); mux=object(); s._usb_mux_transport=mux
        old=type('Lockdown',(),{'udid':'test-device'})()
        async def run(_):
            await s._emit_ready()
            await s._mark_recovering(ConnectionResetError('fault'))
        s._run_tunnel_attempt=run
        s._create_lockdown_with_retry=AsyncMock(side_effect=ConnectionResetError('lockdown dead'))
        with patch.object(bridge.asyncio,'sleep',AsyncMock()) as sleep:
            with self.assertRaises(bridge.BridgePrerequisiteError) as error: await s._connect_with_lockdown(old)
            self.assertEqual([1, 2], [call.args[0] for call in sleep.await_args_list])
        self.assertEqual('direct_hid_recovery_exhausted',error.exception.code)
        self.assertEqual(3,s._create_lockdown_with_retry.await_count)
        self.assertIs(mux,s._usb_mux_transport)

    async def test_sender_cancellation_does_not_cancel_its_supervisor(self):
        s=self.session()
        async def fail():
            await s._mark_recovering(ConnectionResetError('health fault'))
            raise asyncio.CancelledError()
        s._serve=fail
        await s._run_serve()
        self.assertFalse(asyncio.current_task().cancelling())
        self.assertTrue(s._hid_transport_failed)

    async def test_inflight_input_cannot_complete_background_health_recovery(self):
        s=self.session(); s._input_verified=True; s._session_ready.set()
        async def frames():
            yield {'kind':'touch_batch','seq':7}
        s.ipc.read_messages=frames
        async def send(*_):
            # Health fails while a previously queued send is completing.
            await s._mark_recovering(TimeoutError('health failed'))
        s._apply_frame=send
        with patch.object(bridge,'decode_touch_batch',return_value=(7,None,[])), \
             patch.object(s,'_request_direct_hid_rotation',AsyncMock()):
            await s._serve()
        self.assertTrue(s._recovering)
        self.assertFalse(s._session_ready.is_set())
        self.assertFalse(any(e.get('code')=='recovery_completed' for e in s.ipc.events))

    async def test_shutdown_cancellation_is_not_converted_to_recovery(self):
        s=self.session()
        async def wait(): await asyncio.Event().wait()
        s._serve=wait
        task=asyncio.create_task(s._run_serve())
        await asyncio.sleep(0)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertFalse(s._recovering)

    async def test_cancelled_pipe_consumer_cannot_steal_the_next_frame(self):
        payload=json.dumps({'seq':42}).encode()
        data=struct.pack('<I',len(payload))+payload
        started=threading.Event(); release=threading.Event()
        class Reader(io.BytesIO):
            def read(self,n):
                started.set(); release.wait(2)
                return super().read(n)
        channel=bridge.BridgeChannel(); channel._stdin=Reader(data)
        first=asyncio.create_task(anext(channel.read_messages()))
        try:
            await asyncio.to_thread(started.wait,1)
            first.cancel()
            with self.assertRaises(asyncio.CancelledError): await first
            second=asyncio.create_task(anext(channel.read_messages()))
            release.set()
            self.assertEqual({'seq':42},await asyncio.wait_for(second,2))
        finally: release.set()

    async def test_touch_release_is_retained_when_send_fails(self):
        s=self.session(); sm=bridge.FiveSlotStateMachine(); sm.assign(4)
        frame={'kind':'touch_batch'}; points=[{'pointerId':4,'action':'up','normalizedX':0,'normalizedY':0}]
        s._send_touch_report=AsyncMock(side_effect=ConnectionResetError())
        with self.assertRaises(ConnectionResetError): await s._apply_frame(sm,frame,points)
        self.assertEqual(0,sm.slot_for(4))
        s._send_touch_report=AsyncMock()
        await s._apply_frame(sm,frame,points)
        self.assertIsNone(sm.slot_for(4))

    async def test_refresh_cannot_report_success_when_new_hid_does_not_ack(self):
        s = self.session()
        class DeadHid(Hid):
            def __init__(self, rsd=None):
                super().__init__(rsd)
                self.service._receive_frame = AsyncMock(side_effect=ConnectionResetError('new socket closed'))
        with patch.object(bridge, 'UniversalHIDServiceService', DeadHid):
            with self.assertRaises(ConnectionResetError):
                await s._refresh_direct_hid()
        self.assertFalse(any(e.get('code') == 'direct_hid_refreshed' for e in s.ipc.events))

    async def test_old_hid_close_cannot_block_refresh_forever(self):
        s = self.session()
        async def stuck(*args): await asyncio.Event().wait()
        s.hid.__aexit__ = stuck
        with patch.object(bridge, 'UniversalHIDServiceService', Hid), \
             patch.object(bridge, 'HID_CLEANUP_TIMEOUT_SECONDS', 0.02):
            await asyncio.wait_for(s._refresh_direct_hid(), 0.2)
        self.assertTrue(any(e.get('code') == 'direct_hid_refreshed' for e in s.ipc.events))

    async def test_stuck_hid_cleanup_still_reaches_rsd_and_preserves_mux(self):
        s = self.session(); s._owns_hid = True
        async def stuck(*args): await asyncio.Event().wait()
        s.hid.__aexit__ = stuck
        rsd = type('Rsd', (), {'__aexit__': AsyncMock()})()
        s.rsd = rsd; mux = object(); s._usb_mux_transport = mux
        with patch.object(bridge, 'HID_CLEANUP_TIMEOUT_SECONDS', 0.02):
            await asyncio.wait_for(s._cleanup(preserve_capture_mux=True), 0.2)
        rsd.__aexit__.assert_awaited_once()
        self.assertIs(mux, s._usb_mux_transport)

    async def test_idle_wired_mediastream_session_has_a_health_monitor(self):
        s = self.session(); s.auth_mode = 'mediastream'
        observed = asyncio.Event()
        async def health():
            observed.set()
            await asyncio.Event().wait()
        async def frames():
            try: await asyncio.wait_for(observed.wait(), 0.05)
            except asyncio.TimeoutError: pass
            if False: yield {}
        s.ipc.read_messages = frames
        with patch.object(s, '_request_direct_hid_rotation', health):
            await s._serve()
        self.assertTrue(observed.is_set(), 'wired media-auth HID had no idle failure detector')

    async def test_cancelled_motion_write_cannot_silently_reuse_xpc_message_id(self):
        from pymobiledevice3.remote.remotexpc import RemoteXPCConnection, ROOT_CHANNEL
        s = self.session(); writes = []
        class StalledWriter:
            def write(self, data): writes.append(data)
            async def drain(self): await asyncio.Event().wait()
        connection = RemoteXPCConnection(('::1', 1))
        connection._writer = StalledWriter()
        async def report(*args): await connection.send_request({'report': b'input'})
        s.hid.send_report = report
        with patch.object(bridge, 'HID_TOUCH_MOTION_TIMEOUT_SECONDS', 0.01), \
             patch.object(bridge, 'HID_OPERATION_TIMEOUT_SECONDS', 0.03):
            with self.assertRaises(asyncio.TimeoutError):
                await s._send_touch_report(b'input', motion=True)
        # Real SDK writes a frame before drain, but advances ID only afterward.
        # A timeout must escape to recovery instead of sending the next frame
        # over this now-indeterminate connection with the same message ID.
        self.assertEqual(1, len(writes))
        self.assertEqual(0, connection.next_message_id[ROOT_CHANNEL])

    async def test_touch_release_timeout_does_not_retry_cancelled_request_on_same_hid(self):
        s = self.session()
        async def stuck(*args): await asyncio.Event().wait()
        s.hid.send_report = AsyncMock(side_effect=stuck)
        with patch.object(bridge, 'HID_OPERATION_TIMEOUT_SECONDS', 0.01):
            with self.assertRaises(asyncio.TimeoutError):
                await s._send_touch_report(b'release')
        self.assertEqual(1, s.hid.send_report.await_count)

    async def test_slow_motion_write_finishes_once_without_cancellation(self):
        s = self.session(); completed = []
        async def slow(*args):
            await asyncio.sleep(0.02)
            completed.append(True)
        s.hid.send_report = AsyncMock(side_effect=slow)
        with patch.object(bridge, 'HID_TOUCH_MOTION_TIMEOUT_SECONDS', 0.005), \
             patch.object(bridge, 'HID_OPERATION_TIMEOUT_SECONDS', 0.1):
            await s._send_touch_report(b'move', motion=True)
        self.assertEqual([True], completed)
        self.assertEqual(1, s.hid.send_report.await_count)

    async def test_simultaneous_sender_and_watchdog_failure_share_one_repair(self):
        s = self.session(); await s._emit_ready(); old = s.hid
        entered = asyncio.Event(); proceed = asyncio.Event()
        original_refresh = s._refresh_direct_hid
        async def slow_refresh():
            entered.set(); await proceed.wait()
            await original_refresh()
        with patch.object(bridge, 'UniversalHIDServiceService', Hid), \
             patch.object(s, '_refresh_direct_hid', side_effect=slow_refresh) as refresh:
            first = asyncio.create_task(s._repair_hid(ConnectionResetError('sender'), old))
            await entered.wait()
            second = asyncio.create_task(s._repair_hid(ConnectionResetError('watchdog'), old))
            await asyncio.sleep(0); proceed.set()
            self.assertEqual([True, True], await asyncio.gather(first, second))
        self.assertEqual(1, refresh.await_count)
        self.assertEqual(1, sum(e.get('code') == 'recovery_completed' for e in s.ipc.events))
        self.assertTrue(s._session_ready.is_set())

    async def test_failed_inline_repair_does_not_launch_a_second_competing_repair(self):
        s = self.session(); await s._emit_ready(); old = s.hid
        with patch.object(s, '_refresh_direct_hid', side_effect=ConnectionResetError('tunnel dead')) as refresh:
            result = await asyncio.gather(s._repair_hid(ConnectionResetError('sender'), old),
                                          s._repair_hid(ConnectionResetError('watchdog'), old))
        self.assertEqual([False, False], result)
        self.assertEqual(1, refresh.await_count)
        self.assertTrue(s._hid_transport_failed)
        self.assertFalse(s._session_ready.is_set())

    async def test_recovery_ready_releases_all_contacts_and_keyboard(self):
        s = self.session(); s.keyboard_service_id = 512
        await s._emit_ready()
        self.assertEqual(bridge.MAX_SLOTS, len(s.hid.reports))
        self.assertEqual([(512, [])], s.hid.keys)

    async def test_stalled_developer_preflight_consumes_bounded_recovery_attempts(self):
        s = self.session()
        old = type('Lockdown', (), {'udid': 'test-device'})()
        fresh = type('Lockdown', (), {'udid': 'test-device', 'close': AsyncMock()})()
        s._create_lockdown_with_retry = AsyncMock(return_value=fresh)
        async def preflight(lockdown):
            if lockdown is fresh: await asyncio.Event().wait()
        s._preflight_developer_environment = preflight
        async def run(_):
            await s._emit_ready()
            await s._mark_recovering(ConnectionResetError('transport'))
        s._run_tunnel_attempt = run
        with patch.object(bridge, 'HID_RECOVERY_READY_TIMEOUT_SECONDS', 0.01), \
             patch.object(bridge.asyncio, 'sleep', AsyncMock()):
            with self.assertRaises(bridge.BridgePrerequisiteError) as result:
                await asyncio.wait_for(s._connect_with_lockdown(old), 0.3)
        self.assertEqual('direct_hid_recovery_exhausted', result.exception.code)
        self.assertEqual(3, s._create_lockdown_with_retry.await_count)

    async def test_old_pipe_input_is_not_replayed_into_recovered_hid(self):
        s = self.session(); s._generation = 2; s._session_ready.set(); s._input_verified = True
        async def frames():
            yield {'kind': 'keyboard_batch', 'generation': 1, 'seq': 1}
            yield {'kind': 'keyboard_batch', 'generation': 2, 'seq': 2}
        s.ipc.read_messages = frames; s._apply_keyboard = AsyncMock()
        with patch.object(bridge, 'decode_keyboard_batch', return_value=(2, None, [4])), \
             patch.object(s, '_request_direct_hid_rotation', AsyncMock()):
            await s._serve()
        self.assertEqual(1, s._apply_keyboard.await_count)
        self.assertEqual(2, s._apply_keyboard.call_args.args[0]['generation'])

    async def test_failed_old_press_is_not_retried_after_new_generation_is_ready(self):
        s = self.session(); await s._emit_ready(); s._input_verified = True
        async def frames():
            yield {'kind': 'keyboard_batch', 'generation': 1, 'seq': 1}
        s.ipc.read_messages = frames
        s._apply_keyboard = AsyncMock(side_effect=[ConnectionResetError('dead old session'), None])
        with patch.object(bridge, 'decode_keyboard_batch', return_value=(1, None, [4])), \
             patch.object(bridge, 'UniversalHIDServiceService', Hid), \
             patch.object(s, '_request_direct_hid_rotation', AsyncMock()):
            await s._serve()
        self.assertEqual(2, s._generation)
        self.assertEqual(1, s._apply_keyboard.await_count)

if __name__=='__main__': unittest.main()
