# Changelog

All notable changes to iPhoneMirror are documented here. The project follows
[Semantic Versioning](https://semver.org/) for published releases.

## [Unreleased]

## [1.0.3] - 2026-07-14

### Fixed

- Unify application prompts with the driver manager dialog style, summarize
  receiver-name and resolution changes in one confirmation, advertise renamed
  receivers in both DNS-SD and AirPlay `/info`, move wireless settings to the
  top for wireless tabs, and add animated device-list drag ordering.
- Add pre-connection AirPlay capability profiles for maximum quality, 1080p,
  720p and 540p. Applying a profile restarts the receiver, prompts connected
  devices before disconnecting them, and gives explicit iPhone reconnection
  instructions so the selected source resolution is renegotiated.
- Replace local render-resolution and frame-rate limit controls with read-only
  actual stream resolution and frame rate when an AirPlay device is selected,
  while preserving those local controls for wired devices.
- Hide the wired A/B/C projection-mode selector as soon as capture startup
  begins, avoiding the brief white disabled-state flash before the active
  session takes ownership.
- Allow long-press drag ordering in the device list while preserving the custom
  order across subsequent USB and AirPlay discovery polls.
- Keep advanced USB settings restricted to experimental AirPlay mode, and
  automatically scroll the newly unlocked settings card into view after the
  fifth footer-version click.
- Select a newly connected AirPlay device once without repeatedly overriding a
  later manual device selection.
- Resolve known wired and wireless ProductType identifiers to readable Apple
  model names, and correct the advanced USB height/width field order.
- Keep the embedded native preview HWND black while switching from an active
  session to an idle device, and hide the airspace child immediately before
  removing the complete HwndHost airspace from idle layout to eliminate the
  white transition frame. A separate dark Popup HWND masks the active-to-idle
  handoff only after DWM has presented it, making the visible switch atomic.
  Its perimeter stays transparent so the original preview border remains
   continuously visible without cross-HWND pixel-rounding mismatch.

## [1.0.1-preview.1] - 2026-07-14

### Changed

- Synchronize the standalone driver manager language with the main application,
  including shared settings, startup language forwarding, English/Chinese
  resource dictionaries and localized operation dialogs.
- Add AirPlay handshake device metadata forwarding and human-readable ProductType
  display in the wireless device panel.

### Fixed

- Restore the wireless AirPlay receiver capability response to 5120x2880 at
  60 fps, preventing iPhone mirroring from being negotiated down to a
  1440-pixel edge and 30 fps after rebuilding the receiver DLL.
- Add a repeatable AirPlay display-capability source patch and post-build binary
  verification so future receiver rebuilds cannot silently regress to the
  upstream lower-resolution profile.

## [1.0.0] - 2026-07-14

### Changed

- Promote the preview line to the first stable iPhoneMirror release.
- Synchronize application, native core, USB client and package versions at
  1.0.0.
- Distribute original iPhoneMirror code under GPL-3.0-only while retaining all
  bundled third-party components under their respective upstream licenses.

## [0.6.0-preview.1] - 2026-07-14

### Added

- Add three per-device wired projection modes: recommended Valeria demo,
  experimental AirPlay adaptive output and fixed 1565×1565 Aisi-compatible output.
- Add compact mode tabs with per-option detail dialogs covering quality,
  status-bar, framing and advanced HPD1 sizing risks.
- Add local-network AirPlay mirroring through an isolated wireless host process.
- Route AirPlay through the existing session API so main preview, render limits,
  audio, screenshots, detached/full-screen windows, simultaneous sessions and
  OBS work the same way as USB sources.
- Add bounded wireless IPC, I420-to-NV12 conversion tests and a host lifecycle
  smoke test that verifies Ready and stop-event handling.
- Add per-device mute controls to detached-window context menus, including a
  multi-device action that mutes every other active window.
- Add an independent `iPhoneMirror.Driver.exe` manager with one-click Apple USB
  support and per-device libusb0 install, repair, uninstall, rollback and logs.
- Add a main-window Driver manager button and strict wired preflight that opens
  the manager when the selected device's driver is missing or unhealthy.

### Changed

- License original iPhoneMirror code under GNU GPL version 3 only. Previous
  releases remain under their original licenses, and third-party components
  continue under their respective upstream licenses.
- Treat the capture driver as an external prerequisite. iPhoneMirror now only
  detects the selected device's capture-driver readiness.
- Update release packaging, SBOM metadata and documentation for the driverless
  application package.
- Treat AirPlay as a first-class source in the device list and use the unified
  Start/Stop action instead of a separate receiver window workflow.

### Removed

- Remove the bundled libusb-win32 driver package, elevated install helper,
  in-app driver installation UI and driver-help window.

## [0.5.0-preview.2] - 2026-07-12

### Fixed

- Preserve the detached window's remove-corners choice across focus, resize and source-size updates.
- Retry advanced USB session replacement after complete QuickTime teardown and verify streaming state
  before reporting the new session as connected.

## [0.5.0-preview.1] - 2026-07-12

### Added

- Add per-device advanced mode, unlocked by clicking the footer version five times, with
  direct QuickTime HPD1 USB resolution requests and immediate session restart.
- Add polished standalone advanced-settings and driver/trust-help windows.
- Add per-device native-resolution probing, runtime orientation renegotiation, and recovery
  rules for persistent low-resolution or black video streams with active audio.
- Add detached-window corner toggles and independent left/right rotation controls.

### Changed

- Move current-device details above the left device list and separate them visually.
- Use the detached preview as the single OBS Window Capture surface and remove the duplicate
  OBS-specific window button.
- Update application, assembly, package and UI versions to 0.5 Preview 1.

### Fixed

- Recover stale QuickTime USB configuration 5 without restarting the application.
- Refresh the top Start/Stop button immediately after a device session changes state.
- Preserve independent multi-device preview, rotation, rendering and advanced USB settings.
- Improve source FPS reporting, orientation handling, rounded preview clipping and log layout.

## [0.3.0-preview.4] - 2026-07-11

### Added

- Add a versioned native multi-session API. Every connected device now owns an
  independent USB capture, decoder, audio state, rendering preferences and status handle.
- Treat the left device list as persistent tabs: selecting another device changes only the
  homepage preview and control target while all other capture sessions remain active.
- Support multiple detached preview windows at once, including simultaneous homepage and
  detached rendering for the same device.
- Add a matching black-and-white context menu to detached windows with always-on-top,
  window lock/unlock and close actions. Detached windows are always on top by default.
- Show background-device capture failures in a dedicated error dialog named for that device.

### Changed

- Route Start/Stop, resolution, frame rate, audio, refresh, screenshot, fullscreen and OBS
  actions to the currently selected device session.
- Preserve each device's resolution limit, target frame rate, audio toggle and volume when
  switching tabs.
- Closing a detached preview now removes only that HWND renderer. The USB capture remains
  active for instant return to its device tab; use the red Stop button to end that session.
- Closing the application still stops and destroys every remaining device session and sends
  the QuickTime shutdown messages to each device.

### Fixed

- Fix the homepage becoming black after switching from the legacy renderer to a device session.
- Fix opening a second detached preview replacing the first device's window.
- Fix closing one device window corrupting or pausing another device session.
- Fix a closed detached window causing its device tab to return in a stopped state.
- Fix the detached-window context menu not opening with a physical mouse right-click on the
  custom borderless frame.
- Make Lock Window disable both moving and resizing while keeping the context menu available.

## [0.3.0-preview.3] - 2026-07-11

### Added

- Add a device-card context menu with **Mirror simultaneously**, allowing another
  connected iPhone or iPad to run in its own isolated USB capture and native preview window.
- Track secondary mirror processes by UDID, prevent duplicate sessions, and close every
  secondary session with the main application.
- Add device-specific display-outline fits for iPhone 12 mini, iPhone 13 mini,
  standard iPhone 12/13 models, and Max variants.

### Fixed

- Disable Apple's `Valeria` demonstration status bar so a mirrored device keeps its real
  time, battery, and carrier instead of displaying January 9 at 9:41.
- Prevent right-clicking another device from changing the active selection and stopping
  the current mirror session.
- Restore the styled device cards after adding the context menu.
- Replace the native light context menu with a readable black-and-white rounded menu.

### Notes

- Secondary simultaneous windows are muted by default to avoid playing two device audio
  streams over one Windows output endpoint.
- Display corner coefficients are visual fits based on Apple product-bezel resources;
  Apple does not publish numeric display-corner radii.

## [0.3.0-preview.2] - 2026-07-11

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

[Unreleased]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.0.3...HEAD
[1.0.3]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.0.1-preview.1...v1.0.3
[1.0.1-preview.1]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.0.0...v1.0.1-preview.1
[1.0.0]: https://github.com/RayrenSX/iPhoneMirror/compare/v0.6.0-preview.1...v1.0.0
[0.6.0-preview.1]: https://github.com/RayrenSX/iPhoneMirror/compare/v0.5.0-preview.2...v0.6.0-preview.1
[0.5.0-preview.2]: https://github.com/RayrenSX/iPhoneMirror/compare/v0.5.0-preview.1...v0.5.0-preview.2
[0.5.0-preview.1]: https://github.com/RayrenSX/iPhoneMirror/compare/v0.3.0-preview.4...v0.5.0-preview.1
[0.3.0-preview.4]: https://github.com/RayrenSX/iPhoneMirror/releases/tag/v0.3.0-preview.4
[0.3.0-preview.3]: https://github.com/RayrenSX/iPhoneMirror/releases/tag/v0.3.0-preview.3
[0.3.0-preview.2]: https://github.com/RayrenSX/iPhoneMirror/releases/tag/v0.3.0-preview.2
[0.3.0-preview.1]: https://github.com/RayrenSX/iPhoneMirror/releases/tag/v0.3.0-preview.1
