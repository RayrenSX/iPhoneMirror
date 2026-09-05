<p align="center">
  <img src="src/App/Assets/iPhoneMirror.png" width="112" alt="iPhoneMirror icon">
</p>

<h1 align="center">iPhoneMirror</h1>

<p align="center">
  Low-latency iPhone screen and system-audio mirroring for Windows.<br>
  Direct USB capture and wireless AirPlay reception in one application.
</p>

<p align="center"><a href="README.md">简体中文</a> · <strong>English</strong></p>

<p align="center">
  <a href="https://github.com/RayrenSX/iPhoneMirror/releases"><img alt="GitHub Release" src="https://img.shields.io/github/v/release/RayrenSX/iPhoneMirror?include_prereleases&sort=semver"></a>
  <a href="https://github.com/RayrenSX/iPhoneMirror/actions/workflows/windows-build.yml"><img alt="Windows build" src="https://github.com/RayrenSX/iPhoneMirror/actions/workflows/windows-build.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="GPL v3 License" src="https://img.shields.io/badge/license-GPL--3.0--only-3DA639.svg"></a>
  <img alt="Windows 10 and 11 x64" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
</p>

> [!IMPORTANT]
> This is a public preview and is not commercially Authenticode-signed, so Windows
> may show SmartScreen or unknown-publisher warnings. Apple Screen Capture uses a
> private protocol and may require updates for future iOS versions. Official builds
> currently support Windows x64 only; Windows ARM64 is unsupported because the USB
> kernel driver and wireless runtime are not available for ARM64.

> [!TIP]
> ### Special Thanks: Linux Port
>
> A special thank-you to **[@furruka](https://github.com/furruka)** for creating and
> maintaining a native Linux port based on this project in the dedicated
> [Linux adaptation branch](https://github.com/furruka/iPhoneMirror). This work extends
> iPhoneMirror's USB and AirPlay mirroring paths to Linux, replacing Windows-specific
> GUI, rendering, audio, video-decoding, USB-communication and device-discovery layers
> with native counterparts while aiming to preserve the upstream protocol and policy
> behavior.
>
> The port is still under active development and does not currently provide a usable
> Linux release package. See the [Linux port notes](https://github.com/furruka/iPhoneMirror/blob/linux-port/docs/LINUX_PORT.md)
> for its current status, build instructions and known limitations. We are deeply grateful
> for furruka's time and contribution, and encourage Linux users to follow and support
> this work.

## Download

The current release is `v1.8.1`. Download `iPhoneMirror-Setup-v*-x64.exe` from
[Releases](https://github.com/RayrenSX/iPhoneMirror/releases). The three-language
Setup wizard supports a custom destination, defaults to
`C:\Program Files\iPhoneMirror` for an administrator install, creates Start menu
entries, and offers an optional desktop shortcut. A per-user install uses the
Windows user-program directory. For portable use, download
`iPhoneMirror-v*-win-x64.zip`, extract it completely, and run
`iPhoneMirror.exe`. If Windows reports **Bad Image** or `0xc0e90002` for a
wireless DLL, use Setup. If portable use is required, open the downloaded
ZIP's Properties, select **Unblock**, and extract it again; unblocking one
already-extracted DLL is not sufficient.

Both packages are self-contained and include the standalone
`iPhoneMirror.Driver.exe` driver manager. They do not require a separate .NET
Desktop Runtime or driver-tool download. Verify downloads against the
`SHA256SUMS.txt` asset from the same Release.

By default the app checks GitHub Releases after startup. When an update is
available, it shows the version, publication date, and Markdown release notes;
**Update now** downloads, verifies, and launches the in-place upgrade, then
restarts the app. The About page also provides manual checks, stable/Beta
channel controls, and automatic downloads. App themes are configured under App
preferences in the main window. Network failures and timeouts never block normal
startup.

The [complete user guide (Chinese)](docs/USER_GUIDE.md) covers every main interface and workflow.

The computer needs Apple USB support. If it is missing, the driver manager first
uses a trusted local `AppleMobileDeviceSupport64.msi`, then downloads the
standalone MSI from Apple's Software Update catalog, and finally falls back to
the official signed desktop iTunes package and extracts its Apple Mobile Device
Support component. Apple Devices from Microsoft Store remains a supported manual
option; Apple binaries are not redistributed by this project. Wireless discovery
uses the DNS-SD support built into Windows 10/11 and does not install Bonjour;
discovery itself does not require administrator access. An administrator Setup
adds the `iPhoneMirror Wireless AirPlay` local-subnet firewall rule. The rule is
constrained to `WirelessHost.exe` and the local subnet, while allowing the
TCP/UDP media ports negotiated per AirPlay session; it is removed on uninstall.
Portable packages do not change the firewall automatically.

## Project description

iPhoneMirror is a local Windows 10/11 x64 iPhone/iPad mirroring tool. It keeps
wired USB capture and local-network AirPlay reception behind one session,
preview, audio, screenshot, detached-window, OBS and multi-device workflow,
without cloud relay.

The project has four explicit boundaries: the C++ core owns Apple's private
USB protocol, QuickTime/CoreMedia parsing, H.264 decoding, D3D11 rendering and
WASAPI audio; the WPF app owns device discovery, session control and UI; and an
isolated wireless host owns AirPlay protocol and decode, sending bounded media
frames over a named pipe. Driver installation, repair and removal belong to the
standalone `iPhoneMirror.Driver.exe`; the main app only reads wired-driver
state and never mutates system drivers inside the capture process.

> [!TIP]
> See the [complete user guide](docs/USER_GUIDE.md) for driver setup,
> USB, AirPlay, multi-device previews, OBS, logs and advanced settings.

## Highlights

### Device-specific iPhone and iPad presentation

iPhoneMirror does not put every device inside the same generic rounded
rectangle. It resolves Apple's `ProductType` into visual profiles for iPhone X,
notched phones, mini, standard/Max, Dynamic Island, iPad Pro, Air, mini and
all-screen base iPads. Known Home-button and rectangular displays stay
rectangular. Unknown future devices use a conservative geometry fallback so an
iPad is not clipped with a phone-shaped curve.

The profile affects both native rendering and the detached-window outline.
Resizing, orientation changes and full-screen transitions preserve the visual
shape of that device, while the context menu still allows the user to remove or
restore corners manually.
Corner parameters are visual fits based on public device appearance and frame
geometry, not Apple-published industrial measurements.

### Unified transport, native rendering and multiple devices

- USB Screen Capture and local-network AirPlay share one device, preview, audio and OBS workflow.
- Each phone has its own session and detached window; multiple devices can keep running together.
- Device cards support press-and-hold reordering, and a new wireless sender auto-selects only once.
- H.264/CoreMedia and AirPlay screen-mirroring frames are decoded locally and presented through D3D11/DirectComposition.
- Media does not pass through an iPhoneMirror cloud relay, and USB capture does not depend on the network.
- Clean detached windows are ready for OBS, while screenshots read the decoded frame without application UI.
- Optional BLE HID mouse/keyboard control through iOS AssistiveTouch, with no phone-side app or jailbreak required.

### Compared with common mirroring tools

| Dimension | iPhoneMirror | Common general-purpose approach |
|---|---|---|
| Connections | USB and AirPlay in one workflow | One transport, or separate wired and wireless apps |
| Device shape | Per-family iPhone/iPad curves and corners | One generic radius, rectangle or extra black framing |
| Multiple devices | Independent sessions, windows, ordering and simultaneous preview | Primarily single-device switching |
| Wired compatibility | Per-device Demo, experimental AirPlay and Aisi modes | Fixed negotiation parameters |
| OBS | Clean native detached window | Crop the control UI or capture the desktop |
| Drivers | Separate per-device install, repair, removal and logs | Driver changes hidden inside the main application |
| Data path | Local PC/LAN processing with no project cloud relay | Some products require accounts or online services |

iPhoneMirror's optional Bluetooth control uses BLE HID plus iOS AssistiveTouch;
it is limited to pointer-style input and cannot inject multi-touch events. The
project has no integrated video editor. Apple's private protocol and compatible
AirPlay implementation may require updates for future iOS releases.

## Features

| Area | Implementation |
|---|---|
| Wired capture | Direct USB with per-device Demo, experimental AirPlay, and Aisi-compatible modes |
| Wireless capture | Local-network AirPlay integrated with the main preview and every output feature |
| Video-app casting | AirPlay/DLNA HTTP(S)/HLS playback, controls, and source audio |
| Video | CoreMedia/AVCC H.264, HEVC description parsing, and Media Foundation auto/hardware/software policies |
| Rendering | Native D3D11/DirectComposition preview with full-range BT.709 metadata |
| Audio | USB 48 kHz PCM and AirPlay PCM with WASAPI playback, mute and volume |
| Devices | iPhone/iPad metadata, trust status, stable refresh and safe switching |
| Quality | Native/1080p/720p/540p local limits and 24/30/60/120 FPS limits |
| Preview | Main, detached, full-screen, rotation, aspect lock and device-aware corners |
| OBS | Clean per-device detached window for Window Capture |
| Bluetooth control | Per-device BLE HID mouse/keyboard binding, system navigation, and configurable global shortcuts |
| Image adjustments | Preview-only brightness, contrast, saturation, and gamma |
| Tools | Screenshot, force refresh, shortcuts, live logs, Simplified Chinese, Traditional Chinese (Hong Kong), and English UI |
| Driver | Strict per-device check before wired capture; opens the standalone driver manager on failure |

Resolution and FPS options cap local presentation only; they do not reduce the
original USB stream quality.

USB devices default to **A Demo (recommended)**, which advertises
`Valeria=true` with the native `DisplaySize` and preserves complete phone framing, but locks status-bar date,
time, and battery to Apple's demo values. **B AirPlay (experimental)** uses
native dimensions and adaptive orientation so video apps can use external
playback, with possible cropping or incomplete framing. **C Aisi mode** fixes
the target at 1565×1565 for predictable negotiation at the cost of source
clarity. The exclamation button beside each mode shows its full tradeoffs, and
the selection applies only to the current USB device.

## Quick start

1. Run the Release Setup and launch iPhoneMirror from the Start menu. For the
   portable package, extract the ZIP completely and run `iPhoneMirror.exe`.
2. Connect the iPhone or iPad over USB, unlock it and choose **Trust This Computer**.
3. Click **Driver manager** in the left navigation and run one-click installation for the
   target device. The tool installs missing Apple USB support and the capture
   filter as needed.
4. Select the phone and click **Start Mirroring**. If the selected wired device
   has a missing or invalid driver, the app cancels that start attempt and opens
   the driver manager automatically.

Selecting another device first sends the QuickTime stop controls to the prior
session and restores its normal USB configuration. Closing the main window runs
the same cleanup path.

> [!WARNING]
> Do not use Zadig to replace the Apple parent driver with WinUSB/libusb.
> iPhoneMirror only bundles the `libusb0.dll` user-mode runtime required to
> start the application. It does not install or enable the kernel capture
> filter; use the separate driver utility for any `libusb0` UpperFilter changes.

## Wired driver management

`iPhoneMirror.exe` only reads driver state. Installation, repair and removal are
performed by the standalone `iPhoneMirror.Driver.exe` in the same directory.
The **Driver manager** button in the left navigation opens it at any time; if that exact
tool is already running, the existing window is activated.

When **Start Mirroring** is clicked for a wired device, the app verifies the
currently selected phone before creating a capture session:

- the Apple USB parent still uses `usbccgp`;
- the device has the `libusb0` UpperFilter;
- the `libusb0.sys` file and service are healthy; and
- `libusb0` can enumerate the exact device serial.

Any failure blocks that wired start attempt and opens the driver manager. After
repairing the driver and reconnecting the device when prompted, return to the
main app and click **Start Mirroring** again. UI logs are stored at
`%LOCALAPPDATA%\iPhoneMirror.Driver\Logs\driver-ui.log`; elevated operation logs
are stored under `%ProgramData%\iPhoneMirror.Driver`.

The complete bundled/external driver inventory is documented in
[`docs/DRIVER_DEPENDENCIES.md`](docs/DRIVER_DEPENDENCIES.md).

## Diagnostic logs

The main app writes managed UI and workflow errors to
`%LOCALAPPDATA%\iPhoneMirror\Logs\application.log`. USB, decoder, and rendering
core diagnostics are stored beside it in `capture.log`. Startup failures also
write `startup.log`, and one-click updates create timestamped
`installer-update-*.log` files. If LocalAppData is temporarily unavailable,
critical managed errors fall back to `%TEMP%\iPhoneMirror-fallback.log`.

Use **About → Diagnostics** to open the log folder or clean logs and downloaded
update packages immediately. Logs rotate automatically, files older than 14
days are removed, and the main log directory is capped at 64 MB. Files that are
currently in use are skipped without interrupting mirroring.

> [!NOTE]
> This automatic check applies only to wired USB devices. Wireless AirPlay
> sources neither require nor inspect `libusb0`, and never open the driver
> manager because of driver state.

## Wireless AirPlay

The AirPlay receiver starts with the main application. The user does not need
to click **Start Mirroring** before the receiver appears on an iPhone. No empty
AirPlay source is shown in the left device list; a wireless card is created
after a sender connects and is auto-selected once at connection time.

1. Connect the Windows computer and iPhone/iPad to the same private network.
2. Configure the receiver name and advertised connection profile in the
   **Wireless AirPlay** section.
3. Choose maximum 5120x2880 at 60 fps, default 1080p at 60 fps, 720p at 30 fps,
   or 540p at 30 fps.
4. Click **Apply**. Name and resolution changes are summarized in one dialog.
5. Open Screen Mirroring in iOS Control Center and select the configured name.
6. Use **Stop Mirroring** for a connected wireless session; the receiver keeps running.

Wireless tabs do not show the wired local resolution/FPS caps. AirPlay quality
must be advertised before connection. Applying a new name or profile restarts
the receiver, disconnects all current wireless sessions, and requires the phone
to select the receiver again. Wireless screen-mirroring sessions still support
volume, screenshots, detached/full-screen windows, simultaneous previews and OBS.

There is no fixed application-level wireless device count; practical capacity
depends on CPU/GPU resources, memory and local-network bandwidth.
The same receiver identity also handles video-app AirPlay/DLNA casting. Fixed
control/discovery ports are RAOP `5001`, AirPlay `7001`, DLNA `8090`, and SSDP
`1900`; mirroring and RAOP media ports are negotiated dynamically per session.

An administrator Setup creates the `iPhoneMirror Wireless AirPlay` local-subnet
firewall rule for the wireless host. It is scoped to the host process and local
subnet, but allows the dynamic TCP/UDP media ports returned during AirPlay
`SETUP`; it is removed on uninstall. Existing installations need a package
containing this fix to migrate the old rule. A portable package does not change
the firewall automatically. Add the equivalent process-scoped rule manually on a
trusted network when incoming AirPlay or DLNA connections are blocked.

If a sender connects to a black screen and drops after about ten seconds, check
that both rules show `LocalPort: Any` and point to the current
`Wireless\iPhoneMirror.WirelessHost.exe`; this pattern means the control
connection succeeded but the negotiated mirror port was blocked.

During the AirPlay `SETUP` handshake, the receiver reads the sender's
`deviceID`, `model` (Apple ProductType such as `iPhone9,1`) and `osVersion`
from the binary plist. The values cross the versioned named-pipe IPC as a
`DeviceInfo` message and are shown in the selected-device panel. Known
ProductTypes are rendered as a human-readable model while retaining the raw
identifier in parentheses; unknown identifiers are shown unchanged.

## AirPlay music casting

An iPhone or iPad can send music to Windows without starting Screen Mirroring:

1. Keep the computer and iPhone/iPad on the same private network and start iPhoneMirror.
2. Tap the AirPlay audio button in a music app or the Control Center Now Playing panel.
3. Select the same receiver name used for Screen Mirroring.
4. iPhoneMirror creates a wireless source automatically and plays the PCM audio. Use
   the main audio controls to change volume, mute playback, or stop the session.

An audio-only session carries no video. The preview shows an **AirPlay Music** state,
and screenshot, detached-preview and full-screen tools remain disabled until the sender
starts delivering video.

## Video-app casting

Video apps use the same AirPlay receiver identity but send a playback URL rather
than screen-mirroring frames. Select the receiver from the app's Cast/AirPlay
button; iPhoneMirror opens a separate playback window and supports ordinary
HTTP(S) video and HLS, playback controls, recording, streaming and virtual-camera
output. DRM-protected, login-bound or private playback URLs may not be available
to a third-party receiver.

## Bluetooth reverse control

Enable iOS AssistiveTouch and use a Windows Bluetooth adapter that supports BLE
peripheral mode. On the first connection, confirm the matching phone in the
Bluetooth client-binding dialog; bindings are stored per mirrored device and can
be removed from Settings. The shortcut window configures reverse control, Control
Center, Notification Center, App Switcher, Home, Boss key, Dock and Siri actions.
Unbound actions stay disabled, duplicate bindings are rejected, and `Backspace`
or `Delete` clears a binding. The default reverse-control key is `F9` and the
default Boss key is `Ctrl+Alt+B`.

## Third-party dependencies and licensing

| Dependency | Purpose | License/source |
|---|---|---|
| .NET 10, WPF, Windows SDK | UI, Windows APIs and publishing runtime | Microsoft official runtime |
| libusb 1.0.29 | Optional USB transport compatibility layer | LGPL-2.1-or-later, `third_party/libusb/` |
| libusb-win32 1.2.6.0 | `libusb0` filter driver used by the standalone manager | LGPL-3.0 and upstream terms, `src/DriverInstaller/Assets/` |
| AirPlayServer 1.1.2 | Wireless AirPlay, FairPlay, video and audio decode | GPL-3.0, LGPL-2.1-or-later and upstream terms, `third_party/airplay-server/` |
| FFmpeg 4.4.2 runtime | AirPlayServer's bundled H.264/audio runtime | LGPL-2.1-or-later, distributed with AirPlayServer |
| FFmpeg 8.1.2 runtime | Recording, live output and HLS media-cast bridge | GPL-3.0, bundled by default under `tools/ffmpeg/` |
| quicktime_video_hack fixtures | QuickTime protocol regression vectors | MIT, test fixtures only |

Apple Devices, Apple Mobile Device Support, iTunes and Windows system
components are external prerequisites and are not redistributed by this
project. See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) and the
AirPlayServer `SOURCE.md` for copyright, source, version, hash and license
details.

## OBS

Open the detached preview for either USB or AirPlay and select its iPhoneMirror
window in OBS Window Capture. Windows Graphics Capture is recommended on
Windows 11. See [OBS_OUTPUT.md](docs/OBS_OUTPUT.md).

## Verified devices

| ProductType / iOS | Native frame | Measured result |
|---|---:|---|
| `iPhone18,3` / iOS 26.5.2 | 1206×2622 | ~58.6 FPS, typical decode 3–5 ms, 48 kHz stereo PCM |
| `iPhone13,1` / iOS 18.7.8 | 1082×2340 | ~58.9 FPS, typical decode 3–6 ms, 48 kHz stereo PCM |

These are tested combinations, not a guarantee for every iPhone or iOS build.

## Build from source

Requirements: Windows 10/11 x64, Visual Studio 2026 Build Tools with MSVC,
Windows SDK and CMake, the .NET 10 SDK with Windows Desktop support, and MSYS2
UCRT64 with CMake, Ninja, the UCRT64 toolchain, GStreamer (base, good, bad, libav),
libplist and OpenSSL for the bundled UxPlay fallback.

```powershell
git clone https://github.com/RayrenSX/iPhoneMirror.git
cd iPhoneMirror
./build.ps1 -Configuration Release
```

The script builds the C++20 core, runs protocol tests and publishes the
self-contained WPF application under `outputs/iPhoneMirror`, including:

```text
outputs/iPhoneMirror/iPhoneMirror.exe
outputs/iPhoneMirror/iPhoneMirror.Driver.exe
outputs/iPhoneMirror/iPhoneMirror.Core.dll
outputs/iPhoneMirror/iPhoneMirror.VirtualCamera.dll
outputs/iPhoneMirror/iPhoneMirror.VirtualCamera.Admin.exe
outputs/iPhoneMirror/tools/ffmpeg/ffmpeg.exe
outputs/iPhoneMirror/Wireless/iPhoneMirror.WirelessHost.exe
outputs/iPhoneMirror/Wireless/UxPlay/iPhoneMirror.UxPlayHost.exe
outputs/iPhoneMirror/Wireless/UxPlay/uxplay.exe
```

`outputs/iPhoneMirror` is the portable build with .NET/WPF dependencies bundled
inside its executables. The installer uses `outputs/iPhoneMirror.Installer`,
where the app and driver manager share external runtime DLLs to reduce download
size.

The default build bundles the FFmpeg 8.1.2 media-output runtime so recording and
RTMP/SRT/WHIP streaming work out of the box. Build the compact edition only
when minimum size is required and a system FFmpeg dependency is acceptable:

```powershell
.\build.ps1 -Configuration Release -OmitMediaOutputRuntime
```

Pass `-OmitMediaOutputRuntime` to the release packaging script as well when
publishing the compact edition.

Build all Release assets (Setup, ZIP, checksums, and SBOM):

```powershell
./scripts/package_release.ps1 -Version 1.8.1 -GenerateSbom
```

Pass `-UpdateReleaseManifest` when producing the assets that will be uploaded.
The release script then synchronizes sizes and SHA256 digests for the matching
entry in `updates/releases.json`, keeping the fallback update endpoint valid.
Ordinary local package builds do not modify the published release manifest.

The script downloads hash-pinned Inno Setup 6.7.3 and its Simplified and
Traditional Chinese translations into `work/tools`; no global Inno Setup
installation is required.

Build and run the test suites without publishing the self-contained app:

```powershell
./build.ps1 -Configuration Debug -NoPublish
```

## Architecture

```text
iPhone/iPad
  ├─ USB / QuickTime ─► H.264 / PCM decode ─┐
  └─ AirPlay ─► WirelessHost ─► I420 / PCM ─┤
                                              └─► native session
                                                   ├─► D3D11 previews
                                                   ├─► screenshot / OBS
                                                   ├─► FFmpeg MP4 / RTMP / SRT / WHIP
                                                   ├─► Windows 11 virtual camera
                                                   └─► WASAPI audio
```

See [protocol](docs/PROTOCOL.md), [architecture](docs/ARCHITECTURE.md),
[D3D11 rendering](docs/D3D11_RENDERING.md),
[device corner profiles](docs/DEVICE_CORNER_PROFILES.md) and
[WASAPI audio](docs/WASAPI_AUDIO.md) documentation. Planned work is tracked in
the [upgrade roadmap](docs/ROADMAP.md); roadmap items are not implemented features.

## Current limitations

- Built-in recording and RTMP, SRT, and WebRTC/WHIP output include source audio when the audio track and required encoder are available, and otherwise start immediately as video-only output; MP4, RTMP, and SRT encode AAC while WHIP encodes Opus.
- The app is not commercially code-signed.
- The external driver installation matrix needs broader testing.
- Apple does not publish Screen Capture as a stable third-party API.
- AirPlay compatibility is unofficial and can change with future iOS releases.
- Bluetooth control depends on BLE peripheral mode and iOS AssistiveTouch, and is limited to pointer-style single-touch operations.
- Hardware decode availability depends on the Windows MFT and GPU driver; unsupported systems automatically use software decode while the preview remains D3D11-backed.
- SDR outputs are tagged as full-range BT.709; HDR presentation still depends on the source, display, and receiving client.

## Contributing and security

Read [SUPPORT.md](SUPPORT.md) before opening an issue and
[CONTRIBUTING.md](CONTRIBUTING.md) before sending a pull request. Report
security issues through
[private vulnerability reporting](https://github.com/RayrenSX/iPhoneMirror/security/advisories/new).
Never publish a real UDID, pairing record or unredacted USB capture.

## License and acknowledgements

Original iPhoneMirror code is licensed under the
[GNU General Public License v3.0 only](LICENSE). Distribution of modified or
derivative versions must follow GPLv3 source-availability, notice and copyleft
requirements. Bundled third-party components remain under their own licenses;
see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

The wireless receiver is distributed as an independent GPLv3 process. Exact
source links, binary hashes and component licenses are included under
`Wireless/licenses` in every release package.

Protocol research references:

- [danielpaulus/quicktime_video_hack](https://github.com/danielpaulus/quicktime_video_hack)
- [chotgpt/quicktime_video_hack_windows](https://github.com/chotgpt/quicktime_video_hack_windows)

Apple, iPhone, iOS and QuickTime are trademarks of Apple Inc. This project is
not affiliated with, sponsored by or endorsed by Apple Inc.
