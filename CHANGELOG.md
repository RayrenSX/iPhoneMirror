# Changelog

All notable changes to iPhoneMirror are documented here. The project follows
[Semantic Versioning](https://semver.org/) for published releases.

## [Unreleased]

### Changed

- Keep multi-device list order and selection stable across asynchronous usbmux refreshes.
- Stop the previous QuickTime USB session before switching devices and explicitly stop the
  app-owned session during window shutdown.
- Require a visible unplug/reconnect cycle after per-device filter installation.
- Replace separate side-panel start/stop controls with one aligned header action and restore
  the waiting/ready preview states.
- Match detached iPhone and iPad preview corners from ProductType with a conservative
  resolution fallback for unknown future models.

## [0.3.0-preview.1] - 2026-07-11

First public preview.

### Added

- Wired iPhone screen capture over Apple QuickTime Screen Capture USB mode.
- usbmux/Lockdown discovery, trust-state checks and per-UDID device details.
- H.264/CoreMedia parsing, Media Foundation decoding and D3D11 preview.
- 48 kHz stereo system-audio capture and WASAPI playback controls.
- Multi-device discovery and safe capture-session switching.
- Native, 1080p, 720p and 540p local render presets.
- 24, 30, 60 and 120 FPS local presentation limits.
- Full-screen, detached and OBS-friendly preview windows.
- Aspect-ratio locking, rotation, screenshots, shortcuts and live logs.
- Per-device libusb0 filter detection, installation verification and rollback.
- Simplified Chinese and English UI resources.

### Known limitations

- The application and installer helper are not Authenticode-signed yet.
- OBS output currently uses Window Capture rather than a virtual camera.
- The first-time driver path still needs broader clean-machine validation.
- Apple uses a private protocol and may change it in future iOS releases.

[Unreleased]: https://github.com/RayrenSX/iPhoneMirror/compare/v0.3.0-preview.1...HEAD
[0.3.0-preview.1]: https://github.com/RayrenSX/iPhoneMirror/releases/tag/v0.3.0-preview.1
