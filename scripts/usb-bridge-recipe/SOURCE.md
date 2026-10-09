Python packaging recipe from https://github.com/RayrenSX/iUsbBridge
at commit 08a2114b3241b72b16fa84edaa0e48734c58548f.

This recipe packages this repository's tools/usb_touch_bridge.py,
tools/ddi_support.py, tools/mdns_discovery.py, tools/bridge_runtime_check.py
and tools/iostouch. The tracked mDNS adapter must be included; editing an
installed pymobiledevice3 copy does not repair the packaged bridge.
It produces the schema 1 PyInstaller onedir runtime consumed by iPhoneMirror.
Upstream main now builds a different Rust backend (schema 2); updating this recipe
requires validating the application protocol and runtime integrity checks together.

Build through `New-UsbBridgeBuildSource` in `scripts/UsbBridgeBuildSource.ps1`.
The staging step also verifies and copies the repository's pinned x64
`libusb0.dll` and `libusb-1.0.dll` into `native/`. The spec includes both as
binaries at the root of `_internal` (`sys._MEIPASS`); PyInstaller's USB runtime
hook does not use the application's top-level DLLs by default.
The generated manifest covers both DLLs, and the frozen `--check-runtime`
probe must load each backend successfully without opening a device.

The staged `hooks/hook-usb.py` overrides upstream build-host discovery. It keeps
the runtime hook's `glob` dependency and relies on the spec's explicit binaries.
Do not replace this with `PYINSTALLER_USB_HOOK_SKIP_PYUSB_DISCOVERY`: upstream
still searches PATH when that flag is set, and aliases such as `usb-1.0.dll`
can shadow libusb0 when the frozen executable loads its backends.
