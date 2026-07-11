<p align="center">
  <img src="src/App/Assets/iPhoneMirror.png" width="112" alt="iPhoneMirror icon">
</p>

<h1 align="center">iPhoneMirror</h1>

<p align="center">
  Low-latency wired iPhone screen and system-audio capture for Windows.<br>
  USB only—no Wi-Fi or AirPlay required.
</p>

<p align="center"><a href="README.md">简体中文</a> · <strong>English</strong></p>

<p align="center">
  <a href="https://github.com/RayrenSX/iPhoneMirror/releases"><img alt="GitHub Release" src="https://img.shields.io/github/v/release/RayrenSX/iPhoneMirror?include_prereleases&sort=semver"></a>
  <a href="https://github.com/RayrenSX/iPhoneMirror/actions/workflows/windows-build.yml"><img alt="Windows build" src="https://github.com/RayrenSX/iPhoneMirror/actions/workflows/windows-build.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-black.svg"></a>
  <img alt="Windows 10 and 11 x64" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
</p>

> [!IMPORTANT]
> This is a public preview. The application is not commercially Authenticode
> signed, so Windows may show SmartScreen or unknown-publisher warnings. Apple
> Screen Capture is a private protocol and can change in future iOS versions.

## Download

Download `iPhoneMirror-*-win-x64.zip` from
[Releases](https://github.com/RayrenSX/iPhoneMirror/releases), extract it and
run `iPhoneMirror.exe`. The package is self-contained and does not require a
separate .NET Desktop Runtime.

The computer still needs either Apple Devices from Microsoft Store or the
desktop iTunes package containing Apple Mobile Device Support.

## Features

| Area | Implementation |
|---|---|
| Wired capture | Direct USB; no Wi-Fi or AirPlay |
| Video | CoreMedia/AVCC H.264 and low-latency Media Foundation decode |
| Rendering | Native D3D11/DirectComposition preview |
| Audio | 48 kHz stereo PCM with WASAPI playback, mute and volume |
| Devices | iPhone/iPad metadata, trust status, stable refresh and safe switching |
| Quality | Native/1080p/720p/540p local limits and 24/30/60/120 FPS limits |
| Preview | Main, detached, full-screen, rotation, aspect lock and device-aware corners |
| OBS | Stable-title dedicated window for Window Capture |
| Tools | Screenshot, force refresh, shortcuts, live logs, Chinese and English UI |
| Driver | Per-device libusb0 UpperFilter installation with verification and rollback |

Resolution and FPS options cap local presentation only; they do not reduce the
original USB stream quality.

## Quick start

1. Install and start Apple Devices or Apple Mobile Device Support.
2. Connect the iPhone or iPad over USB, unlock it and choose **Trust This Computer**.
3. Extract the Release ZIP and run `iPhoneMirror.exe`.
4. Select the phone and click **Start Mirroring**.
5. On first use for that device, review the scoped driver change and approve UAC.
6. After installation, unplug the cable, wait for its card to disappear, then reconnect it unlocked.

Selecting another device first sends the QuickTime stop controls to the prior
session and restores its normal USB configuration. Closing the main window runs
the same cleanup path.

> [!WARNING]
> Do not use Zadig to replace the Apple parent driver with WinUSB/libusb.
> iPhoneMirror only adds a `libusb0` UpperFilter to the selected Apple
> `usbccgp` device instance. It does not replace the Apple driver or modify the
> WPD/usbmux child interfaces.

## OBS

Open **OBS Preview Window** in iPhoneMirror, then add a Window Capture source in
OBS and choose `iPhoneMirror OBS Preview`. Windows Graphics Capture is
recommended on Windows 11. OBS 30.1+ can capture `iPhoneMirror.exe` audio with
Application Audio Capture. See [OBS_OUTPUT.md](docs/OBS_OUTPUT.md).

## Verified devices

| ProductType / iOS | Native frame | Measured result |
|---|---:|---|
| `iPhone18,3` / iOS 26.5.2 | 1206×2622 | ~58.6 FPS, typical decode 3–5 ms, 48 kHz stereo PCM |
| `iPhone13,1` / iOS 18.7.8 | 1082×2340 | ~58.9 FPS, typical decode 3–6 ms, 48 kHz stereo PCM |

These are tested combinations, not a guarantee for every iPhone or iOS build.

## Build from source

Requirements: Windows 10/11 x64, Visual Studio 2026 Build Tools with MSVC,
Windows SDK and CMake, plus the .NET 10 SDK with Windows Desktop support.

```powershell
git clone https://github.com/RayrenSX/iPhoneMirror.git
cd iPhoneMirror
./build.ps1 -Configuration Release
```

The script builds the C++20 core, runs protocol tests and publishes the
self-contained WPF application under `outputs/iPhoneMirror`.

## Architecture

```text
iPhone
  │ USB / usbmux / Lockdown
  ▼
QuickTime Screen Capture session
  ├─ CoreMedia + AVCC H.264 ─► Media Foundation ─► D3D11 preview
  └─ 48 kHz PCM             ─► WASAPI audio
```

See [protocol](docs/PROTOCOL.md), [architecture](docs/ARCHITECTURE.md),
[D3D11 rendering](docs/D3D11_RENDERING.md),
[device corner profiles](docs/DEVICE_CORNER_PROFILES.md) and
[WASAPI audio](docs/WASAPI_AUDIO.md) documentation.

## Current limitations

- OBS uses Window Capture; there is no virtual camera yet.
- The app and elevated helper are not commercially code-signed.
- A completely clean Windows/libusb0 installation matrix needs broader testing.
- Apple does not publish Screen Capture as a stable third-party API.
- The project does not provide iPhone touch or remote control.

## Contributing and security

Read [SUPPORT.md](SUPPORT.md) before opening an issue and
[CONTRIBUTING.md](CONTRIBUTING.md) before sending a pull request. Report
security issues through
[private vulnerability reporting](https://github.com/RayrenSX/iPhoneMirror/security/advisories/new).
Never publish a real UDID, pairing record or unredacted USB capture.

## License and acknowledgements

Original iPhoneMirror code is available under the [MIT License](LICENSE).
Bundled third-party components remain under their own licenses; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). The GPLv3 libusb-win32 kernel
driver is accompanied by its corresponding upstream source and license text.

Protocol research references:

- [danielpaulus/quicktime_video_hack](https://github.com/danielpaulus/quicktime_video_hack)
- [chotgpt/quicktime_video_hack_windows](https://github.com/chotgpt/quicktime_video_hack_windows)

Apple, iPhone, iOS and QuickTime are trademarks of Apple Inc. This project is
not affiliated with, sponsored by or endorsed by Apple Inc.
