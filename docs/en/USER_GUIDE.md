# iPhoneMirror user guide

[Documentation](README.md) · [简体中文使用指南](../USER_GUIDE.md)

The menu labels below refer to v1.8.5-pre7. Features vary by version. [Pre7 release notes](RELEASE_NOTES.md)

## Install, update, and uninstall

Download Setup or the portable ZIP for Windows 10 or 11 x64. Run Setup, or extract the entire ZIP and run `iPhoneMirror.exe`. Official packages include the .NET runtime and Driver Manager. Windows may show SmartScreen. The release's `SHA256SUMS.txt` lists the expected hashes. [Download](../../README.md#download)

Check for updates under **About** and read the notes before applying an update. **Update settings** controls the update channel, startup checks, automatic downloading, and download route. To uninstall Setup, use **Windows Settings → Apps**. The uninstaller asks whether to remove settings and cached downloads. Use Driver Manager to remove capture drivers.

## First launch and setup

The first launch opens the setup wizard. Choose a language and read the free-software notice. The continue button becomes available after three seconds. The author-support page is optional. Choose the application mode, appearance, and connection methods you use.

Follow the driver and device-binding prompts. Connect and unlock the phone and accept **Trust This Computer** when prompted. Bind the USB, AirPlay, and Bluetooth identities you intend to use to one profile for that device. Configure reverse control if needed, then save display preferences. Start mirroring or control after the wizard finishes.

To finish a skipped step, reopen initial setup from **Settings → Application**. The wizard saves progress and reuses device profiles. Stop any active mirroring or control session before reconfiguring the full wizard.

## Install and manage USB drivers

Open **Drivers** in the sidebar and select the connected phone in Driver Manager. Use one-click installation or repair and follow the Apple USB support and reconnect prompts. Driver Manager checks the Apple driver and Apple Mobile Device Service separately. Keep the Apple parent driver during normal installation. Use Driver Manager's confirmed per-device parent-driver tools if it needs repair. [Driver dependencies](../DRIVER_DEPENDENCIES.md)

## USB mirroring

1. Connect a data-capable cable, unlock the phone, and trust the PC.
2. Open **Sources**, select the device, and click **Start mirroring**. Refresh the list if needed.
3. Click **Stop mirroring** before unplugging. This also stops wired control for that device.

**Settings → Projection** offers two USB modes, saved per device. Stop capture before switching.

| Mode | When to use it |
| --- | --- |
| **AirPlay mode (recommended)** | Default USB mode. Keeps the real status bar and follows rotation. Some video apps can use external playback. |
| **Demo mode (compatibility)** | Try when the recommended mode fails. The status bar shows Apple demo values. Video-app external playback is unavailable. |

A previously saved fixed-size C mode migrates to AirPlay mode. This setting still uses the USB cable. The encoded resolution depends on the device and may differ from the requested size.

## Wireless AirPlay

1. Connect the PC and phone to the same local network.
2. Under **Settings → Projection → Wireless AirPlay**, set the receiver name and quality, then click **Apply**. The original receiver supports screen mirroring, music, and supported video-app casting. UxPlay is an optional mirroring receiver.
3. Open **Control Center → Screen Mirroring** and select the receiver. To disconnect, stop mirroring in Control Center or end the session in iPhoneMirror.

Setup run as administrator configures a local-subnet firewall rule. The portable build may need a firewall rule for `iPhoneMirror.WirelessHost.exe`. The receiver uses negotiated TCP and UDP media ports.

### Paired wireless devices

A device can appear as **Paired · Wireless online** before mirroring starts. iPhoneMirror matches verified identities and shows wired and wireless availability on one device card. For eligible devices, selecting the card and clicking **Start mirroring** starts experimental CoreDevice video. It requires iOS or iPadOS 27 or later, existing pairing, Developer Mode, and a matching developer image. This path provides video only.

## Reverse control

| Method | Requirements |
| --- | --- |
| USB touch | iOS or iPadOS 17.4 or later, trusted cable connection, Developer Mode, a matching developer disk image (DDI), and a working HID service |
| Wi-Fi touch | The same software and service requirements, existing pairing, and a reachable local network. Initial setup may require USB. |
| Bluetooth | A Windows adapter and driver with BLE Peripheral support and AssistiveTouch on the phone |

### USB and Wi-Fi touch control

Select the device and start **Wired control** or **Wireless control**. Follow the status-window prompts and wait for the ready state. The first DDI preparation can take minutes. Use the mouse, keyboard, or touchscreen in the focused preview. These control modes support key mapping and up to five concurrent contacts. Reverse-control settings can also show or hide the phone's keyboard.

### Bluetooth control

On the phone, enable **Settings → Accessibility → Touch → AssistiveTouch**. Start Bluetooth control and follow the binding prompts. In relative mouse mode, pause with the configured release shortcut, usually `F9`. Absolute mouse positioning is experimental. Bluetooth provides pointer and keyboard input. Touch key mapping and real multi-touch require USB or Wi-Fi control.

### Key mapping

1. Select a device with a ready USB or Wi-Fi touch session.
2. Under **Settings → Reverse control → Key mapping**, add a mapping and press and release the desired physical key.
3. Choose tap, long press, hold until release, double tap, or swipe. Set the target or record the gesture in the existing preview, then click **Finish**.
4. Enable key mapping and focus the preview to use the binding. Use the list to edit, disable, or remove individual bindings.

Mappings pause while editing, entering text, or switching devices. Mappings, the mouse, and the touchscreen share five touch contacts. Suppressing a mapped key's normal Windows action is optional.

## Picture, sound, and preview tools

Settings are grouped under **Application**, **Projection**, and **Reverse control**. Projection resolution, frame rate, and decoder preferences save when changed. Color adjustments and receiver settings use their own save or apply buttons.

**Projection → Projection audio output** offers **Windows** for PC playback. Eligible USB sessions can select **Phone only**. Stop recording or streaming first when it uses the phone's audio. For wireless AirPlay, choose the audio destination on the phone.

Add preview tools through **Settings → Application → Customize top toolbar** or the toolbar context menu. **Screenshot** saves the decoded frame. **Copy screen text** freezes it and lets you select text. OCR runs on the PC and uses installed Windows OCR languages. Press `Esc` to return to live video.

### Send files and install an IPA

Use **Send files** or drop files into the preview to select an app that supports file sharing. File operations require the target device's ready USB or Wi-Fi control connection. When you drop an IPA, choose **Transfer** or **Install**. For installation, check the target device and package, select an existing valid signature or a supported signing method, and confirm. Signing options include an Apple account, a P12/PFX file, or a certificate and private key with provisioning profiles.

## Independent windows and full screen

Open an independent window for the selected device. The context menu includes rotation, mute, topmost, position locking, borders, opacity, and reverse control. `F11` or `Alt+Enter` toggles full screen. `Esc` exits.

**Settings → Projection → Independent window startup layout** lets you restore the last per-device layout, place windows automatically, save the current layout as a preset, or enter a custom position and size. The selection applies when the window next opens.

At opacity below 80%, click-through is available under **Window → Style**. Restore interaction with `Ctrl+Alt+Shift+P` or the tray menu. Use **Reverse control → Shortcut settings** to configure window actions and choose which Windows hotkeys to pass through or block.

## Recording, streaming, and OBS

- **MP4 recording.** Select **Stop output** to finalize and save the recording. If you cancel the save dialog, the app retains the completed recording for later.
- **Streaming.** Choose RTMP, SRT, or WebRTC/WHIP and enter the destination. An optional microphone can be mixed into eligible device audio on the streaming tab.
- **OBS.** Add **Window Capture** and select the independent window whose title contains iPhoneMirror and the device name. Prefer Windows Graphics Capture. On a multi-GPU PC, run iPhoneMirror and OBS on the same GPU. Add application audio capture for `iPhoneMirror.exe` if needed.
- **Virtual camera.** Requires Windows 11 and administrator permission for the first installation. Select **iPhoneMirror Virtual Camera** in the receiving app. Capture audio separately.

Recording and streaming can run together for one source. The virtual camera is mutually exclusive with those built-in outputs. **Phone only** prevents recording or streaming the source audio on the PC.

## Device management

Open **Device Management** in the sidebar. **Apps** lists apps and supports permitted actions such as opening or uninstalling. **Files** can browse, import, save, rename, and delete files where permitted.

**Map to Explorer** exposes an optional Windows drive with accessible media, crash logs, and app data or shared Documents. The drive can appear before all folders have been discovered. Read-only permissions carry over from the phone. Windows WebClient settings limit transfer sizes. Device or control disconnect and application exit remove the mapping.

For system logs, profile management, and other advanced actions, click the version on **About** five times and choose **Developer tools → Device tools**.

## Local automation API

Enable **Developer tools → Automation API**, apply the selected port, and copy the key. The API is off by default and listens on IPv4 loopback. Select a device by its returned ID. Automation input requires an exclusive control lease. Release it on completion or use **Reclaim device control** in the app. [HTTP API](../api/README.md) · [Python SDK and MCP](../../sdk/python/README.md)

## Troubleshooting

| Symptom | Checks |
| --- | --- |
| USB device is listed but video stays black | Unlock and trust the phone, check Driver Manager and the cable, and try the other USB mode after stopping capture. Protected video may remain black while the Home Screen works. |
| AirPlay receiver is missing | Use the same local network, avoid isolated guest Wi-Fi, apply receiver changes, and check the firewall. |
| AirPlay connects and then stalls | Check the firewall for negotiated media ports, lower the receiver resolution to 720p or 540p, or try UxPlay. |
| Wireless component reports Bad Image or 0xc0e90002 | Verify hashes. For ZIP, unblock the archive and extract it fully. On managed PCs, check execution policy with the administrator. |
| Sound is missing or breaking up | Check the Windows mixer and default device, then the USB output setting or AirPlay audio route. Increase buffering if needed. |
| USB or Wi-Fi control sends no input | Check trust, Developer Mode, pairing, the DDI/HID status, and the selected device. |
| Bluetooth will not pair | Check BLE Peripheral support, AssistiveTouch, and the phone's binding. If prompted, remove the stale pairing on the phone before pairing again. |
| Independent window is click-through | Restore interaction with `Ctrl+Alt+Shift+P` or the tray menu. |
| OBS shows black | Confirm the independent window has video, then try Windows Graphics Capture and the same GPU for both apps. |
| Explorer files are missing or cannot be written | Wait for discovery, confirm that the app exposes the folder, and check permissions and the Windows WebClient transfer limit. |

In **About → Diagnostics**, copy the relevant logs and the full version. Include the Windows and iOS versions, device model, connection method, reproduction steps, and time of failure. Redact personal information, API keys, and pairing records. [Support and reporting](../../SUPPORT.md)
