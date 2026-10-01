# Contributing to iPhoneMirror

Thanks for helping improve iPhone/iPad mirroring, audio and output workflows on Windows.

## Before opening an issue

- Use the latest published preview or current `main` build.
- Search existing issues first.
- Remove UDIDs, device names, pairing records, account information and personal
  screen content from logs and screenshots.
- Never upload an unredacted USB capture. Protocol captures can contain stable
  device identifiers and private application data.
- Report security-sensitive driver, elevation or memory-safety problems through
  [GitHub private vulnerability reporting](https://github.com/RayrenSX/iPhoneMirror/security/advisories/new).

## Development environment

- Windows 10/11 x64
- Visual Studio 2026 Build Tools with MSVC, Windows SDK and CMake
- .NET 10 SDK with Windows Desktop support
- MSYS2 UCRT64 with CMake, Ninja, the UCRT64 toolchain, GStreamer (base, good,
  bad, libav), libplist and OpenSSL for the bundled UxPlay fallback
- Python 3.13 x64 for the versioned USB bridge packaging recipe and diagnostics

See the [development and testing guide](docs/DEVELOPMENT.md) for dependency sources,
build switches, artifact layouts, and focused tests, and the [documentation index](docs/README.md)
for maintained references versus historical audit reports.

Build, test and publish:

```powershell
./build.ps1 -Configuration Release
```

Build without publishing the self-contained app:

```powershell
./build.ps1 -Configuration Debug -NoPublish
```

The default build runs native CTest, Python bridge tests, bridge source-staging
checks, application-logic, WPF runtime, driver-installer, Visual C++ runtime and
Apple-support validation. `CI=true` explicitly skips the WPF runtime suite;
local runs need an interactive desktop. Localization is a separate workflow step.
For media-output or wireless changes, also run the focused
smoke scripts under `scripts/` or the loopback lab in `tools/srs-lab`.

Real-device USB configuration/bulk probes are disabled by default
(`IPHONEMIRROR_BUILD_DANGEROUS_USB_TOOLS=OFF`). Enable them only on a dedicated
test device after reviewing the command and its recovery procedure.

Localization verification:

```powershell
./scripts/verify_localization.ps1
```

## Pull requests

1. Keep each pull request focused on one problem.
2. Explain the protocol, driver, rendering or UI behavior that changed.
3. Add or update tests when parsing or state-machine behavior changes.
4. Run the Release build before requesting review.
5. For UI changes, attach screenshots containing no personal phone content.
6. For real-device changes, state the ProductType and iOS version but do not
   publish the UDID.

Changes to external-driver detection, USB activation/stop sequencing, vendored
native libraries or third-party licensing require extra review. Driver
installation, registry filter mutation and rollback behavior live in
`src/DriverInstaller`, a separate executable shipped with the main application.

## Style

- C++: C++20, `/W4`, UTF-8, RAII and explicit ownership.
- C#: nullable reference types enabled; keep USB/native work off the WPF UI
  thread.
- Preserve low-latency behavior: do not add unbounded frame or audio queues.
- Keep Simplified Chinese, Traditional Chinese (Hong Kong), and English
  resource keys in sync.
- Update the README, user guide, architecture/protocol notes, or release notes
  when a user-visible workflow, dependency, port, or output format changes.

By contributing, you agree that your contribution may be distributed under the
project's GNU General Public License v3.0 only (GPL-3.0-only). Third-party material
remains under its original license.
