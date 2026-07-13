# Contributing to iPhoneMirror

Thanks for helping improve wired iPhone capture on Windows.

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
- Python 3 for diagnostic scripts

Build, test and publish:

```powershell
./build.ps1 -Configuration Release
```

Core tests only:

```powershell
./build.ps1 -Configuration Debug -NoPublish
```

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
installation, registry filter mutation, signing and rollback behavior now live
outside this application package.

## Style

- C++: C++20, `/W4`, UTF-8, RAII and explicit ownership.
- C#: nullable reference types enabled; keep USB/native work off the WPF UI
  thread.
- Preserve low-latency behavior: do not add unbounded frame or audio queues.
- Keep Simplified Chinese and English resource keys in sync.

By contributing, you agree that your contribution is licensed under the
project's GNU General Public License v3.0 only. By contributing, you agree that
your contribution may be distributed under GPL-3.0-only. Third-party material
remains under its original license.
