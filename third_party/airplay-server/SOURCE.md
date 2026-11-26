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
peer to reconnect for 2 seconds after the socket reports transport loss, including while paused.
On supported Windows versions the accepted video socket starts TCP keepalive after
2 seconds of inactivity and waits for 3 unanswered probes at 1-second intervals.
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
localized through the application's shared language resources.
Secondary mirroring and RAOP media sockets bind to the local address of the
accepted AirPlay control connection. This keeps their TCP listeners and UDP
timing traffic on the same LAN interface when a system proxy or VPN installs a
lower-metric virtual route.
iPhoneMirror's receiver build script reapplies all patches and verifies the
compiled DLL contains the runtime width, height and frame-rate capability
markers before installation. The host validates predefined profiles, including
square original-quality profiles derived from the bundled Apple panel catalog,
1080p, 720p and 540p, and retains the legacy 5120x2880 fallback. It forwards the
configured receiver name into both DNS-SD registration and the `/info` plist.
For controlled codec-negotiation comparisons, the final source recipe
`patches/Apply-DiagnosticMirrorCodecPatch.ps1` recognizes the exact process
environment value `IPHONE_MIRROR_DIAGNOSTIC_MIRROR_AVC_ONLY=1`. It clears only
the screen-multicodec feature bit 42 on the mirror/combined receiver, using
`patches/airplay-mirror-codec-policy.h` for the global feature word, both DNS-SD
TXT service profiles and the `/info` response. The flag is read through Win32
environment APIs so separate DLL CRT caches cannot disagree. The default
multicodec advertisement and HEVC decoding remain available, and the dedicated
URL-media receiver retains its existing capabilities. Other flag values do not
activate the diagnostic. The flag does not resize decoded pixels or guarantee
that iOS selects panel-native dimensions.

For an isolated phone-speaker experiment, the same recipe recognizes
`IPHONE_MIRROR_DIAGNOSTIC_MIRROR_NO_AUDIO=1`. It clears AirPlay audio feature
bit 9 in the mirror/combined DNS-SD records and the `/info` and `/server-info`
responses. The host assigns a separate receiver identity so iOS can discover
the experimental capabilities independently. The default and dedicated
URL-media capability profiles retain audio. The flag only changes the
advertised capability and does not force the sender's playback route.
Run `scripts/test_airplay_phone_audio.ps1 -Receiver <staged host executable>`
with the diagnostic host and DLL in the same runtime folder to count video
frames and audio packets without storing media or sender identifiers.
In the 2026-11-23 iPhone trial the phone displayed a pending connection while
the receiver counted 1,523 video frames and zero audio packets. The session
disconnected after 60.7 seconds from the first frame. The phone's local
speaker output was not verified.

The same day, the full iPhoneMirror app rendered the experimental AirPlay
stream at 1326×2880. The session reported 3,139 video frames and zero audio
packets. The user confirmed that the Windows preview and iPhone speaker both
worked during this session. The receiver reported disconnection 58.6 seconds after the first
published frame. The UI experiment used a build-time hash manifest for the
diagnostic receiver DLL and DNS-SD shim. Other runtime hashes were checked
normally.

`IPHONE_MIRROR_DIAGNOSTIC_MIRROR_AUDIO_EVENT=1` starts a per-session AirPlay
event channel for both mirror variants. Combined with the no-audio flag, it
advertises video-only capabilities from the start and sends an explicit
`updateInfo` on the event channel. Used alone, it starts with the ordinary
audio advertisement and sends an audio capability withdrawal 15 seconds
after the event channel connects. The second variant follows the [UxPlay
fork's event-channel experiment](https://github.com/erseco/UxPlay/commit/ab52665ec306f07307af21b797f1dfcdd9283065).
The fork demonstrated sustained mirroring on macOS and reported that audio
still needed a manual route change. Verify picture, connection duration and
speaker playback independently on iPhone before presenting either variant as
a supported audio route.

The first iPhone test with the event channel enabled from a video-only start
received successful replies to `updateInfo`. iPhoneMirror rendered 3,069
frames and received zero audio packets. The sender still closed its video
socket 58.6 seconds after the first published frame.

Starting with audio enabled kept the preview running for 110.6 seconds. The
iPhone acknowledged the update after 15 seconds and continued sending audio
to Windows. Selecting a different route on the iPhone ended the mirror session.
For the next diagnostic, `IPHONE_MIRROR_DIAGNOSTIC_WITHDRAW_RAOP=1` also
withdraws the `_raop._tcp` advertisement and updates the `_airplay._tcp` TXT
features after the downgrade is acknowledged. The receiver restores the
initial advertisement when the event connection closes. This follows the
[UxPlay fork's DNS-SD experiment](https://github.com/erseco/UxPlay/commit/ada57e97).
The Windows protocol smoke checks both withdrawal and restoration with
`--withdraw-raop-event`. In the physical iPhone test, the receiver updated
discovery after 15 seconds and continued rendering for at least 357 seconds
before the app was closed for the next trial. The phone kept sending audio
packets and the user confirmed Windows playback.

The early video-only trial cleared one audio feature bit but `/info` still
listed audio formats and latencies. A stricter mask (`0x1A43F4E6`) passed
loopback negotiation, but the iPhone did not show the receiver in the screen
mirroring list. The updated diagnostic keeps the earlier discovery mask.
Setting both
`IPHONE_MIRROR_DIAGNOSTIC_MIRROR_NO_AUDIO=1` and
`IPHONE_MIRROR_DIAGNOSTIC_VIDEO_ONLY_CAPABILITIES=1` removes those fields and
keeps the same feature mask across DNS-SD, `/server-info`, `/info` and `updateInfo`.
This diagnostic has its own receiver identity. The media-only receiver uses
its regular profile. `--video-only-capabilities` checks the stricter handshake.
With the restored discovery mask, the iPhone found the receiver and the user
confirmed iPhone speaker playback while iPhoneMirror rendered the video. The
receiver counted 2,832 video frames and zero audio packets. The iPhone still
sent TEARDOWN about 58.55 seconds after the first published frame, shortly
after a successful `/feedback` response. The sender-initiated timeout remained
with the audio format and latency entries removed.

### AirBridge comparison

[AirBridge 0.6.2](https://github.com/alcxandros/AirBridge/releases/tag/v0.6.2)
reports iOS 26 picture on the PC with sound on the phone. The release notes
do not give a session duration. Its [UxPlay patch](https://github.com/alcxandros/AirBridge/blob/v0.6.2/patches/0001-noaudiofeat.patch)
clears feature bit 9 and disables the local audio renderer.

A Windows loopback probe of the exact release receiver (UxPlay
`05b0275cb1b726592700e47a6e0307544b3d3a52`) returned feature mask `0x527FFCE6`
with the audio format and latency arrays still present. The profile differs
from the earlier iPhoneMirror video-only mask `0x4005A7FFCE6` in bits 27 and
42. The reference also publishes both AirPlay and RAOP discovery records
and returns the UDP timing port in the initial `SETUP`. These differences
are candidates for controlled comparison. The upstream [UxPlay issue](https://github.com/FDH2/UxPlay/issues/303)
records the one-minute teardown on earlier builds with audio support disabled.
The exact release receiver can listen independently of iPhoneMirror using a
different base port. In the test environment Windows created two inbound
block rules for this executable on the Public network profile. Loopback
verification passed. A LAN test requires temporary access for the reference
receiver and a physical iPhone session longer than the prior timeout.
After the test receiver was allowed through the firewall, the iPhone initially
did not list it. The receiver's own mDNS process had no UDP socket on port 5353.
An independent probe then obtained the actual `_airplay` and `_raop` TXT
payloads from the reference receiver and registered both records with the
Windows DNS-SD API. Windows acknowledged registration on the Ethernet
interface and again with the all-interface policy used by iPhoneMirror.
A separate Windows DNS-SD browse returned both the AirBridge test receiver
and iPhoneMirror. Resolving the test receiver returned the active LAN address
and TCP port 17101. A local TCP connection to that address succeeded.

With Windows discovery active, the same iPhone (iOS 27.2.1) connected to the
reference receiver over IPv6. The reference presented H.264 video at 498×1080
and the user confirmed sound came from the iPhone speaker. The session closed
approximately 64 seconds after the first video pipeline log. The user confirmed
the disconnect occurred without touching the mirroring controls. The reference
was stopped and its two Public-profile inbound firewall rules were restored to
Block. The AirBridge 0.6.2 result therefore reproduces the short video-only
session on this phone, despite the difference in receiver implementation.

In a repeat iPhoneMirror trial on iOS 27.2.1, the video-only receiver
published 3,417 frames and received zero audio packets before the phone
closed the video socket 58.580 seconds after the first frame. The receiver
acknowledged `/feedback` with `200 OK` 286 ms before the close. The iPhone
then sent TEARDOWN on the control connection. A bounded USB activity-stream
probe covered the session and returned logs, but the AirPlay filter found no
sender-side event identifying the reason for the close. This confirms the
sequence on the wire. It does not establish an internal iOS timeout cause.

For a separate transport, the same iPhone advertised CoreDevice DisplayService
and answered its media capability query over the existing trusted USB
connection. Apple device enumeration listed only the USB connection. A direct
LAN probe reached lockdownd but the service did not accept the existing USB
pairing record for the Wi-Fi session. The probe did not attempt new pairing or
start media streaming. Wireless CoreDevice video and local speaker playback
remain unverified.

Each accepted mirror codec-configuration update also emits numeric diagnostic
event 33: actual codec (`0` AVC, `1` HEVC), AVC-only diagnostic enabled (`0`/`1`),
source width, source height, output width and output height. This reports what
the sender negotiated rather than inferring codec or resolution from a UI
quality label. The event contains no media payload, keys or device identity.
Diagnostic event 34 records six integers per receive window: compressed frame
count, receiver elapsed milliseconds, sender timestamp span in milliseconds,
count of sender timestamp gaps over 25 ms, total synchronous decode/callback
milliseconds, and peak decode/callback milliseconds. The host reports its
decoded callback and IPC counters beside the same event. Only timestamp
differences are logged.
The new recipe and shared header are recorded in `BUILD.json` AdditionalPatches
when installing a rebuilt receiver.

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
  HEVC, ALAC and AAC decoding, the H.264/HEVC parsers, swscale and swresample. SIMD assembly
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
The equivalent commit-pinned GitHub API tarball is also accepted, SHA-256
`06f221fe8d4d7b2a3416f19641a472c0cce836df38110bb2c093b7528a7065fc`.
Rebuild using `scripts/build_airplay_ffmpeg.ps1` and `scripts/ffmpeg-airplay-build.sh`
with MSYS2 UCRT64 (the vendored build used GCC 16.2.0). The checked-in
`scripts/ffmpeg-airplay-mathops.patch` preserves x86 five-bit shift semantics while
making the operand acceptable to current binutils. This is the only source patch.
`scripts/test_airplay_ffmpeg.ps1` verifies H.264/HEVC orientation changes, scaling and
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

The request-policy patch also accepts exact POST pairing routes over RTSP/1.0
without CSeq, while retaining signature verification and CSeq requirements for
session requests. Empty HTTP 200 responses carry an explicit zero content length.
Numeric request/response diagnostics record route identifiers and lengths only.

Combined mode provides screen mirroring and a separate DLNA service. Its AirPlay
and RAOP advertisements and `/info` deliberately omit URL-video handoff bits:
the shared RTSP endpoint does not implement the complete server-info/reverse/play
handoff protocol. Advertising those bits caused YouTube to leave screen mirroring
for an unsupported path. Dedicated media mode retains its existing feature policy.
On 2026-11-16, the iPhone 17 Original/native-profile physical test confirmed YouTube
picture, stereo sound and repeated portrait/landscape transitions without disconnect
after this policy correction. HEVC support separately restored the native-profile
home screen that was previously black.

The application now launches separate mirror and media hosts, with independent
identities, IPC queues and lifecycles. Mirror ports are 5001/7001; video ports are
5011/7011 with DLNA HTTP on 8090. The media host excludes Screen, screen multicodec,
and protected-video capability bits, omits displays from `/info`, and rejects
screen SETUP streams. Physical testing confirmed its removal from the iOS Screen
Mirroring list. Text, XML and binary direct Play requests and independent host
stop/restart are covered by `scripts/test_receiver_isolation.py`.

The earlier physical test (2026-11-16) stopped at YouTube's 120-byte `/fp-setup2`
request, with no fallback after a 421 response. The dedicated video HTTP endpoint
now integrates the pinned go-atv SAP/control implementation through
`iPhoneMirror.AirPlayControl.dll`; see `../airplay-control/SOURCE.md` for source,
license and rebuild instructions. RAOP keeps rejecting unsupported fp-setup2.
Native integration tests cover ordinary and pair-secret-mixed negotiation,
coalesced upgrade/first encrypted command, fragmented records, encrypted `/play`
delivery into the media queue, and closure on authentication failure. Independent
mirror startup/stop/restart tests still pass. Physical YouTube playback with this
new implementation remains to be tested; protocol success is not playback success.

Subsequent physical testing confirmed encrypted `/play` and continuing playback
status requests after preserving X-Apple-PD from pair-verify. The host then rejected
the supplied non-HTTP location before starting playback. The video HTTP endpoint
now bridges `mlhls://` resources through encrypted FCUP `/event` requests and
`/action` responses. A tokenized loopback HTTP service rewrites playlist references
while leaving HTTP(S) segment URLs intact. Native integration covers both encrypted
reverse acknowledgments and master/media playlist delivery to the media queue.
Physical YouTube playback with the playlist bridge is pending validation.

The URL-video scrub route now distinguishes progress reads from seeks. GET
`/scrub` returns the current duration and position as `text/parameters`; only
POST with a single valid, finite, nonnegative `position` query value may seek.
`patches/airplay-scrub-request.h` contains the bounded, locale-independent query
parser shared by the AirPlay and RAOP URL-control branches. The AirPlay branch
checks the active media owner before either read or write. Diagnostic event 32
records numeric method, parse outcome, target milliseconds, callback application,
ownership and endpoint kind without logging request URLs or bodies. Invalid
requests cannot be silently converted into a real seek to zero; valid zero
remains supported. This fixes a reproduced parser defect, while the provenance
of earlier physical zero seeks remains unconfirmed without the new diagnostics.

Rebuild and stage the receiver with (replace each quoted placeholder with its actual path):

```powershell
./scripts/build_airplay_receiver.ps1 -SourceRoot '<v1.1.2-clone>' -OutputDirectory '<stage>'
./scripts/build_airplay_control.ps1 -OutputDirectory '<control-stage>'
./scripts/test_airplay_upstream.ps1 -ReceiverRoot '<stage>' -FfmpegBuildRecord '<record.json>' -MsysRoot '<msys64>'
./scripts/install_airplay_runtime.ps1 -ReceiverBinary '<stage>/airplay2dll.dll' -FfmpegBuildRecord '<record.json>' -ControlBinary '<control-stage>/iPhoneMirror.AirPlayControl.dll'
```

The receiver build's `-Install` option also requires `-FfmpegBuildRecord` and
uses this installer, so an AAC-enabled receiver cannot be installed alongside
the previous FFmpeg runtime accidentally.
