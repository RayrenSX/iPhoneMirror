"""Exercise the same offline probes used by the packaged bridge preflight."""
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import call, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from bridge_runtime_check import check_runtime_functionality, check_usb_backends
from usb_touch_bridge import (build_touchscreen_report, build_touchscreen_frame,
                              TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE)


class BridgeRuntimeCheckTests(unittest.TestCase):
    def test_both_native_backends_must_load_without_device_access(self):
        with patch('iostouch.qt.usb.get_backend', return_value=object()) as backend, \
             patch('usb.core.find', side_effect=AssertionError('must not enumerate devices')):
            self.assertEqual(check_usb_backends(), ['usb_backend_libusb0', 'usb_backend_libusb1'])
        self.assertEqual(backend.call_args_list, [call('libusb0'), call('libusb1')])

    def test_missing_backend_cannot_pass_by_falling_back(self):
        for results in ([None, object()], [object(), None]):
            with self.subTest(results=results), \
                 patch('iostouch.qt.usb.get_backend', side_effect=results), \
                 self.assertRaisesRegex(RuntimeError, 'USB backend could not be loaded'):
                check_usb_backends()

    def test_frozen_probe_requires_dlls_in_meipass(self):
        for missing in ('libusb0.dll', 'libusb-1.0.dll'):
            with self.subTest(missing=missing), TemporaryDirectory() as directory:
                root = Path(directory)
                internal = root / '_internal'
                internal.mkdir()
                for name in ('libusb0.dll', 'libusb-1.0.dll'):
                    (root / name).write_bytes(b'root-only DLL fixture')
                    if name != missing:
                        (internal / name).write_bytes(b'bundled DLL fixture')
                with patch.object(sys, 'frozen', True, create=True), \
                     patch.object(sys, 'platform', 'win32'), \
                     patch.object(sys, '_MEIPASS', str(internal), create=True), \
                     patch('iostouch.qt.usb.get_backend', return_value=object()), \
                     self.assertRaisesRegex(RuntimeError, 'Bundled USB backend is missing'):
                    check_runtime_functionality(build_touchscreen_report,
                                                TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE,
                                                build_touchscreen_frame)

    def test_native_serialization_crypto_and_resources(self):
        self.assertEqual([
            'xpc_roundtrip', 'five_slot_hid_reports', 'simultaneous_contacts_independent_release',
            'quic_initial_encryption',
            'aes_gcm_roundtrip', 'lzfse_roundtrip', 'ca_store', 'ddi_async_http_transport',
        ], check_runtime_functionality(build_touchscreen_report,
                                       TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE, build_touchscreen_frame))

    def test_bad_report_is_not_reported_ready(self):
        with self.assertRaisesRegex(RuntimeError, 'HID report'):
            check_runtime_functionality(lambda *args, **kwargs: b'',
                                        TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE, build_touchscreen_frame)

    def test_single_contact_encoder_cannot_pass_multi_contact_check(self):
        def old_frame(contacts, timestamp):
            return build_touchscreen_report(*contacts[-1], timestamp=timestamp)
        with self.assertRaisesRegex(RuntimeError, 'Multi-contact HID'):
            check_runtime_functionality(build_touchscreen_report,
                                        TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE, old_frame)
