# Compact packaging and optional UxPlay

## Compatibility audit update (2026-10-03, third round)

The media-output compact FFmpeg profile is **withdrawn from default builds**.
The production HTTP audio path failed 9 of 18 reference-generated formats:
24/32-bit PCM, unsigned 8-bit and float64 PCM, AIFF, IMA ADPCM, A-law, WMA and MP2.
The pinned 101,897,728-byte essentials executable is restored. Both normal builds
and packaging now default to it; the expanded custom-profile gate rejects the
known incompatible compact binary. The former 99.82 MB package below is historical,
not a release candidate containing current compatibility fixes.

The restored runtime passed all 18 formats over three rounds. An independent
audio stop/producer race discovered during this test is fixed, with 75 restart
cycles passing. The bridge preflight now executes offline XPC/HID/QUIC/AES/LZFSE
and CA-store checks inside the frozen executable. Packaged UxPlay GStreamer H.264
pixels match the reference and AAC produces non-silent resampled PCM over three
cycles. See `COMPONENT_AUDIT_20261003.md` for current evidence and remaining limits.

## Historical local package (2026-10-03, before compatibility fixes)

The completed `1.8.4-test4` package, produced with the normal build and
`package_release.ps1 -SkipBuild`, contains:

| Artifact | Bytes | Decimal MB |
| --- | ---: | ---: |
| `outputs/releases/iPhoneMirror-Setup-v1.8.4-test4-x64.exe` | 99,821,797 | 99.82 |
| `outputs/releases/iPhoneMirror-v1.8.4-test4-win-x64.zip` | 183,930,101 | 183.93 |
| `outputs/releases/iPhoneMirror-UxPlay-v1.8.4-test4-win-x64.zip` | 70,620,171 | 70.62 |

The optional UxPlay component is separate from the installer. Setup SHA-256:
`3b4735d0515ed0716af100b3d2cfab7574009f1e303c02913bc75d2e1a1e3173`.
All three final files match `outputs/releases/SHA256SUMS.txt`. The default size
target remains advisory, as functional completeness takes priority.

The complete build passed with tests run separately: 136 Python tests, the full
logic suite, seven Wireless/UxPlay/IPC native checks, final AirPlay codec byte
comparisons and actual UxPlay ZIP extraction/hash/runtime checks. The full logic
suite was run directly with `dotnet <test.dll>`: `dotnet run` injects
`DOTNET_ROOT_X64`, which the elevation environment guard intentionally rejects.
The installer regression passed install, upgrade (including closing the running
driver), installed runtime hashes, uninstall with data preservation, and uninstall
with deletion of isolated test data. Test installations were cleaned up.

No external release was uploaded and no SBOM was generated for this local package.
Online UxPlay installation requires publishing the matching component ZIP with
the latest application Release. Real iPhone mirroring, sound, USB control/recovery and
Intel/AMD hardware encoding remain hardware acceptance checks.

## Component behavior

The standard payload excludes UxPlay. Selecting UxPlay and applying wireless
settings opens a separate download window with mirror probing, percentage,
downloaded/total bytes, throughput, cancellation and retry. The existing receiver
continues until the component is ready and the user applies the receiver change.
An unavailable saved UxPlay selection falls back to the original receiver at startup.

`package_uxplay_component.ps1` creates a deterministic ZIP and records its URL,
size, SHA-256 and per-file hashes for packaging validation. `package_release.ps1` publishes this
ZIP alongside Setup, the portable ZIP and checksums; the release workflow expects
all five assets when SBOM generation is enabled. The component ZIP must be uploaded
with that release before users can download it. This implementation does not upload
development assets on its own.

Each download or retry queries the public release list and selects the UxPlay
Windows x64 ZIP from the latest application version, including prereleases.
Drafts and standalone component releases are excluded. If that release has no
matching ZIP, installation fails instead of downloading an older embedded URL.
The GitHub API falls back to `updates/releases.json` when unavailable. The archive
must match the release asset digest or its `SHA256SUMS.txt` entry before installation.
No additional manifest asset is required for existing releases.

The verified ZIP supplies the per-file hashes and is retained under
`Components/UxPlay/Archives/<archive-hash>.zip`. Every explicit install resolves
the current release digest and validates the retained ZIP before rebuilding its
file list; local metadata cannot approve a modified runtime. A valid archive can
repair runtime files without downloading them again. A damaged archive is downloaded
again and must pass the same release checksum verification.

Successful installation atomically saves `installed-component.dat`, protected by
Windows DPAPI for the current user. This protects the offline receipt from file
modification; it is local storage protection, not a publisher signature. Old plain
`installed-component.json` files are not trusted or migrated. Existing caches that
match the application's embedded file hashes remain usable; other legacy caches
are verified again through the download flow.

Current/newer verified components remain usable offline without an automatic
network check. If an application upgrade embeds a newer component requirement,
an older saved component no longer hides it: selecting UxPlay opens the download
flow. A stale release list cannot satisfy that newer requirement. The previous
component is retained if the required release is missing or installation fails.

Downloaded components are verified before extraction into a staging directory,
then moved into `%LOCALAPPDATA%\iPhoneMirror\Components\UxPlay\<archive-hash>`.
Zip traversal, duplicate paths, symlinks, unexpected files, oversized entries and
hash mismatches are rejected. Interrupted installations never receive a ready
receipt. Identical component archives are reused across app versions. Windows may
request firewall access when the downloaded receiver first starts.

Build flags:

- Default: build UxPlay as a separate asset, include its minimum-version and legacy-cache metadata.
- `-IncludeUxPlayRuntime`: also include it in the application for an offline bundle.
- `-OmitUxPlayRuntime`: skip building the optional asset on development machines.
- `-MaximumInstallerBytes`: size target, default **100,000,000 bytes** (exclusive).
  Exceeding it warns by default; working components take priority over size.
  `-EnforceInstallerSizeLimit` explicitly opts into blocking oversized packages.

## Python bridge

The spec follows imports from `usb_touch_bridge.py` instead of collecting every
upstream `pymobiledevice3` service. Lazy tunnel, HID, media authentication and DDI
modules remain explicit. The unused Pygments image formatter is excluded.
`--check-runtime` imports the production lazy dependencies without opening a device;
the packaging script runs it before publishing the bridge.

The locally verified onedir payload decreased from **151.48 MB to 56.78 MB**.
This is unpacked size, not Setup savings. Hardware recovery tests remain necessary
before releasing a newly built bridge.

## Custom FFmpeg build

`build_compact_ffmpeg.ps1` accepts an official `ffmpeg-8.1.2.tar.xz`, its independently
verified SHA-256 and an MSYS2 UCRT64 root. It keeps H.264 CPU/GPU encoding, AAC/Opus,
MP4, RTMP, SRT, WHIP, HLS, DirectShow microphone capture and the application's audio
mixing/playback filters. It disables unrelated codecs and graphical dependencies.

The UCRT64 environment needs GCC, MSYS make, nasm, pkgconf, x264, opus, srt, OpenSSL,
oneVPL, AMF headers and nv-codec headers, including their static libraries.

```powershell
./scripts/build_compact_ffmpeg.ps1 -SourceArchive <official-source-archive> `
    -SourceSha256 <reviewed-source-sha256> -MsysRoot <msys64-root>
./scripts/package_release.ps1 -FfmpegRuntimeManifestPath <generated-runtime-manifest.psd1>
```

The recipe creates a fresh build directory and runs capability checks plus CPU
recording, decoding and Opus smoke tests. The generated manifest pins the resulting
files; its FFmpeg hash is embedded into the application during the normal build.
`prepare_compact_ffmpeg.ps1` verifies the source hash and invalidates cached
binaries when the recipe changes. It is no longer used by the default build:
the compatibility audit rejected the current recipe. Default builds use
`scripts/ffmpeg-runtime-manifest.psd1`. Custom recipes must pass the expanded
capability gate and production audio compatibility tests before adoption.

The compiled media-output executable is **15,531,520 bytes**, down from about
101.9 MB. CPU recording/decoding, audio mixing, FLV/MPEG-TS, playback-rate audio
and local NVIDIA H.264 encoding passed. Intel/AMD hardware and live stream
endpoints still require testing on the corresponding machines/services.

The installer driver remains independently self-contained. Its inner single-file
compression is disabled only for the installer publish, allowing Inno Setup's
solid stream to compress repeated .NET data. The driver target no longer pulls in
unused Windows SDK projections; the standalone portable driver retains its own
compression. Driver regression tests passed.

The last pre-AirPlay-refinement comparison installer was **112,948,409 bytes**.
Earlier Python archive experiments were not adopted. The user prioritizes working
components over the 100 MB target, which is therefore a warning by default.

Verification already completed: focused component tests (including same-size
corruption, nested links and concurrent installation), six dialog renderings,
packaged bridge dependency checks and 136 Python tests. The earlier sandbox and
source-download blockers were resolved. These checks do not replace real-device
mirroring, audio, USB control and recovery acceptance tests before release.

Focused local checks:

```powershell
dotnet run --project src/App.Logic.Tests -c Release -- --uxplay-component
dotnet run --project src/App.Runtime.Tests -c Release -- --component-download-ui work/component-ui
./scripts/test_compact_ffmpeg.ps1 -Ffmpeg <built-ffmpeg.exe>
```

The component tests cover integrity rejection, safe extraction, cancellation,
cache repair, mirror candidates and excluding component ZIPs from app updates.
The UI check renders three languages in both themes without network activity.
For a real component archive, use `--uxplay-package <zip> <descriptor.json> <cache>`
with the logic test runner. It invokes the production extraction and hash checks,
then runs the extracted host's runtime preflight with only Windows on `PATH`.

## AirPlay decoder runtime

The receiver's original libraries actually identify as FFmpeg snapshot
`63b2b0f47df420007c53888ce0e8383d24b8fb06` (avcodec 58.137.100), not 4.4.2.
The same-source rebuild retains H.264/ALAC decoding, H.264 parsing, scaling,
resampling, runtime CPU detection and assembly optimizations. Its four DLLs total
**2,179,072 bytes**, down from **61,633,536 bytes**. The runtime imports only
Windows system DLLs and its bundled FFmpeg DLLs; MinGW support is linked statically.

```powershell
./scripts/build_airplay_ffmpeg.ps1 -SourceArchive <pinned-source.tar.gz> -MsysRoot <msys64-root>
./scripts/test_airplay_ffmpeg.ps1 -BuildRecord <generated-build-record.json> `
    -MsysRoot <msys64-root> -Ffmpeg <reference-ffmpeg.exe> -Ffprobe <reference-ffprobe.exe>
```

The clean rebuild passed H.264 B-frame decoding across landscape/portrait changes,
BGRA scaling, ALAC decoding and interleaved PCM conversion. Decoded video and audio
match reference FFmpeg byte-for-byte. The receiver also passes library loading and
mirror/combined protocol checks. These are software tests, not a substitute for
an iPhone session. Source hash, compiler patch, rebuild instructions and notices
are recorded in `third_party/airplay-server/SOURCE.md`.
