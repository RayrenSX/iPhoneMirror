USB iPhone Touch Demo

1. Connect and unlock an iPhone by USB, then tap Trust This Computer.
2. Keep `iUsbBridge.exe` beside `UsbTouchDemo.exe`, then run `UsbTouchDemo.exe`.
3. Click or drag inside the window to send touch input.
4. Close the window to release the active touch.

The demo automatically selects the first USB iPhone. To select a specific
device, pass its UDID as the first argument to the demo; the bundled bridge
performs USB enumeration itself.

The release folder `dist/UsbTouchDemo` is ready to run after copying the bridge
from `dist/iUsbBridge.exe` into that folder. The demo is fully self-contained
and uses the project's own bridge.
