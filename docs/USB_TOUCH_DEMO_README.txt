USB iPhone Touch Demo

Historical demo handoff notes. The current root build.ps1 builds the main app
and iUsbBridge runtime; it does not produce UsbTouchDemo.exe. For the maintained
bridge interface, see USB_TOUCH_BRIDGE_USAGE.md. The Python demo source is
tools/usb_mouse_demo.py and is separate from the packaged application.

1. Connect and unlock an iPhone by USB, then tap Trust This Computer.
2. If using an existing compatible demo build, keep the complete bridge runtime
   beside UsbTouchDemo.exe: iUsbBridge.exe, iUsbBridge.runtime.json and _internal.
   Then run UsbTouchDemo.exe.
3. Click or drag inside the window to send touch input.
4. Close the window to release the active touch.

The demo automatically selects the first USB iPhone. To select a specific
device, pass its UDID as the first argument to the demo; the bundled bridge
performs USB enumeration itself.

The historical release folder was dist/UsbTouchDemo. Copying only the current
bridge EXE is insufficient; preserve its entire onedir runtime and verify that
the demo supports the current input protocol before use.
