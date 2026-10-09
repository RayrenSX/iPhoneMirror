# -*- mode: python ; coding: utf-8 -*-
import importlib.util
import sys
from pathlib import Path
from PyInstaller.utils.hooks import collect_submodules, collect_data_files, copy_metadata

hiddenimports = []
# Analyse imports from our bridge, not every upstream CLI/service. Collecting
# all of pymobiledevice3 pulls in screenshot/video tooling (PyAV and Pillow)
# which the HID bridge does not use. Keep the lazy tunnel/media gate below.
hiddenimports += collect_submodules('qh3')
hiddenimports += collect_submodules('srptools')
hiddenimports += collect_submodules('construct')
hiddenimports += collect_submodules('cryptography')
hiddenimports += collect_submodules('developer_disk_image')
hiddenimports += collect_submodules('ipsw_parser')
hiddenimports += collect_submodules('pyimg4')
hiddenimports += collect_submodules('opack2')
hiddenimports += collect_submodules('lzfse')
hiddenimports += collect_submodules('lzss')
hiddenimports += collect_submodules('pmd_net_addr')
hiddenimports += collect_submodules('pmd_pytcp')
hiddenimports += collect_submodules('pytun_pmd3')


def existing_modules(module_names):
    """Keep optional import names out of PyInstaller's missing-import log."""
    result = []
    for module_name in module_names:
        try:
            if importlib.util.find_spec(module_name) is not None:
                result.append(module_name)
        except (ImportError, AttributeError, ModuleNotFoundError, ValueError):
            pass
    return result

hiddenimports += [
    'pymobiledevice3.remote.userspace_tunnel',
    'pymobiledevice3.remote.tunnel_service',
    'pymobiledevice3.remote.core_device.hid_service',
    'pymobiledevice3.remote.core_device.display_service',
    'pymobiledevice3.remote.core_device.screen_stream',
    'pymobiledevice3.remote.core_device.core_device_service',
    'pymobiledevice3.remote.remote_service_discovery',
    'pymobiledevice3.remote.remotexpc',
    'pymobiledevice3.remote.xpc_message',
    'pymobiledevice3.remote.common',
    'pymobiledevice3.remote.module_imports',
    'pymobiledevice3.lockdown',
    'pmd_pytcp',
    'pytun_pmd3',
    'pytun_pmd3.wintun',
]
hiddenimports = existing_modules(hiddenimports)

datas = []
datas += collect_data_files('pymobiledevice3')
datas += collect_data_files('qh3')
datas += collect_data_files('developer_disk_image')
datas += collect_data_files('pmd_net_addr')
datas += collect_data_files('pmd_pytcp')
datas += collect_data_files('pytun_pmd3')
# PersonalizedImageMounter reaches pyimg4 through the Apple TSS path.  pyimg4
# resolves its version from installed distribution metadata at import time, so
# its .dist-info must accompany the onedir bridge as well.
datas += copy_metadata('pyimg4')

# Onedir places these at _internal's root (sys._MEIPASS). Merely shipping
# them beside iPhoneMirror.exe cannot satisfy pyi_rth_usb's library lookup.
usb_binaries = [(str(Path(SPECPATH) / 'native' / name), '.')
                for name in ('libusb0.dll', 'libusb-1.0.dll')]

a = Analysis(
    ['src/usb_touch_bridge.py'],
    pathex=[],
    binaries=usb_binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    # Override hook-usb: its host discovery can add aliases such as usb-1.0.dll
    # that shadow the pinned libusb0 backend in the standard runtime hook.
    hookspath=[str(Path(SPECPATH) / 'hooks')],
    hooksconfig={},
    runtime_hooks=[],
    # The optional Pygments image formatter is the only Pillow consumer in
    # this graph. The bridge uses text diagnostics, never image formatting.
    excludes=['pytest', 'IPython', 'jedi', 'frida', 'py_spy', 'pygments.formatters.img'],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    name='iUsbBridge',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=True,
    disable_windowed_traceback=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    exclude_binaries=True,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name='iUsbBridge',
)
