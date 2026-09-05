"""
FiveSlotStateMachine 和 HID 报告构建器的单元测试。

不需要真机连接——纯逻辑测试。
"""

import struct
import time
import unittest
from unittest.mock import patch
import sys
import os
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'tools'))


class TestPackagedSourceParity(unittest.TestCase):
    def test_usb_control_copy_matches_application_bridge(self):
        root = Path(__file__).resolve().parents[1]
        self.assertEqual(
            (root / 'tools' / 'usb_touch_bridge.py').read_text(encoding='utf-8').splitlines(),
            (root.parent / 'iUsbBridge' / 'src' / 'usb_touch_bridge.py').read_text(encoding='utf-8').splitlines(),
        )


class TestFiveSlotStateMachine(unittest.TestCase):
    def setUp(self):
        from usb_touch_bridge import FiveSlotStateMachine
        self.SmClass = FiveSlotStateMachine

    def test_assign_slot_0_first(self):
        sm = self.SmClass()
        self.assertEqual(sm.assign(1), 0)

    def test_assign_sequential_slots(self):
        sm = self.SmClass()
        for i in range(5):
            self.assertEqual(sm.assign(i), i)

    def test_assign_exceeds_max_returns_none(self):
        sm = self.SmClass()
        for i in range(5):
            sm.assign(i)
        self.assertIsNone(sm.assign(5))

    def test_assign_same_id_returns_same_slot(self):
        sm = self.SmClass()
        slot1 = sm.assign(1)
        slot2 = sm.assign(1)
        self.assertEqual(slot1, slot2)

    def test_release_frees_slot(self):
        sm = self.SmClass()
        sm.assign(1)
        slot = sm.release(1)
        self.assertIsNotNone(slot)
        self.assertEqual(sm.assign(2), slot)

    def test_release_unknown_id_returns_none(self):
        sm = self.SmClass()
        self.assertIsNone(sm.release(99))

    def test_clear_releases_all(self):
        sm = self.SmClass()
        for i in range(3):
            sm.assign(i)
        released = sm.clear()
        self.assertEqual(len(released), 3)
        for i in range(5):
            self.assertIsNotNone(sm.assign(i))

    def test_slots_reused_in_order(self):
        sm = self.SmClass()
        sm.assign(1)
        sm.assign(2)
        sm.release(1)
        self.assertEqual(sm.assign(3), 0)

    def test_slot_for_reports_active_pointer(self):
        sm = self.SmClass()
        self.assertIsNone(sm.slot_for(10))
        self.assertEqual(sm.assign(10), 0)
        self.assertEqual(sm.slot_for(10), 0)


class TestBuildTouchscreenReport(unittest.TestCase):
    def setUp(self):
        from usb_touch_bridge import build_touchscreen_report
        from pymobiledevice3.remote.core_device.hid_service import (
            TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE,
        )
        self.build = build_touchscreen_report
        self.CONTACT = TOUCHSCREEN_STATE_CONTACT
        self.RELEASE = TOUCHSCREEN_STATE_RELEASE

    def test_report_length_58(self):
        report = self.build(0, self.CONTACT, 32767, 32767)
        self.assertEqual(len(report), 58)

    def test_report_id_is_0x09(self):
        report = self.build(0, self.CONTACT, 0, 0)
        self.assertEqual(report[0], 0x09)

    def test_contact_state_byte_slot0(self):
        report = self.build(0, self.CONTACT, 0, 0)
        self.assertEqual(report[3], 0xC2)

    def test_contact_state_byte_slot3(self):
        report = self.build(3, self.CONTACT, 0, 0)
        self.assertEqual(report[3], 0xC2 | 3)  # 0xC3

    def test_release_state_byte_slot0(self):
        report = self.build(0, self.RELEASE, 0, 0)
        self.assertEqual(report[3], 0x02)

    def test_release_state_byte_slot4(self):
        report = self.build(4, self.RELEASE, 0, 0)
        self.assertEqual(report[3], 0x06)

    def test_xy_little_endian(self):
        report = self.build(0, self.CONTACT, 0x1234, 0x5678)
        x_le = struct.unpack('<H', report[4:6])[0]
        y_le = struct.unpack('<H', report[6:8])[0]
        self.assertEqual(x_le, 0x1234)
        self.assertEqual(y_le, 0x5678)

    def test_xy_clamped_to_16bit(self):
        report = self.build(0, self.CONTACT, 70000, 70000)
        x_le = struct.unpack('<H', report[4:6])[0]
        self.assertEqual(x_le, 70000 & 0xFFFF)

    def test_timestamp_48bit_little_endian(self):
        ts = 0x010203040506
        report = self.build(0, self.CONTACT, 0, 0, timestamp=ts)
        ts_bytes = report[44:50]
        self.assertEqual(int.from_bytes(ts_bytes, 'little'), ts)

    def test_padding_bytes_zero(self):
        report = self.build(0, self.CONTACT, 0, 0)
        self.assertEqual(report[8:40], b'\x00' * 32)
        self.assertEqual(report[50:58], b'\x00' * 8)

    def test_fixed_bytes_at_40_42(self):
        report = self.build(0, self.CONTACT, 0, 0)
        self.assertEqual(report[40:44], b'\x02\x00\x00\x00')


class TestIpcFrameParsing(unittest.TestCase):
    """测试 IPC 帧格式（4字节LE长度 + JSON）的编解码。"""

    def test_encode_decode_roundtrip(self):
        import json
        frame = {"schema": "iphoneMirror.touch.v2", "kind": "touch_batch",
                 "seq": 42, "timestampNs": 12345,
                 "points": [{"pointerId": 0, "action": "down",
                             "normalizedX": 0.5, "normalizedY": 0.5}]}
        payload = json.dumps(frame).encode("utf-8")
        header = struct.pack("<I", len(payload))
        raw = header + payload

        length = struct.unpack("<I", raw[:4])[0]
        decoded = json.loads(raw[4:4+length].decode("utf-8"))
        self.assertEqual(decoded, frame)

    def test_multitouch_frame(self):
        import json
        points = [{"pointerId": i, "action": "down",
                   "normalizedX": 0.1*i, "normalizedY": 0.5} for i in range(5)]
        frame = {"schema": "iphoneMirror.touch.v2", "kind": "touch_batch",
                 "seq": 0, "timestampNs": 0, "points": points}
        payload = json.dumps(frame).encode("utf-8")
        header = struct.pack("<I", len(payload))
        length = struct.unpack("<I", header)[0]
        self.assertEqual(length, len(payload))


class TestTouchBatchValidation(unittest.TestCase):
    def setUp(self):
        from usb_touch_bridge import decode_touch_batch
        self.decode = decode_touch_batch

    def test_accepts_project_message(self):
        message = {
            'schema': 'iphoneMirror.touch.v2', 'kind': 'touch_batch',
            'seq': 7, 'timestampNs': 99,
            'points': [{'pointerId': 3, 'action': 'move',
                        'normalizedX': 0.25, 'normalizedY': 0.75}],
        }
        sequence, timestamp, points = self.decode(message)
        self.assertEqual((sequence, timestamp), (7, 99))
        self.assertEqual(points[0]['pointerId'], 3)

    def test_rejects_legacy_shape(self):
        with self.assertRaises(ValueError):
            self.decode({'type': 'frame', 'seq': 1, 'contacts': []})

    def test_rejects_duplicate_pointer_ids(self):
        base = {'schema': 'iphoneMirror.touch.v2', 'kind': 'touch_batch', 'seq': 1,
                'points': [{'pointerId': 1, 'action': 'down', 'normalizedX': 0.1, 'normalizedY': 0.1},
                           {'pointerId': 1, 'action': 'move', 'normalizedX': 0.2, 'normalizedY': 0.2}]}
        with self.assertRaises(ValueError):
            self.decode(base)

    def test_rejects_out_of_range_coordinate(self):
        message = {'schema': 'iphoneMirror.touch.v2', 'kind': 'touch_batch', 'seq': 1,
                   'points': [{'pointerId': 1, 'action': 'down', 'normalizedX': 1.1, 'normalizedY': 0.5}]}
        with self.assertRaises(ValueError):
            self.decode(message)


class TestBridgeErrorContract(unittest.TestCase):
    def test_maps_apple_transport_prerequisites_to_stable_codes(self):
        import usb_touch_bridge as bridge

        self.assertEqual(
            bridge.bridge_error_code(bridge.ConnectionFailedToUsbmuxdError()),
            'apple_usbmux_unavailable')
        self.assertEqual(
            bridge.bridge_error_code(bridge.NotPairedError()),
            'apple_device_not_trusted')
        self.assertEqual(
            bridge.bridge_error_code(bridge.DeviceNotFoundError('missing device')),
            'apple_device_not_found')
        self.assertEqual(
            bridge.bridge_error_code(bridge.BridgePrerequisiteError(
                'developer_image_required', 'not mounted')),
            'developer_image_required')
        self.assertEqual(
            bridge.bridge_error_code(bridge.BridgePrerequisiteError(
                'remote_control_unsupported_ios', '9021')),
            'remote_control_unsupported_ios')


class TestCoordinateMapping(unittest.TestCase):
    """测试归一化坐标 → 16位像素坐标的映射。"""

    def test_zero_maps_to_zero(self):
        self.assertEqual(int(round(0.0 * 65535)), 0)

    def test_one_maps_to_max(self):
        self.assertEqual(int(round(1.0 * 65535)), 65535)

    def test_half_maps_to_center(self):
        self.assertEqual(int(round(0.5 * 65535)), 32768)

    def test_clamping(self):
        self.assertEqual(int(round(max(0.0, min(1.0, -0.5)) * 65535)), 0)
        self.assertEqual(int(round(max(0.0, min(1.0, 1.5)) * 65535)), 65535)


class TestPersonalizedDdiMirrorDownloads(unittest.TestCase):
    def setUp(self):
        import usb_touch_bridge as bridge
        self.bridge = bridge

    def test_uses_only_official_github_endpoints(self):
        sources = self.bridge._personalized_ddi_download_sources()
        self.assertEqual(len(self.bridge.PERSONALIZED_DDI_MIRROR_PREFIXES), 115)
        self.assertEqual(len(sources), 2)
        self.assertEqual([source.name for source in sources],
                         ['github-raw', 'github-api'])

    def test_rank_does_not_probe_or_reorder_github_endpoints(self):
        sources = self.bridge.rank_personalized_ddi_download_sources()
        self.assertEqual([source.kind for source in sources], ['raw', 'api'])

    def test_github_token_is_never_sent_to_a_public_mirror(self):
        mirror = self.bridge.PersonalizedDdiDownloadSource(
            'mirror', 'mirror', 'https://mirror.invalid/')
        api = self.bridge.PersonalizedDdiDownloadSource('api', 'api')
        with patch.dict(os.environ, {'IPHONE_MIRROR_GITHUB_TOKEN': 'test-token'}):
            self.assertNotIn('Authorization', self.bridge._ddi_source_headers(mirror))
            self.assertEqual(self.bridge._ddi_source_headers(api)['Authorization'],
                             'Bearer test-token')

    def test_download_rejects_a_payload_with_the_wrong_sha256(self):
        asset = next(asset for asset in self.bridge.PERSONALIZED_DDI_ASSETS
                     if asset.local_name == 'Image.trustcache')
        source = self.bridge.PersonalizedDdiDownloadSource('test', 'api')

        class Response:
            status_code = 200
            headers = {'Content-Length': str(asset.size)}

            def __init__(self, url):
                self.url = url

            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def iter_content(self, chunk_size):
                yield b'x' * asset.size

        with TemporaryDirectory() as directory, \
             patch.object(self.bridge.requests, 'get',
                          side_effect=lambda url, **_kwargs: Response(url)):
            with self.assertRaises(self.bridge.BridgePrerequisiteError) as raised:
                self.bridge._download_personalized_ddi_asset(
                    source, asset, Path(directory) / 'asset.download')

        self.assertEqual(raised.exception.code,
                         'developer_image_download_integrity_failed')


class TestDeveloperEnvironmentPreflight(unittest.IsolatedAsyncioTestCase):
    class Ipc:
        def __init__(self):
            self.events = []

        async def emit(self, event):
            self.events.append(event)

    async def test_requires_developer_mode_before_opening_image_mounter(self):
        import usb_touch_bridge as bridge

        class Lockdown:
            async def get_developer_mode_status(self):
                return False

        ipc = self.Ipc()
        session = bridge.TouchSession(ipc, 120)
        with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
            await session._preflight_developer_environment(Lockdown())

        self.assertEqual(raised.exception.code, 'developer_mode_required')
        self.assertEqual([event['code'] for event in ipc.events], [
            'checking_developer_environment'])

    async def test_missing_personalized_image_is_automatically_mounted_and_rechecked(self):
        import usb_touch_bridge as bridge

        class Lockdown:
            async def get_developer_mode_status(self):
                return True

        state = {'mount_calls': 0, 'closed': False, 'mounted': False,
                 'mount_paths': None}

        class Mounter:
            IMAGE_TYPE = 'Personalized'

            def __init__(self, lockdown):
                self.lockdown = lockdown

            async def __aenter__(self):
                return self

            async def __aexit__(self, *_args):
                state['closed'] = True

            async def is_image_mounted(self, image_type):
                self.image_type = image_type
                return state['mounted']

            async def mount(self, image, build_manifest, trustcache):
                state['mount_calls'] += 1
                state['mount_paths'] = (image, build_manifest, trustcache)
                state['mounted'] = True

        mount_timeouts = []

        async def record_wait_for(awaitable, timeout):
            mount_timeouts.append(timeout)
            return await awaitable

        def fetch_bundle(download_started):
            download_started()
            return (Path('Image.dmg'), Path('BuildManifest.plist'),
                    Path('Image.trustcache'))

        ipc = self.Ipc()
        session = bridge.TouchSession(ipc, 120)
        with patch.object(bridge, 'PersonalizedImageMounter', Mounter), \
             patch.object(bridge, 'fetch_automatic_personalized_ddi_bundle',
                          side_effect=fetch_bundle), \
             patch.object(bridge.asyncio, 'wait_for', record_wait_for):
            await session._preflight_developer_environment(Lockdown())

        self.assertEqual(state['mount_calls'], 1)
        self.assertEqual(mount_timeouts, [
            bridge.PERSONALIZED_DDI_DOWNLOAD_TIMEOUT_SECONDS,
            bridge.PERSONALIZED_DDI_MOUNT_TIMEOUT_SECONDS])
        self.assertTrue(state['closed'])
        self.assertEqual(
            tuple(path.name for path in state['mount_paths']),
            bridge.PERSONALIZED_DDI_FILES)
        self.assertEqual([event['code'] for event in ipc.events], [
            'checking_developer_environment', 'mounting_developer_image',
            'testing_developer_image_sources', 'downloading_developer_image'])

    async def test_automatic_personalized_image_failure_has_stable_error(self):
        import usb_touch_bridge as bridge

        class Lockdown:
            async def get_developer_mode_status(self):
                return True

        class Mounter:
            IMAGE_TYPE = 'Personalized'

            def __init__(self, lockdown):
                pass

            async def __aenter__(self):
                return self

            async def __aexit__(self, *_args):
                pass

            async def is_image_mounted(self, _image_type):
                return False

        session = bridge.TouchSession(self.Ipc(), 120)
        with patch.object(bridge, 'PersonalizedImageMounter', Mounter), \
             patch.object(bridge, 'fetch_automatic_personalized_ddi_bundle',
                          side_effect=bridge.BridgePrerequisiteError(
                              'developer_image_download_failed',
                              'All mirror downloads failed')):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await session._preflight_developer_environment(Lockdown())

        self.assertEqual(raised.exception.code, 'developer_image_download_failed')

    async def test_developer_mode_query_failure_has_stable_error(self):
        import usb_touch_bridge as bridge

        class Lockdown:
            async def get_developer_mode_status(self):
                raise RuntimeError('lockdown response was malformed')

        session = bridge.TouchSession(self.Ipc(), 120)
        with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
            await session._preflight_developer_environment(Lockdown())

        self.assertEqual(raised.exception.code, 'developer_mode_check_failed')

    async def test_explicit_local_ddi_is_mounted_and_rechecked(self):
        import usb_touch_bridge as bridge

        class Lockdown:
            async def get_developer_mode_status(self):
                return True

        state = {'mounted': False, 'mount_paths': None, 'checks': 0}

        class Mounter:
            IMAGE_TYPE = 'Personalized'

            def __init__(self, lockdown):
                pass

            async def __aenter__(self):
                return self

            async def __aexit__(self, *_args):
                pass

            async def is_image_mounted(self, _image_type):
                state['checks'] += 1
                return state['mounted']

            async def mount(self, image, build_manifest, trustcache):
                state['mount_paths'] = (image, build_manifest, trustcache)
                state['mounted'] = True

        mount_timeouts = []

        async def record_wait_for(awaitable, timeout):
            mount_timeouts.append(timeout)
            return await awaitable

        with TemporaryDirectory() as directory:
            ddi_dir = Path(directory)
            for name in bridge.PERSONALIZED_DDI_FILES:
                (ddi_dir / name).write_bytes(b'test-ddi')

            ipc = self.Ipc()
            session = bridge.TouchSession(ipc, 120, ddi_dir=ddi_dir)
            with patch.object(bridge, 'PersonalizedImageMounter', Mounter), \
                 patch.object(bridge.asyncio, 'wait_for', record_wait_for):
                await session._preflight_developer_environment(Lockdown())

        self.assertEqual(state['checks'], 2)
        self.assertEqual(mount_timeouts, [
            bridge.PERSONALIZED_DDI_MOUNT_TIMEOUT_SECONDS])
        self.assertEqual(
            tuple(path.name for path in state['mount_paths']),
            bridge.PERSONALIZED_DDI_FILES)
        self.assertIn('mounting_developer_image', [event['code'] for event in ipc.events])

    async def test_refreshing_a_stale_mounted_ddi_unmounts_then_remounts(self):
        import usb_touch_bridge as bridge

        class Lockdown:
            async def get_developer_mode_status(self):
                return True

        state = {'mounted': True, 'unmount_calls': 0, 'mount_calls': 0}

        class Mounter:
            IMAGE_TYPE = 'Personalized'

            def __init__(self, lockdown):
                pass

            async def __aenter__(self):
                return self

            async def __aexit__(self, *_args):
                pass

            async def is_image_mounted(self, _image_type):
                return state['mounted']

            async def umount(self):
                state['unmount_calls'] += 1
                state['mounted'] = False

            async def mount(self, _image, _build_manifest, _trustcache):
                state['mount_calls'] += 1
                state['mounted'] = True

        timeouts = []

        async def record_wait_for(awaitable, timeout):
            timeouts.append(timeout)
            return await awaitable

        ipc = self.Ipc()
        session = bridge.TouchSession(ipc, 120)
        with patch.object(bridge, 'PersonalizedImageMounter', Mounter), \
             patch.object(bridge, 'fetch_automatic_personalized_ddi_bundle',
                          return_value=(Path('Image.dmg'),
                                        Path('BuildManifest.plist'),
                                        Path('Image.trustcache'))), \
             patch.object(bridge.asyncio, 'wait_for', record_wait_for):
            await session._refresh_personalized_ddi(Lockdown())

        self.assertEqual(state['unmount_calls'], 1)
        self.assertEqual(state['mount_calls'], 1)
        self.assertEqual(timeouts, [
            bridge.PERSONALIZED_DDI_REMOUNT_TIMEOUT_SECONDS,
            bridge.PERSONALIZED_DDI_DOWNLOAD_TIMEOUT_SECONDS,
            bridge.PERSONALIZED_DDI_MOUNT_TIMEOUT_SECONDS])
        self.assertIn('remounting_developer_image', [event['code'] for event in ipc.events])

    async def test_missing_touch_surface_refreshes_a_preexisting_ddi_once(self):
        import usb_touch_bridge as bridge

        class Lockdown:
            def __init__(self):
                self.closed = False

            async def close(self):
                self.closed = True

        ipc = self.Ipc()
        lockdown = Lockdown()
        session = bridge.TouchSession(ipc, 120, udid='trusted-device')
        session._ddi_was_mounted = True
        connect_attempts = []
        refresh_attempts = []

        async def create_lockdown(_connection_type):
            return lockdown

        async def connect_with_lockdown(_lockdown):
            connect_attempts.append(True)
            if len(connect_attempts) == 1:
                raise bridge.BridgePrerequisiteError(
                    'touch_surface_unavailable', 'missing surface 257')

        async def refresh_ddi(_lockdown):
            refresh_attempts.append(True)

        with patch.object(session, '_create_lockdown_with_retry', create_lockdown), \
             patch.object(session, '_connect_with_lockdown', connect_with_lockdown), \
             patch.object(session, '_refresh_personalized_ddi', refresh_ddi):
            await session.connect()

        self.assertEqual(len(connect_attempts), 2)
        self.assertEqual(len(refresh_attempts), 1)
        self.assertTrue(lockdown.closed)

    async def test_mounted_ddi_with_empty_service_inventory_is_retried_after_refresh(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        class Lockdown:
            async def close(self):
                pass

        ipc = Ipc()
        session = bridge.TouchSession(ipc, 120, udid='trusted-device')
        session._ddi_was_mounted = True
        connects = 0
        refreshes = 0

        async def create_lockdown(_connection_type):
            return Lockdown()

        async def connect_with_lockdown(_lockdown):
            nonlocal connects
            connects += 1
            if connects < 3:
                raise bridge.BridgePrerequisiteError(
                    'touch_surface_unavailable', 'RSD services=[]')

        async def refresh(_lockdown):
            nonlocal refreshes
            refreshes += 1

        with patch.object(session, '_create_lockdown_with_retry', create_lockdown), \
             patch.object(session, '_connect_with_lockdown', connect_with_lockdown), \
             patch.object(session, '_refresh_personalized_ddi', refresh), \
             patch.object(bridge.asyncio, 'sleep', return_value=None):
            await session.connect()

        self.assertEqual(connects, 3)
        self.assertEqual(refreshes, 1)
        self.assertIn('waiting_for_hid_service', [event['code'] for event in ipc.events])

    def test_local_ddi_bundle_rejects_missing_or_empty_required_files(self):
        import usb_touch_bridge as bridge

        with TemporaryDirectory() as directory:
            ddi_dir = Path(directory)
            (ddi_dir / 'Image.dmg').write_bytes(b'present')
            (ddi_dir / 'BuildManifest.plist').write_bytes(b'')
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                bridge.local_personalized_ddi_bundle(ddi_dir)

        self.assertEqual(raised.exception.code, 'developer_image_bundle_invalid')
        self.assertIn('BuildManifest.plist', str(raised.exception))
        self.assertIn('Image.trustcache', str(raised.exception))

    async def test_preflight_runs_before_coredevice_tunnel_setup(self):
        import usb_touch_bridge as bridge

        class Lockdown:
            udid = 'trusted-device'

        ipc = self.Ipc()
        session = bridge.TouchSession(ipc, 120)

        async def fail_preflight(_lockdown):
            raise bridge.BridgePrerequisiteError(
                'developer_image_required', 'not mounted')

        async def create_tunnel(_lockdown):
            raise AssertionError('CoreDevice tunnel must not start before preflight')

        with patch.object(session, '_preflight_developer_environment', fail_preflight), \
             patch.object(bridge.CoreDeviceTunnelProxy, 'create', create_tunnel):
            with self.assertRaises(bridge.BridgePrerequisiteError):
                await session._connect_with_lockdown(Lockdown())

    async def test_ready_is_rejected_when_authentication_gate_is_closed(self):
        import usb_touch_bridge as bridge

        ipc = self.Ipc()
        session = bridge.TouchSession(ipc, 120)
        with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
            await session._emit_ready()

        self.assertEqual(raised.exception.code, 'remote_control_gate_closed')
        self.assertEqual(ipc.events, [])

    async def test_9021_from_display_service_is_reported_for_the_direct_hid_fallback(self):
        import usb_touch_bridge as bridge
        from pymobiledevice3.remote.core_device import screen_stream

        class Rsd:
            class Service:
                address = ('127.0.0.1', 12345)

            service = Service()

        class Display:
            def __init__(self, _rsd):
                pass

            async def __aenter__(self):
                return self

            async def start_video_stream(self, **_kwargs):
                raise RuntimeError('startmediastream returned 9021')

        class Transport:
            port = 5555

        ipc = self.Ipc()
        session = bridge.TouchSession(ipc, 120)
        session.rsd = Rsd()
        with patch.object(bridge, 'DisplayService', Display), \
             patch.object(screen_stream, 'open_media_receiver',
                          return_value=(Transport(), '127.0.0.1')):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await session._open_gate()

        self.assertEqual(raised.exception.code, 'remote_control_unsupported_ios')
        self.assertFalse(any(event['event'] == 'ready' for event in ipc.events))

    async def test_9021_uses_verified_direct_hid_before_ready(self):
        import usb_touch_bridge as bridge

        ipc = self.Ipc()
        session = bridge.TouchSession(ipc, 120, udid='trusted-device')
        attempts = []

        async def initialize_touch(*, open_media_gate=True):
            attempts.append(open_media_gate)

        with patch.object(session, '_initialize_touch_with_retry', initialize_touch):
            await session._enable_direct_hid_fallback(
                RuntimeError('startmediastream returned 9021'))
            await session._emit_ready()

        self.assertEqual(attempts, [False])
        self.assertEqual(session.auth_mode, 'direct')
        self.assertTrue(session.gate_open)
        self.assertEqual(ipc.events[-1]['event'], 'ready')
        self.assertEqual(ipc.events[-1]['authMode'], 'direct')
        self.assertTrue(ipc.events[-1]['gateOpen'])


class TestOptionalDisplayService(unittest.IsolatedAsyncioTestCase):
    async def test_hid_initialization_falls_back_to_legacy_service(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        class Rsd:
            peer_info = {'Services': {}}

        class MissingModern:
            SERVICE_NAME = 'com.apple.coredevice.hid.universalhidservice'

            def __init__(self, _rsd):
                pass

            async def __aenter__(self):
                raise RuntimeError(f'No such service: {self.SERVICE_NAME}')

            async def __aexit__(self, *_args):
                pass

        class AvailableLegacy:
            SERVICE_NAME = bridge.LEGACY_UNIVERSAL_HID_SERVICE

            def __init__(self, _rsd):
                pass

            async def __aenter__(self):
                return self

            async def __aexit__(self, *_args):
                pass

            async def list_connected_services(self):
                return {'connectedServices': [{'_ServiceID': 257}]}

        ipc = Ipc()
        session = bridge.TouchSession(ipc, 120)
        session.rsd = Rsd()
        with patch.object(bridge, 'UniversalHIDServiceService', MissingModern), \
             patch.object(bridge, 'LegacyUniversalHIDServiceService', AvailableLegacy):
            await session._init_touch()

        self.assertIsInstance(session.hid, AvailableLegacy)
        self.assertEqual(ipc.events[-1]['code'], 'hid_service_selected')
        self.assertEqual(ipc.events[-1]['message'], bridge.LEGACY_UNIVERSAL_HID_SERVICE)

    async def test_empty_rsd_service_inventory_becomes_touch_surface_error(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        class Rsd:
            peer_info = {'Services': {}}
            product_type = 'iPhone18,1'
            product_version = '26.6.1'

        class MissingHid:
            SERVICE_NAME = 'com.apple.coredevice.hid.universalhidservice'

            def __init__(self, _rsd):
                pass

            async def __aenter__(self):
                raise RuntimeError(f'No such service: {self.SERVICE_NAME}')

            async def __aexit__(self, *_args):
                pass

        ipc = Ipc()
        session = bridge.TouchSession(ipc, 120)
        session.rsd = Rsd()
        with patch.object(bridge, 'UniversalHIDServiceService', MissingHid), \
             patch.object(bridge, 'LegacyUniversalHIDServiceService', MissingHid), \
             patch.object(bridge.asyncio, 'sleep', return_value=None):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await session._initialize_touch_with_retry(open_media_gate=False)

        self.assertEqual(raised.exception.code, 'touch_surface_unavailable')
        self.assertIn('advertised HID services: none', str(raised.exception))
        self.assertTrue(any(event['code'] == 'hid_service_inventory' for event in ipc.events))

    async def test_wireless_missing_network_usbmux_uses_remote_pairing(self):
        import usb_touch_bridge as bridge

        class Ipc:
            async def emit(self, _event):
                pass

        session = bridge.TouchSession(Ipc(), 120, udid='trusted-device', transport='wireless')
        remote_attempts = []

        async def no_network_lockdown(_connection_type):
            raise bridge.DeviceNotFoundError('network device missing')

        async def connect_remote_pairing():
            remote_attempts.append(True)

        with patch.object(session, '_create_lockdown_with_retry', no_network_lockdown), \
             patch.object(session, '_connect_wireless_via_remote_pairing', connect_remote_pairing):
            await session.connect()

        self.assertEqual(remote_attempts, [True])

    async def test_wireless_without_remote_pairing_record_has_stable_error(self):
        import usb_touch_bridge as bridge

        class Ipc:
            async def emit(self, _event):
                pass

        session = bridge.TouchSession(Ipc(), 120, udid='trusted-device', transport='wireless')
        with patch.object(bridge, 'iter_remote_paired_identifiers', return_value=iter(())):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await session._connect_wireless_via_remote_pairing()

        self.assertEqual(raised.exception.code, 'wireless_remote_pairing_required')

    async def test_wireless_remote_pairing_discovery_uses_stored_udid_and_full_bonjour_window(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        captured = {}

        async def discover(*, bonjour_timeout, udid):
            captured['bonjour_timeout'] = bonjour_timeout
            captured['udid'] = udid
            return []

        ipc = Ipc()
        session = bridge.TouchSession(ipc, 120, udid='00008150-ABCDEF', transport='wireless')
        with patch.object(bridge, 'iter_remote_paired_identifiers',
                          return_value=iter(('00008150-abcdef',))), \
             patch.object(bridge, 'get_remote_pairing_tunnel_services', discover):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await session._connect_wireless_via_remote_pairing()

        self.assertEqual(raised.exception.code, 'wireless_device_not_discoverable')
        self.assertEqual(captured['udid'], '00008150-abcdef')
        self.assertEqual(captured['bonjour_timeout'],
                         bridge.REMOTE_PAIRING_DISCOVERY_TIMEOUT_SECONDS)
        self.assertIn('discovering_wireless_device',
                      [event['code'] for event in ipc.events])

    async def test_usb_remote_pairing_provision_reconnects_after_pair_completion(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        calls = []

        class Service:
            def __init__(self, complete_pair):
                self.complete_pair = complete_pair
                self.closed = False

            async def connect(self, autopair):
                calls.append(autopair)
                if self.complete_pair:
                    raise bridge.RemotePairingCompletedError()

            async def close(self):
                self.closed = True

        services = [Service(True), Service(False)]

        async def create(_lockdown):
            return services.pop(0)

        session = bridge.TouchSession(Ipc(), 120, udid='trusted-device')
        with patch.object(bridge.RemotePairingLockdownService, 'create', create):
            self.assertTrue(await session._provision_remote_pairing(object()))

        self.assertEqual(calls, [True, False])

    async def test_lockdown_is_closed_when_tunnel_setup_fails(self):
        import usb_touch_bridge as bridge

        class Ipc:
            async def emit(self, _event):
                pass

        class Lockdown:
            def __init__(self):
                self.closed = False

            async def close(self):
                self.closed = True

        lockdown = Lockdown()
        session = bridge.TouchSession(Ipc(), 120, udid='test-udid')

        async def create_lockdown(**_kwargs):
            return lockdown

        async def fail_tunnel_setup(_lockdown):
            raise RuntimeError('tunnel setup failed')

        with patch.object(bridge, 'create_using_usbmux', create_lockdown), \
             patch.object(session, '_connect_with_lockdown', fail_tunnel_setup):
            with self.assertRaisesRegex(RuntimeError, 'tunnel setup failed'):
                await session.connect()

        self.assertTrue(lockdown.closed)

    async def test_lockdown_connect_uses_existing_trust_without_autopair(self):
        import usb_touch_bridge as bridge

        class Ipc:
            async def emit(self, _event):
                pass

        lockdown = object()
        create_arguments = {}

        async def create_lockdown(**kwargs):
            create_arguments.update(kwargs)
            return lockdown

        session = bridge.TouchSession(Ipc(), 120, udid='trusted-device')
        with patch.object(bridge, 'create_using_usbmux', create_lockdown):
            result = await session._create_lockdown_with_retry('USB')

        self.assertIs(result, lockdown)
        self.assertEqual(create_arguments, {
            'serial': 'trusted-device',
            'connection_type': 'USB',
            'autopair': False,
        })

    async def test_mux_disconnect_rebuilds_lockdown_client(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        ipc = Ipc()
        lockdown = object()
        attempts = 0

        async def create_lockdown(**_kwargs):
            nonlocal attempts
            attempts += 1
            if attempts < 3:
                raise bridge.MuxException('socket connection broken')
            return lockdown

        session = bridge.TouchSession(ipc, 120, udid='trusted-device')
        with patch.object(bridge, 'create_using_usbmux', create_lockdown), \
             patch.object(bridge.asyncio, 'sleep', return_value=None):
            result = await session._create_lockdown_with_retry('USB')

        self.assertIs(result, lockdown)
        self.assertEqual(attempts, 3)
        self.assertEqual([event['code'] for event in ipc.events], [
            'lockdown_retry', 'lockdown_retry'])

    async def test_active_capture_mux_retries_without_stopping_mirroring(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        class Device:
            activated = True
            dev = object()
            serial = 'trusted-device'

        attempts = []

        class Mux:
            def __init__(self, _dev, _serial):
                self.mux = object()
                self.closed = False
                attempts.append(self)

            def start(self):
                if len(attempts) == 1:
                    raise RuntimeError('device did not answer usbmux VERSION packet')

            def close(self):
                self.closed = True

        class Server:
            def __init__(self, _mux, _serial, port):
                self.stopped = False

            def start(self):
                return '127.0.0.1:45678'

            def stop(self):
                self.stopped = True

        ipc = Ipc()
        session = bridge.TouchSession(ipc, 120, udid='trusted-device')
        previous_address = os.environ.get('USBMUXD_SOCKET_ADDRESS')
        try:
            with patch.object(bridge, '_get_usb_backend', return_value=object()), \
                 patch.object(bridge, '_find_usb_devices', return_value=[Device()]), \
                 patch.object(bridge, '_UsbMuxTransport', Mux), \
                 patch.object(bridge, '_UsbmuxdThread', Server), \
                 patch.object(bridge.asyncio, 'sleep', return_value=None):
                await session._start_capture_mux()

            self.assertEqual(len(attempts), 2)
            self.assertTrue(attempts[0].closed)
            self.assertIs(session._usb_mux_transport, attempts[1])
            self.assertEqual(os.environ['USBMUXD_SOCKET_ADDRESS'], '127.0.0.1:45678')
            self.assertEqual([event['code'] for event in ipc.events], ['capture_mux_retry'])
        finally:
            await session._cleanup()
            if previous_address is None:
                os.environ.pop('USBMUXD_SOCKET_ADDRESS', None)
            else:
                os.environ['USBMUXD_SOCKET_ADDRESS'] = previous_address

    async def test_active_capture_mux_falls_back_after_repeated_version_timeouts(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        class Device:
            activated = True
            dev = object()
            serial = 'trusted-device'

        muxes = []

        class Mux:
            def __init__(self, _dev, _serial):
                muxes.append(self)

            def start(self):
                raise RuntimeError('device did not answer usbmux VERSION packet')

            def close(self):
                pass

        ipc = Ipc()
        session = bridge.TouchSession(ipc, 120, udid='trusted-device')
        previous_address = os.environ.get('USBMUXD_SOCKET_ADDRESS')
        try:
            with patch.object(bridge, '_get_usb_backend', return_value=object()), \
                 patch.object(bridge, '_find_usb_devices', return_value=[Device()]), \
                 patch.object(bridge, '_UsbMuxTransport', Mux), \
                 patch.object(bridge.asyncio, 'sleep', return_value=None):
                await session._start_capture_mux()

            self.assertEqual(len(muxes), bridge.CAPTURE_MUX_START_ATTEMPTS)
            self.assertIsNone(session._usb_mux_transport)
            self.assertIsNone(session._usb_mux_server)
            self.assertEqual([event['code'] for event in ipc.events], [
                'capture_mux_retry', 'capture_mux_retry', 'capture_mux_fallback'])
        finally:
            if previous_address is None:
                os.environ.pop('USBMUXD_SOCKET_ADDRESS', None)
            else:
                os.environ['USBMUXD_SOCKET_ADDRESS'] = previous_address

    async def test_non_transport_lockdown_error_is_not_retried(self):
        import usb_touch_bridge as bridge

        class Ipc:
            async def emit(self, _event):
                pass

        attempts = 0

        async def create_lockdown(**_kwargs):
            nonlocal attempts
            attempts += 1
            raise RuntimeError('protocol rejected')

        session = bridge.TouchSession(Ipc(), 120, udid='trusted-device')
        with patch.object(bridge, 'create_using_usbmux', create_lockdown):
            with self.assertRaisesRegex(RuntimeError, 'protocol rejected'):
                await session._create_lockdown_with_retry('USB')

        self.assertEqual(attempts, 1)

    def test_touch_session_service_failures_are_recoverable(self):
        import usb_touch_bridge as bridge

        self.assertTrue(bridge.TouchSession._is_optional_session_failure(
            RuntimeError('No such service: com.apple.coredevice.displayservice')))
        self.assertTrue(bridge.TouchSession._is_remote_control_unsupported_ios(
            RuntimeError('startmediastream returned 9021')))
        self.assertTrue(bridge.TouchSession._can_fallback_to_direct_hid(
            RuntimeError('startmediastream returned 9021')))
        self.assertFalse(bridge.TouchSession._is_optional_session_failure(
            RuntimeError('startmediastream returned 9021')))
        self.assertTrue(bridge.TouchSession._is_optional_session_failure(
            RuntimeError('No such service: com.apple.coredevice.hid.universalhidservice')))
        self.assertFalse(bridge.TouchSession._is_optional_session_failure(
            RuntimeError('connection reset by peer')))

    async def test_missing_display_service_does_not_abort_hid_session(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        class MissingDisplay:
            def __init__(self, _rsd):
                pass

            async def __aenter__(self):
                raise RuntimeError('No such service: com.apple.coredevice.displayservice')

        ipc = Ipc()
        session = bridge.TouchSession(ipc, 120)
        session.rsd = object()
        session.hid = object()
        with patch.object(bridge, 'DisplayService', MissingDisplay):
            await session._open_gate()

        self.assertIsNone(session.display)
        self.assertTrue(session.gate_open)
        self.assertEqual(session.auth_mode, 'direct')
        self.assertEqual(ipc.events[-1]['event'], 'warning')
        self.assertEqual(ipc.events[-1]['code'], 'gate_unavailable')

    def test_legacy_hid_service_uses_legacy_feature(self):
        import usb_touch_bridge as bridge

        self.assertEqual(bridge.LegacyUniversalHIDServiceService.SERVICE_NAME,
                         'com.apple.coredevice.hid.universalhid')
        self.assertEqual(bridge.LEGACY_UNIVERSAL_HID_FEATURE,
                         'com.apple.coredevice.feature.remote.universalhid')

    async def test_missing_main_touchscreen_surface_never_reports_ready(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        class Rsd:
            peer_info = {'Services': {}}

        class Hid:
            SERVICE_NAME = 'com.apple.coredevice.hid.universalhidservice'

            def __init__(self, _rsd):
                pass

            async def __aenter__(self):
                return self

            async def __aexit__(self, *_args):
                pass

            async def list_connected_services(self):
                return {'connectedServices': [{'_ServiceID': 1281}]}

        ipc = Ipc()
        session = bridge.TouchSession(ipc, 120)
        session.rsd = Rsd()
        with patch.object(bridge, 'UniversalHIDServiceService', Hid), \
             patch.object(bridge, 'LegacyUniversalHIDServiceService', Hid):
            with self.assertRaises(bridge.BridgePrerequisiteError) as raised:
                await session._init_touch()

        self.assertEqual(raised.exception.code, 'touch_surface_unavailable')
        self.assertFalse(any(event['event'] == 'ready' for event in ipc.events))


class TestWifiSyncProvisioning(unittest.IsolatedAsyncioTestCase):
    async def test_missing_wifi_sync_value_is_treated_as_disabled(self):
        import usb_touch_bridge as bridge
        from pymobiledevice3.exceptions import MissingValueError

        class Lockdown:
            async def get_enable_wifi_connections(self):
                raise MissingValueError('MissingValue', 'test-udid', 'test-ios')

        self.assertFalse(await bridge.get_wifi_sync_enabled(Lockdown()))

    async def test_enable_uses_usb_without_automatic_pairing_and_closes_lockdown(self):
        import usb_touch_bridge as bridge

        class Ipc:
            def __init__(self):
                self.events = []

            async def emit(self, event):
                self.events.append(event)

        class Lockdown:
            udid = 'trusted-device'

            def __init__(self):
                self.enabled = False
                self.set_values = []
                self.closed = False

            async def get_enable_wifi_connections(self):
                return self.enabled

            async def set_enable_wifi_connections(self, value):
                self.set_values.append(value)
                self.enabled = value

            async def close(self):
                self.closed = True

        ipc = Ipc()
        lockdown = Lockdown()
        create_arguments = {}

        async def create_lockdown(**kwargs):
            create_arguments.update(kwargs)
            return lockdown

        with patch.object(bridge, 'BridgeChannel', return_value=ipc), \
             patch.object(bridge, 'create_using_usbmux', create_lockdown):
            self.assertTrue(await bridge.enable_wifi_sync_async('trusted-device'))

        self.assertEqual(create_arguments, {
            'serial': 'trusted-device',
            'connection_type': 'USB',
            'autopair': False,
        })
        self.assertEqual(lockdown.set_values, [True])
        self.assertTrue(lockdown.closed)
        self.assertEqual(ipc.events[-1], {
            'event': 'wifi_sync_enabled',
            'udid': 'trusted-device',
            'changed': True,
        })

    async def test_already_enabled_device_is_not_written_again(self):
        import usb_touch_bridge as bridge

        class Ipc:
            async def emit(self, _event):
                pass

        class Lockdown:
            udid = 'trusted-device'
            closed = False

            async def get_enable_wifi_connections(self):
                return True

            async def set_enable_wifi_connections(self, _value):
                raise AssertionError('already-enabled setting must not be rewritten')

            async def close(self):
                self.closed = True

        lockdown = Lockdown()

        async def create_lockdown(**_kwargs):
            return lockdown

        with patch.object(bridge, 'BridgeChannel', return_value=Ipc()), \
             patch.object(bridge, 'create_using_usbmux', create_lockdown):
            self.assertTrue(await bridge.enable_wifi_sync_async('trusted-device'))

        self.assertTrue(lockdown.closed)


if __name__ == '__main__':
    unittest.main()
