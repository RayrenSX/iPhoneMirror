# Build, test, and package iPhoneMirror

[Documentation](README.md) · [简体中文开发指南](../DEVELOPMENT.md)

## Build environment

Use Windows 10 or 11 x64, Visual Studio Build Tools with MSVC, CMake and the Windows SDK, the .NET SDK specified in `global.json`, and Python 3.13 x64. A full build also uses MSYS2 UCRT64 for the optional UxPlay receiver. See the workflow for the exact package list. [Windows build workflow](../../.github/workflows/windows-build.yml)

Run commands from the repository root.

```powershell
./build.ps1 -Configuration Release
./build.ps1 -Configuration Debug -NoPublish
```

The Release build prepares the Python bridge, DDI and native components, then builds, tests and stages the self-contained app under `outputs/iPhoneMirror`. The installer payload is staged under `outputs/iPhoneMirror.Installer`. Use `-OutputRoot` to build into a fresh workspace subdirectory. `-NoPublish` still prepares native dependencies. `-SkipTests` skips the build script's test stage.

The app builds the checked-in Python iUsbBridge source using the pinned recipe. When changing bridge code, keep the standalone Python iUsbBridge repository in sync. The EXE needs its runtime manifest and `_internal` directory beside it.

## Validation

```powershell
python scripts/verify_docs.py
./scripts/verify_localization.ps1
./scripts/verify_localization_surfaces.ps1
ctest --test-dir build/native -C Release --output-on-failure
```

The full build runs native, Python, application-logic and driver tests. WPF runtime tests need an interactive desktop and are skipped on GitHub-hosted CI. Run interactive UI tests serially so they do not compete for focus. Record the device model, OS and connection type when reporting real-device results.

## Packaging

```powershell
./scripts/package_release.ps1 -GenerateSbom
```

The packaging script reads the app version and checks it against Driver Manager and the native core. For pre7, the product version is `1.8.5-pre7` and the managed assembly and file version is `1.8.5.0`. Output includes Setup, the portable ZIP, the optional UxPlay component, SHA-256 hashes, and the SPDX SBOM. `-SkipBuild` requires a matching staged payload.

GitHub Releases builds the experimental CoreDevice decoder before packaging and uses `-RequireCoreDeviceDecoder` to verify that Setup and ZIP contain the same decoder and licenses. To prepare that payload locally, first run `./scripts/build_coredevice_decoder.ps1 -MsysRoot <MSYS2 directory>`.
When packaging an isolated local build, pass its `build.ps1 -OutputRoot` directory to `package_release.ps1 -BuildOutputRoot`.

Verify the final ZIP by extracting it to a separate folder and checking the files, versions, and startup. Verify the final Setup with installation, startup, upgrade, removal, and reinstall in an isolated test environment. Check USB, AirPlay, control and reconnection on suitable devices when those behaviors change. Report the scope of each result without treating a successful build as a device test.

Each release tag contains five files in `docs/releases`. Put the English body in `v<version>.md` and use `.zh-CN`, `.zh-HK`, `.zh-TW`, and `.ja-JP` before `.md` for translations. GitHub Releases displays the English body and asset table with a link to the Simplified Chinese notes. The release metadata carries the authored notes for all five interface languages. The updater selects the current language directly from that response. Older releases use the available original notes when localized notes are unavailable.

[Architecture](ARCHITECTURE.md) · [Contribution guide](../../CONTRIBUTING.md)
