"""The USB build hook must not discover backends from the host environment."""
import os
from pathlib import Path
import runpy
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


class UsbPackagingHookTests(unittest.TestCase):
    def test_host_alias_is_not_collected_or_probed(self):
        hook = Path(__file__).resolve().parents[1] / 'scripts/usb-bridge-recipe/hooks/hook-usb.py'
        with TemporaryDirectory() as directory:
            # These names are searched before libusb0 by the upstream runtime
            # hook. The local build hook must not even attempt host discovery.
            for alias in ('usb-1.0.dll', 'usb.dll', 'usb-0.1.dll'):
                (Path(directory) / alias).write_bytes(b'host library fixture')
            with patch.dict(os.environ, {'PATH': directory}), \
                 patch('ctypes.util.find_library', side_effect=AssertionError('host DLL lookup')), \
                 patch('usb.core.find', side_effect=AssertionError('host USB discovery')):
                settings = runpy.run_path(str(hook))
        self.assertEqual(settings['binaries'], [])
        self.assertIn('glob', settings['hiddenimports'])
