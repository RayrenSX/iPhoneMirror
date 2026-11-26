# iPhoneMirror architecture

[Documentation](README.md) · [简体中文架构](../ARCHITECTURE.md)

This page describes v1.8.5-pre7.

| Layer | Responsibility |
| --- | --- |
| `src/App` | WPF interface, source selection, device identity, per-device sessions, automation API and output configuration |
| `src/Core` | C++ capture, QuickTime USB transport, decode, Direct3D 11 and DirectComposition preview, WASAPI playback |
| `src/WirelessHost` and `src/UxPlayHost` | Original AirPlay/DLNA or optional UxPlay receiver. Video and audio pass to the core over local IPC. |
| `tools` and the packaged Python bridge | CoreDevice preparation, USB and Wi-Fi HID control, device management, and the optional IPA signing worker |
| `src/DriverInstaller` | Separate Windows driver manager, driver readiness and confirmed elevated operations |
| `src/VirtualCamera` | Windows 11 Media Foundation virtual camera |

## Capture and preview

Normal USB capture uses the per-device filter on the QuickTime interface. USBMux remains available separately for trust and device services. Video packets feed the decode and D3D11 render queues. PCM audio feeds WASAPI and can also feed the selected output. The wireless receiver uses the same preview and output infrastructure after IPC delivery.

Each active source has a separate session. Main and independent previews attach to those sessions. Independent windows can be used for OBS Window Capture. FFmpeg handles recording and streaming. The virtual camera publishes video through Media Foundation.

## Device identity, control, and files

The source list merges verified identities when the app can establish that they belong to the same device. Paired wireless discovery validates an existing pairing before reporting wireless availability. Eligible devices on iOS or iPadOS 27 or later can start experimental CoreDevice video through the bridge. That video path is separate from regular AirPlay and from the existing USB or Wi-Fi touch-control requirements.

The app routes input to the focused preview and its active session. USB and Wi-Fi input use the bridge. Bluetooth uses Windows BLE HID. Device Management reuses an authorized device connection. Windows Explorer mapping uses local WebDAV to expose the file domains the device permits. Signing and installation are separate steps.

## Automation

The HTTP API listens on IPv4 loopback when enabled. It exposes authenticated device discovery, capture status, screenshots, and supported device actions. Input requires an exclusive lease for the selected device. The Python SDK and stdio MCP server call this API. [Automation API](../api/README.md)

[Build and test](DEVELOPMENT.md) · [User guide](USER_GUIDE.md)
