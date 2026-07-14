# AirPlay receiver source and licenses

The files in `bin/x64` are a pinned runtime subset of AirPlayServer v1.1.0,
with the wrapper patched for native Windows FFmpeg loading, per-client IPC
identity, AirPlay SETUP device metadata extraction, and runtime-selectable
AirPlay display capability responses with a 5120x2880@60 fallback. The SETUP
metadata patch reads the sender's
`deviceID`, `model` (Apple ProductType), and `osVersion` plist values and
forwards them to the host as an explicit IPC `DeviceInfo` message.
iPhoneMirror's receiver build script reapplies both patches and verifies the
compiled DLL contains the runtime width, height and frame-rate capability
markers before installation. The host only accepts the predefined maximum,
1080p, 720p and 540p profiles exposed by the application, and forwards the
configured receiver name into both DNS-SD registration and the `/info` plist.
iPhoneMirror starts its own GPL-licensed `iPhoneMirror.WirelessHost.exe`
process, which loads `airplay2dll.dll`; the GPL-3.0-only application and native
capture core exchange decoded frames with that process over a named pipe.

- Project: https://github.com/xenos1337/AirPlayServer
- Version: v1.1.0
- Commit: `ff149b2e768bf9ae93199de941ab170571a941a4`
- Original release artifact SHA-256:
  `a08140406e5735b19e47c1697e903174863cda396a3dd54571ff68c0e95c04db`
- Corresponding source:
  https://github.com/xenos1337/AirPlayServer/archive/refs/tags/v1.1.0.zip

The receiver includes or derives from the following components:

- AirPlayServer wrapper and UI: MIT (`LICENSE-MIT.txt`).
- PlayFair FairPlay implementation: GPL-3.0 (`LICENSE-PLAYFAIR-GPL-3.0.md`).
- FFmpeg 4.4.2 H.264 decoder, resampling and scaling libraries: LGPL-2.1-or-later
  (`LICENSE-FFMPEG-LGPL-2.1.txt`). The native Windows build avoids the MSYS2
  runtime and reports only the libraries required by the receiver.
- Fraunhofer FDK AAC: Fraunhofer FDK AAC license (`NOTICE-FDK-AAC.txt`).

AirPlay is an Apple protocol and trademark. This is an unofficial compatible
receiver. iPhoneMirror supplies its own Windows DNS-SD compatibility DLL and
does not install or depend on the legacy Bonjour service.
