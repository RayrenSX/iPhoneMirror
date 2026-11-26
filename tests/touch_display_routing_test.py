import asyncio
import sys
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

root = Path(__file__).resolve().parents[1]
source = root / 'tools' if (root / 'tools' / 'usb_touch_bridge.py').is_file() else root / 'src'
sys.path.insert(0, str(source))
import usb_touch_bridge as bridge


COVER = {'displayId': 1, 'uniqueId': 'cover', 'primary': True, 'currentMode': {'size': [1398, 2034]}}
INNER = {'displayId': bridge.XpcUInt64Type(2), 'uniqueId': 'inner',
         'currentMode': {'size': [bridge.XpcUInt64Type(1878), bridge.XpcUInt64Type(2670)]}}
SURFACES = {'connectedServices': [{'_ServiceID': 257, 'displayUUID': 'cover'},
                                  {'_ServiceID': {'uint': bridge.XpcUInt64Type(258)}, 'displayUUID': 'inner'},
                                  {'_ServiceID': 512}]}


class TouchDisplayRoutingTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        self.session.rsd = SimpleNamespace(_bridge_touch_display=bridge.TouchDisplay(2, 'inner', True))

    def test_active_panel_wins_and_old_ddi_uses_rotated_preview_aspect(self):
        external = {'displayId': 9, 'external': True, 'active': True}
        display = bridge._select_touch_display({'displays': [dict(COVER, active=True), INNER, external]},
                                               None, (1432, 2034))
        self.assertEqual((1, 'cover', True), (display.id, display.uuid, display.tracks_active_panel))
        display = bridge._select_touch_display({'displays': [COVER, INNER]}, None, (2034, 1432))
        self.assertEqual((2, 'inner', False), (display.id, display.uuid, display.tracks_active_panel))
        for size, target in (((1432, 2034), 2), ((1536, 1080), 2), ((1398, 2034), 1)):
            with self.subTest(size=size):
                display = bridge._select_touch_display({'displays': [COVER, INNER]}, None, size)
                self.assertEqual(target, display.id)
                self.assertTrue(display.matched_preview)
        ambiguous = bridge._select_touch_display({'displays': [COVER, INNER]}, None, (1414, 2034))
        self.assertEqual(1, ambiguous.id)
        self.assertFalse(ambiguous.matched_preview)
        display = bridge._select_touch_display({'displays': [dict(COVER, active=True), INNER]}, 2, None)
        self.assertEqual((2, 'inner', False), (display.id, display.uuid, display.tracks_active_panel))
        display = bridge._select_touch_display({'displays': [dict(COVER, backlightState='off'),
                                                              dict(INNER, backlightState='on')]}, None, None)
        self.assertEqual(2, display.id)
        self.assertEqual(1, bridge._select_touch_display({'displays': None}, None, None).id)

    async def test_touch_surface_is_connection_local_for_send_keepalive_and_cleanup(self):
        hid = SimpleNamespace(SERVICE_NAME='universalhid', list_connected_services=AsyncMock(return_value=SURFACES),
                              send_report=AsyncMock(), send_keyboard=AsyncMock())
        await self.session._verify_touch_surface(hid)
        hid.list_connected_services.assert_awaited_once()
        self.assertEqual(512, self.session.keyboard_service_id)
        self.session.hid = hid
        self.session._session_ready.set()
        self.session._ensure_keyboard_service = AsyncMock(return_value=False)
        self.session._ping_hid = AsyncMock()
        await self.session._probe_hid_sender()
        await self.session._send_touch_report(b'press', active_pointer_ids={1},
                                             contacts=[(0, bridge.TOUCHSCREEN_STATE_CONTACT, 100, 200)])
        self.session._touch_report_sent_at = 0
        await self.session._keep_touch_alive()
        await self.session._release_hid_state()
        self.assertGreater(hid.send_report.await_count, 7)
        self.assertEqual({258}, {call.args[0] for call in hid.send_report.await_args_list})

    async def test_multi_panel_does_not_fall_back_to_cover_on_unmatched_inventory(self):
        hid = SimpleNamespace(SERVICE_NAME='universalhid',
                              list_connected_services=AsyncMock(return_value={'connectedServices': [{'_ServiceID': 257}]}))
        with self.assertRaises(bridge.BridgePrerequisiteError):
            await self.session._verify_touch_surface(hid)
        self.session.rsd._bridge_touch_display = bridge.TouchDisplay()
        await self.session._verify_touch_surface(hid)
        self.assertEqual(257, bridge._hid_touch_surface(hid))

    async def test_inversion_is_held_until_the_gesture_ends(self):
        slots = bridge.FiveSlotStateMachine()
        self.session._send_touch_report = AsyncMock()

        async def apply(action):
            point = {'pointerId': 1, 'action': action, 'normalizedX': .25, 'normalizedY': .5}
            await self.session._apply_frame(slots, {}, [point])
            return self.session._send_touch_report.await_args.kwargs['contacts'][0][2]

        self.session._touch_orientation = 4
        self.assertEqual(49151, await apply('down'))
        self.session._touch_orientation = 3
        self.assertEqual(49151, await apply('move'))
        self.assertEqual(49151, await apply('up'))
        self.assertEqual(16384, await apply('down'))

    async def test_panel_change_retires_the_ready_session(self):
        self.session._session_ready.set()
        display = bridge.TouchDisplay(2, 'inner', True, True, 'inner')
        with patch.object(bridge, '_read_touch_displays', AsyncMock(return_value={
                'displays': [dict(COVER, active=True), INNER]})), patch.object(bridge.asyncio, 'sleep', AsyncMock()):
            await self.session._monitor_touch_panel(display)
        self.assertTrue(self.session._hid_transport_failed)
        self.assertFalse(self.session._session_ready.is_set())

    async def test_preview_updates_use_only_the_current_generation(self):
        self.session._generation = 2

        async def read_messages():
            for generation, width, height in ((2, 1432, 2034), (1, 1398, 2034)):
                yield {'schema': bridge.MESSAGE_SCHEMA, 'kind': 'touch_display_preview',
                       'generation': generation, 'width': width, 'height': height}

        self.session.ipc.read_messages = read_messages
        await self.session._serve()
        self.assertEqual((1432, 2034), self.session._preview_size)
        self.session._session_ready.set()
        cover = bridge.TouchDisplay(1, 'cover', True, False, 'cover')
        with patch.object(bridge, '_read_touch_displays', AsyncMock(return_value={
                'displays': [COVER, INNER]})), patch.object(bridge.asyncio, 'sleep', AsyncMock()):
            await self.session._monitor_touch_panel(cover)
        self.assertFalse(self.session._session_ready.is_set())

    async def test_both_authentication_paths_open_the_selected_display(self):
        self.session.rsd.service = SimpleNamespace(address=('fd00::2', 1234))
        self.session.device_hub.bind = AsyncMock()
        display = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
                                  connect=AsyncMock(), close=AsyncMock(),
                                  invoke=AsyncMock(), get_media_stream_server_status=AsyncMock(
                                      return_value={'running': False, 'sessions': []}),
                                  start_video_stream=AsyncMock(return_value={
                                      'connection': {'options': {
                                          'avcMediaStreamOptionClientSessionID': {'uuid': uuid.uuid4()}}}}))
        hid = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock())

        async def run(primary):
            received = asyncio.Event()

            async def recv():
                if not received.is_set():
                    received.set()
                    return b'first RTP packet'
                await asyncio.Future()

            transport = SimpleNamespace(port=40123, recv=recv, close=Mock())
            with patch.object(bridge, 'DisplayService', return_value=display), patch.object(
                    bridge, 'UniversalHIDServiceService', return_value=hid), patch.object(
                    bridge, '_open_touch_media_receiver', return_value=(transport, 'fd00::1')):
                if primary:
                    async with bridge.touch_session(self.session.rsd):
                        pass
                else:
                    try:
                        await self.session._open_gate()
                        self.assertEqual('mediastream', self.session.auth_mode)
                    finally:
                        await self.session._cleanup()
            transport.close.assert_called_once()
            self.assertEqual(2, display.start_video_stream.await_args.kwargs['display_id'])
            self.assertEqual('fd00::1', display.start_video_stream.await_args.kwargs['receiver_ip'])

        await run(primary=True)
        await run(primary=False)

    def test_userspace_address_never_selects_the_kernel_receiver(self):
        rsd = self.session.rsd
        rsd._bridge_tunnel_address = 'fd00::1'
        with patch('pmd_pytcp.socket.socket') as create, patch(
                'pymobiledevice3.remote.core_device.screen_stream.open_media_receiver') as upstream:
            create.return_value.getsockname.return_value = ('fd00::1', 40123)
            receiver, address = bridge._open_touch_media_receiver(rsd, Mock())
            self.assertEqual(('fd00::1', 40123), (address, receiver.port))
            create.return_value.bind.assert_called_once_with(('fd00::1', 0))
            receiver.close()
            create.return_value.close.assert_called_once()
            create.return_value.bind.side_effect = OSError('bind failed')
            with self.assertRaisesRegex(RuntimeError, 'startmediastream userspace receiver'):
                bridge._open_touch_media_receiver(rsd, Mock())
            self.assertEqual(2, create.return_value.close.call_count)
            upstream.assert_not_called()


if __name__ == '__main__':
    unittest.main()
