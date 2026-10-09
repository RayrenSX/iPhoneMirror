# AirPlay receiver source and licenses

The files in `bin/x64` are a pinned runtime subset of AirPlayServer v1.1.2,
with selected upstream fixes from `c788d6fe` (AirPlay connection compatibility)
and `37d7fd0f` (mirror stream recovery), plus a wrapper patched for native
Windows FFmpeg loading, per-client IPC identity, AirPlay SETUP device metadata
extraction, runtime-selectable AirPlay display capability responses with a
5120x2880@60 fallback, and H.264 decoder recovery across orientation changes.
Selected v1.2.5 changes are adapted by `patches/Apply-AirPlayUpstream125Patch.ps1`:
bounded AVC parameter records, corrected clock units/Windows timed waits,
validated timing responses, empty RTP suppression, audio stop cleanup,
same-session TCP reconnection, and sender pause/geometry events over the
existing logger callback. The public DLL callback ABI and FFmpeg 58 ABI remain
unchanged; host/core IPC is now version 8.
One combined
receiver process publishes matching `_raop._tcp` and `_airplay._tcp` records
with one stable device ID. This follows UxPlay's receiver model and prevents
iOS from hiding a route whose service identity, `/info` identity, or pairing
endpoint disagrees.

Mirror mode uses AirPlayServer v1.1.2's upstream mirroring/audio negotiation
profile (`0x5A7FFEE6,0x0`). Combined/media mode enables UxPlay's AirPlay video
and HLS bits 0 and 4 (`0x5A7FFEF7,0x0`) because this receiver implements the
corresponding URL-video handlers. Within either mode, the `_airplay._tcp` and
`_raop._tcp` DNS-SD records, `/info`, and `/server-info` advertise the same
profile. Screen-mirroring stream type 110 continues to the decoded-frame IPC
path, while `/play`, `/playback-info`, and `/stop` use the URL-video IPC path.
`/rate` pause/resume and `/scrub` seek requests are forwarded as distinct IPC
controls without reloading the media URL. Both the AirPlay HTTP and RAOP ports
handle media controls,
and the two-stage `/fp-setup` exchange remains available before the first video
URL. The `/info` response, HTTP server-info response, and both DNS-SD records
use the same receiver name, model, features, and device ID. The SETUP
metadata patch reads the sender's
`deviceID`, `model` (Apple ProductType), and `osVersion` plist values and
forwards them to the host as an explicit IPC `DeviceInfo` message.
iPhoneMirror also detects the ALAC frames used by audio-only RAOP sessions and
decodes them through the vendored FFmpeg runtime. Screen-mirroring AAC-ELD now
also uses FFmpeg's native AAC decoder, and failed decoder output is no longer exposed
to the host as valid PCM. RAOP packet retransmission and a bounded jitter wait
smooth Wi-Fi delivery, while unrecovered packets use the negotiated PCM frame
length so concealment does not advance the playback clock at the wrong rate.
PCM conversion preserves the decoded sample count and sample rate. No adaptive
audio clock compensation has been added. The data listener permits a matching
peer to reconnect for 120 seconds after transport loss, including while paused.
An idle/static connected stream has no inactivity timeout; only an incomplete
packet read has a three-second deadline. Pending codec/PTS state is cleared on
transport replacement while the negotiated AES stream context is retained.
Interrupted encrypted payloads with a complete header advance CTR by the full
declared frame length. Three consecutive invalid decrypted frames end the old
stream so fresh SETUP can recover an unknown cipher position. Retried SETUP for
the same stream ID preserves CTR and returns live ports; a new stream ID joins
old workers before installing keys and starting new sockets. Mirror and audio
remote endpoints retain the complete binary IPv4/IPv6 address.
The core keeps the last decoded frame and its dimensions until a new frame
arrives. Sender geometry is stored separately, and pause/reconnect status is
localized for all four application languages.
Secondary mirroring and RAOP media sockets bind to the local address of the
accepted AirPlay control connection. This keeps their TCP listeners and UDP
timing traffic on the same LAN interface when a system proxy or VPN installs a
lower-metric virtual route.
iPhoneMirror's receiver build script reapplies all patches and verifies the
compiled DLL contains the runtime width, height and frame-rate capability
markers before installation. The host only accepts the predefined maximum,
1080p, 720p and 540p profiles exposed by the application, and forwards the
configured receiver name into both DNS-SD registration and the `/info` plist.
iPhoneMirror keeps the v1.1.2 source pin so its custom IPC and capability patches
remain reproducible; selected upstream fixes are reapplied as source
patches during every receiver build rather than replacing the bundled DLL with
an upstream release artifact.
iPhoneMirror starts its own GPL-licensed `iPhoneMirror.WirelessHost.exe`
process, which loads `airplay2dll.dll`; the GPL-3.0-only application and native
capture core exchange decoded frames with that process over a named pipe.

- Project: https://github.com/xenos1337/AirPlayServer
- Version: v1.1.2
- Commit: `34ba6cfd49b2432cf30e89913d66decb775763e4`
- Original release artifact SHA-256:
  `633838f0334ca876ac4a27fbffc7fe359949783fb6f9bdd5f00f25d2f6641d61`
- Corresponding source:
  https://github.com/xenos1337/AirPlayServer/archive/refs/tags/v1.1.2.zip

The receiver includes or derives from the following components:

- AirPlayServer wrapper and UI: MIT (`LICENSE-MIT.txt`).
- PlayFair FairPlay implementation: GPL-3.0 (`LICENSE-PLAYFAIR-GPL-3.0.md`).
- FFmpeg snapshot `63b2b0f47df420007c53888ce0e8383d24b8fb06`: LGPL-2.1-or-later
  (`LICENSE-FFMPEG-LGPL-2.1.txt`). The original DLL identifies itself as
  `N-102062-g63b2b0f47d`, with avcodec 58.137.100; the previous 4.4.2 label was
  incorrect. The replacement preserves that source/ABI and enables H.264,
  ALAC and AAC decoding, the H.264 parser, swscale and swresample. SIMD assembly
  and Windows threading remain enabled. GCC/winpthreads support is linked
  statically; the DLLs require no MSYS2 runtime or developer-installed libraries.
  Build dependency notices are in `NOTICE-FFMPEG-BUILD.txt`.
- Fraunhofer FDK AAC was used by the previous receiver. Its historical notice
  remains in `NOTICE-FDK-AAC.txt`; the current production DLL contains no FDK
  source objects or FDK runtime dependency. The optional test fixture generator
  uses an independently installed FDK encoder and is never packaged.

FFmpeg corresponding source:
https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/63b2b0f47df420007c53888ce0e8383d24b8fb06

Archive SHA-256: `2d6e9244be21b32756c481f4653d7f17ae08608dd2db1ba04e826cf0052df860`.
Rebuild using `scripts/build_airplay_ffmpeg.ps1` and `scripts/ffmpeg-airplay-build.sh`
with MSYS2 UCRT64 (the vendored build used GCC 16.2.0). The checked-in
`scripts/ffmpeg-airplay-mathops.patch` preserves x86 five-bit shift semantics while
making the operand acceptable to current binutils. This is the only source patch.
`scripts/test_airplay_ffmpeg.ps1` verifies H.264 orientation changes, scaling and
ALAC/PCM output against reference decoded bytes. Runtime hashes are pinned in
`SHA256SUMS.txt` and the application's `RuntimeBinaryIntegrity.cs`; update both
after validating a rebuild. Distributors must provide corresponding FFmpeg
sources, this recipe/patch, and the MinGW dependency sources with their binaries.

AirPlay is an Apple protocol and trademark. This is an unofficial compatible
receiver. iPhoneMirror supplies its own Windows DNS-SD compatibility DLL and
does not install or depend on the legacy Bonjour service.

The pinned `dnssd.dll` is rebuilt from `src/WirelessHost/DnsSdShim.cpp`,
`DnsSdRegistrationPolicy.h`, `DnsSdAdvertisementPolicy.h`, and `DnsSdShim.def`
with the CMake target `iPhoneMirror.DnsSdShim`. Per-adapter references to the
same receiver share a native all-interface registration, including Windows
Mobile Hotspot when Ethernet remains the preferred route. The runtime installer
accepts `-DnsSdBinary <rebuilt-dnssd.dll>` to update this DLL, both hash records,
and `BUILD.json` in the same rollback transaction as the receiver and FFmpeg.

`scripts/test_airplay_upstream.ps1` links the actual patched `AirPlayLib.lib`
and decodes encrypted AAC-ELD frames (480 samples, stereo, 44100 Hz), verifies
signal energy/frequency and PCM channel order, and exercises malformed codec
records, PPS lengths over 255, idle pause/resume, TCP FIN/partial-header/payload
recovery, CTR short chunks, retried/new SETUP, unknown-cipher recovery, IPv6,
empty RTP, timing packet bounds, repeated stop and audio restart. Audio probes
also verify live port responses on SETUP retry, rejection of foreign-IP data
and control packets, ordered PCM released by a final control resend without a
later data packet, FLUSH state reset and concurrent stop/destroy with an
in-flight callback. Independent
997 Hz/1501 Hz stereo fixtures detect channel swaps and crosstalk.
The same script compiles the patched `FgAirplayChannel.cpp` and verifies decoder
backpressure during rotation, caller input ending at an inaccessible page,
close/reopen and complete chroma planes at odd/minimum scaled dimensions. Its
optional `-Ffmpeg` argument selects the fixture encoder; otherwise `ffmpeg.exe`
is resolved from PATH. Fixture-generation tools are not production dependencies.
`WirelessReceiverMediaRouteSmoke` verifies pause/reconnect IPC and stream SETUP
through the real DLL and host on a bound LAN interface. The production five-DLL
runtime and both integrity manifests are installed together by
`scripts/install_airplay_runtime.ps1` with rollback on replacement failure.
Build hashes/toolchain metadata in `BUILD.json` and dependency notices are
updated in the same installation transaction. `test_install_airplay_runtime.ps1`
verifies rollback after a late replacement failure and rejects mismatched inputs.

Rebuild and stage the receiver with:

```powershell
./scripts/build_airplay_receiver.ps1 -SourceRoot <v1.1.2-clone> -OutputDirectory <stage>
./scripts/test_airplay_upstream.ps1 -ReceiverRoot <stage> -FfmpegBuildRecord <record.json> -MsysRoot <msys64>
./scripts/install_airplay_runtime.ps1 -ReceiverBinary <stage>/airplay2dll.dll -FfmpegBuildRecord <record.json>
```

The receiver build's `-Install` option also requires `-FfmpegBuildRecord` and
uses this installer, so an AAC-enabled receiver cannot be installed alongside
the previous FFmpeg runtime accidentally.
