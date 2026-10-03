"""Exercise the same offline probes used by the packaged bridge preflight."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from bridge_runtime_check import check_runtime_functionality
from usb_touch_bridge import build_touchscreen_report, TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE


class BridgeRuntimeCheckTests(unittest.TestCase):
    def test_native_serialization_crypto_and_resources(self):
        self.assertEqual([
            'xpc_roundtrip', 'five_slot_hid_reports', 'quic_initial_encryption',
            'aes_gcm_roundtrip', 'lzfse_roundtrip', 'ca_store',
        ], check_runtime_functionality(build_touchscreen_report,
                                       TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE))

    def test_bad_report_is_not_reported_ready(self):
        with self.assertRaisesRegex(RuntimeError, 'HID report'):
            check_runtime_functionality(lambda *args, **kwargs: b'',
                                        TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE)
