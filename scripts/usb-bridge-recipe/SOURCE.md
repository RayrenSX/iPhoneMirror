Python packaging recipe from https://github.com/RayrenSX/iUsbBridge
at commit 08a2114b3241b72b16fa84edaa0e48734c58548f.

This recipe packages this repository's tools/usb_touch_bridge.py and tools/iostouch.
It produces the schema 1 PyInstaller onedir runtime consumed by iPhoneMirror.
Upstream main now builds a different Rust backend (schema 2); updating this recipe
requires validating the application protocol and runtime integrity checks together.
