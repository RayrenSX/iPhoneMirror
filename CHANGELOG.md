# Changelog

All notable changes to iPhoneMirror are documented here. The project follows
[Semantic Versioning](https://semver.org/) for published releases.

## [Unreleased]

## [1.5.2] - 2026-07-31

### Added

- Add an STA/WPF runtime test that loads application resources and constructs
  the compiled update window from a release fixture, covering failures that
  XAML compilation and source-text checks cannot detect.
- Add a static guard that rejects direct `RenderTransform` values and property
  elements on every application and driver-manager child window.

### Changed

- Reuse the shared inner-content page transition for the update window instead
  of maintaining a separate window-level entrance animation.
- Run the application runtime test as part of the standard `build.ps1` test
  sequence and therefore on the Windows GitHub Actions build.

### Fixed

- Fix manual and automatic update checks fetching a release successfully but
  then reporting `RenderTransform` failure when WPF tried to construct the
  update window. WPF does not allow a render transform directly on `Window`.
- Make the update progress value binding explicitly one-way so XAML loading
  does not try to write through the view model's read-only public property.

## [1.5.1] - 2026-07-31

### Added

- Add a reusable child-window drag behavior to the main application so the
  title regions in advanced, image, projection, media-output, update, startup
  error and USB-mode windows can move the window consistently. Resizable
  windows also support title-region double-click maximize and restore.
- Add shared pressed-state scrollbar resources for both light and dark themes.

### Changed

- Refine the main navigation as a compact overlay rail with a 48-pixel icon
  column, restrained active indicator, consistent icon sizing and a narrower
  208-pixel expanded pane that no longer repeats the product title.
- Move the complete vertical and horizontal scrollbar templates into SharedUI
  so the main application and driver manager use the same thin rounded track,
  hover expansion, dragging feedback and page-direction commands.
- Rebalance dark-mode Mica surfaces, cards, dialogs, controls and combo boxes
  around neutral translucent grays for clearer layering without heavy blocks.
- Use the lightweight control-fill surface for ordinary workspace actions and
  refresh controls, keeping semantic action colors reserved for start, stop,
  warning and error states.

### Fixed

- Keep compact navigation labels collapsed without mixing pane-width and label
  animations, preventing layout shifts while the overlay pane opens or closes.
- Preserve correct horizontal scrollbar direction, thumb sizing and pressed
  feedback when the shared template is used outside the main application.
- Make custom child-window title regions draggable instead of limiting window
  movement to a small or inconsistent hit target.

## [1.5.0] - 2026-07-30

### Added

- Add a compact navigation rail and animated workspace panels for mirroring,
  device selection, output controls and settings, with preview actions kept
  close to the active video surface.
- Add shared light/dark theme dictionaries, reusable modern window controls and
  consistent custom title bars across the main app and driver manager.
- Recognize libusb0 `set_configuration` failures and show localized recovery
  guidance to reconnect the cable, restart the iPhone/iPad and try another
  Apple original or MFi-certified cable.

### Changed

- Redesign About, update settings and diagnostics as lightweight, unframed tab
  pages with consistent spacing, typography and a resizable live-log area.
- Restrict ordinary controls and selection states to a black, gray and white
  palette. The top-right start/stop mirroring action and semantic warnings keep
  color where it communicates state or required attention.
- Make the idle preview surface theme-aware: light mode now uses a light canvas
  with dark high-contrast text and device glyphs, while dark mode retains its
  dark preview treatment.
- Replace system title bars on child windows with the shared close-button and
  rounded-hover treatment, and enlarge the three wired-mode guidance pages so
  localized instructions remain visible and scrollable.
- Reorganize independent preview context menus into window, display and audio
  groups and keep labels synchronized after language or state changes.

### Fixed

- Apply the same DWM border color, rounded-corner and maximize frame policy to
  the driver manager as the main application.
- Stop UAC cancellation during Apple support MSI installation or service
  recovery from falling through to a misleading generic installation failure.
- Remove the hard-coded black main-preview background that made the light theme
  inconsistent and reduced placeholder contrast.

## [1.4.4] - 2026-07-29

### Added

- Ask users to report the iPhone/iPad trust-prompt state after Apple USB,
  parent-driver, and capture-filter changes, with explicit choices for trusted,
  previously trusted, or not yet handled states.
- Show a dedicated no-PING recovery window that gives device restart and an
  Apple original or MFi-certified cable strong visual priority.

### Fixed

- Skip the Microsoft Store reinstall when a trusted Apple USB INF is already
  present but Apple Mobile Device Service is missing, and proceed directly to
  the signed Apple desktop compatibility package.
- Extract and install only Apple's signed `AppleMobileDeviceSupport64.msi`
  from the official package instead of installing the complete iTunes desktop
  application, with Authenticode, SHA256 and file-lock verification before
  elevation.
- Replace the three-minute, twice-per-second Apple support status log flood
  with concise wait summaries, download percentage, and visible install and
  verification phases in the driver manager.
- Stop immediately when service-start elevation is cancelled, report Windows
  Installer reboot-required exit codes, link failures to the verbose MSI log,
  and include that log in the application's cleanup workflow.

## [1.4.3] - 2026-07-29

### Added

- Install Apple Devices non-interactively from its pinned Microsoft Store
  product ID through `winget` when Apple USB support is completely absent.
- Add an explicit release-build option for embedding an authorized, Apple-signed
  `AppleMobileDeviceSupport64.msi`; the driver manager installs this offline
  payload before trying any network source.
- Audit the Apple USB driver package separately from Apple Mobile Device
  Service, recognizing modern `appleusb.inf` and legacy `usbaapl64.inf` /
  `usbaapl.inf` packages before reporting the wired environment as ready.

### Changed

- Keep the Apple-signed desktop iTunes package as an official HTTPS fallback
  when Microsoft Store or `winget` is unavailable, while continuing to prefer
  a trusted offline AppleMobileDeviceSupport MSI when supplied by the user.

### Fixed

- Make the virtual-camera timeline stress test accept intentional real-time
  catch-up gaps while still rejecting overlapping or non-monotonic samples.
- Accept Microsoft VC Runtime file versions with runner-specific suffixes such
  as `14.29.30157.0 built by: cloudtest`, fixing the Windows GitHub Actions
  publish failure while retaining Microsoft signature and copy verification.

## [1.4.2] - 2026-07-29

### Added

- Add a process-wide managed diagnostic logger at
  `%LOCALAPPDATA%\iPhoneMirror\Logs\application.log`, available before native
  initialization and after native shutdown, with structured timestamps,
  process/thread context, exception type, HRESULT, source and sanitized detail.
- Capture WPF dispatcher, AppDomain and unobserved task exceptions in both the
  main app and driver manager, and persist handled failures from updates,
  settings, media output, virtual camera, wireless probing and cleanup paths.
- Add a Diagnostics tab to About with the log directory, an open-folder action,
  retention information and one-click cleanup for app/driver logs and update
  downloads.
- Write startup failures, environment details and the native-runtime inventory
  to `%LOCALAPPDATA%\iPhoneMirror\Logs\startup.log`, and show a project-styled
  recovery window with the log location instead of terminating silently.

### Changed

- Store native capture diagnostics at
  `%LOCALAPPDATA%\iPhoneMirror\Logs\capture.log` instead of the temporary
  directory, and persist one-click updater Setup logs beside it.
- Rotate managed logs at 8 MB, retain four archives, cap the combined log
  directory at 64 MB, remove files older than 14 days and retain a bounded TEMP
  fallback log when LocalAppData is unavailable.
- Validate required native files before core initialization and include file
  size/version details plus optional wireless, FFmpeg and virtual-camera
  components in startup crash diagnostics.

### Fixed

- Stop silently discarding failures while loading/saving settings, probing
  optional runtimes, rolling back sessions, finalizing recordings, cleaning
  helper processes and checking background capture sessions.
- Bundle the signed, hash-pinned x64 `libusb0.dll` user-mode runtime beside the
  native core. The app now starts on a clean Windows machine before the capture
  filter driver is installed, so users can reach the built-in driver manager.
- Detect the wired QuickTime `no PING` handshake timeout and show a localized,
  actionable recovery prompt that asks the user to restart the iPhone and retry
  with an Apple original or MFi-certified cable while keeping the phone unlocked.
- Include signed app-local copies of `VCRUNTIME140.dll`,
  `VCRUNTIME140_1.dll` and `MSVCP140.dll` in both the Setup and portable ZIP.
  This fixes the startup `DllNotFoundException` at `im_initialize()` on clean
  Windows systems without the Visual C++ Redistributable already installed.
- Place the same runtime beside `iPhoneMirror.WirelessHost.exe`, so wireless
  mirroring does not depend on a machine-wide Visual C++ installation either.
- Require all six runtime copies in publish, packaging and installer upgrade
  checks, preventing a future release from silently omitting them.

## [1.4.1] - 2026-07-29

### Fixed

- Suppress critical-error dialogs in the main process before any wireless child
  process starts so the setting is inherited during process initialization.
- Validate every bundled AirPlay/FFmpeg image with a non-executing `SEC_IMAGE`
  mapping before resolving DLL dependencies. Windows code-integrity failures now
  return through the in-app diagnostic path without displaying the misleading
  `avutil-56.dll` Bad Image dialog.
- Add an invalid-image preflight test that verifies malformed runtime images fail
  silently with the expected diagnostic exit code.

## [1.4.0] - 2026-07-28

### Added

- Add a standard x64 Windows Setup executable with a selectable installation
  directory, Program Files default, registered uninstall entry, localized Start
  menu shortcuts, optional desktop shortcut and a stable AppUserModelID.
- Add an independent GitHub Release updater with semantic-version comparison,
  stable and Beta channels, startup/manual checks, installer-first asset
  selection, streamed progress and optional automatic download.
- Add SHA256 verification from `SHA256SUMS.txt`, interrupted-download cleanup,
  retry support, automatic installer launch, in-place upgrades and a privileged
  ZIP fallback when a release does not contain a Setup executable.
- Add Fluent-style About, update settings and update windows with Markdown
  release-note rendering, Mica/rounded-window integration, entrance animation,
  download percentage and transfer-speed reporting.
- Add system, dark and light themes and expose version, GitHub, changelog,
  license and manual update actions from the About page.

### Changed

- Include `CHANGELOG.md` and the ZIP update helper in both installed and portable
  distributions, and include the Setup executable in release checksums.
- Pin and hash-verify Inno Setup 6.7.3 and its official Simplified Chinese
  translation for reproducible bilingual installer builds.
- Wait for the old application process to exit before the ZIP updater replaces
  files, then relaunch the upgraded executable.

### Fixed

- Keep startup update failures isolated from application startup and show
  actionable inline errors for network failures, timeouts and corrupt downloads.
- Preserve user configuration and downloaded updates by default on uninstall,
  while prompting users before deleting those files.
- Prevent unrelated utility executables from being selected as update installers;
  only assets explicitly named as Setup/Installer executables take precedence.
- Replace the stale preview footer with the assembly-derived `v1.4.0` version.
- Preflight the AirPlay/FFmpeg runtime without system dialogs, recognize Windows
  code-integrity errors such as `0xc0e90002`, show actionable Setup/ZIP guidance,
  and back off retries instead of repeatedly displaying a misleading Bad Image
  dialog.
- Update every theme-managed brush dynamically so light/dark changes apply to
  existing windows, while keeping fixed high-contrast text on the black preview
  surface.
- Add an isolated installer test that verifies a simulated `1.3.0` to `1.4.0`
  upgrade, uninstall registration and shortcuts, retained user data, and explicit
  user-data deletion without touching the production installation.

## [1.3.0] - 2026-07-28

### Added

- Add built-in MP4 recording, RTMP and SRT publishing, WebRTC/WHIP publishing,
  and a Windows 11 Media Foundation virtual camera backed by the active
  projection session.
- Add a session-bound mirroring-settings window and a matching detached-window
  context-menu command, while retaining decoder and image controls for wireless
  sessions without showing unsupported resolution or frame-rate controls.
- Add a loopback-only Stream Lab with automatic SRS/MediaMTX backend selection,
  RTMP, SRT, WHIP/WHEP and browser virtual-camera verification at `tools/srs-lab`.

### Changed

- Let screenshots prompt for a destination and let recording start immediately,
  then prompt for its destination after MP4 finalization.
- Bundle and hash-verify an FFmpeg 8.1.2 runtime used by recording and live
  video output.

### Fixed

- Bind image-adjustment windows to the exact native session that opened them,
  close them on session replacement, and prevent normal window close when the
  pre-edit values cannot be restored.
- Serialize image and video settings operations while keeping image editing
  modeless, so concurrent settings surfaces cannot mutate the same transaction.
- Drive decoder status text and indicator color from native applied, pending,
  failed and actual-runtime state instead of displaying a fixed success marker
  or leaving wireless sessions at detecting.
- Start media output from the newest buffered PCM packet instead of replaying
  stale native audio, preventing several seconds of initial audio/video skew.
- Keep video recording and live output running through PCM interruptions by
  inserting clocked silence, then discard late audio backlog when PCM resumes.
- Treat FFmpeg finalization timeouts and non-zero exit codes as failures, kill
  stalled process trees, and never report a potentially damaged MP4 as saved.
- Write recordings to `.partial.mp4` staging files and promote them only after
  successful FFmpeg finalization, so crash remnants are not offered as complete
  recordings after restart.

## [1.2.2] - 2026-07-28

### Added

- Add per-device brightness, contrast, saturation and gamma controls in a
  dedicated image-adjustment window. Slider changes preview immediately;
  Save commits them, while Back, Escape or closing the window restores the
  values that were active when it opened.
- Add an Adjust image command to each detached device-preview context menu.
  The adjustment window is modeless and remains usable alongside the main
  window and other preview windows.
- Add explicit decoder request, pending, active and fallback diagnostics in
  the status bar below the preview, including the actual hardware or software
  decoder selected by Media Foundation.
- Add native C API coverage and device-isolated application-logic coverage for
  image controls, decoder switching and localization resource integrity.

### Changed

- Remove the HDR output selector from the application because the upstream
  mirroring driver does not provide an HDR mirror stream. The local renderer
  now consistently requests SDR output and retains deterministic tone mapping
  for any HDR-tagged input encountered through a compatible source.
- Keep the main video settings focused on resolution, frame rate and decoder.
  Decoder changes are submitted only by Apply video settings; a failed live
  switch can offer an explicit, user-confirmed device reconnect.
- Freeze per-device session-start settings into immutable snapshots so device
  selection or edits in another window cannot mix settings during concurrent
  session creation.

### Fixed

- Fix decoder selection being reported as applied before the native decoder
  committed the change or reached the next keyframe.
- Fix multi-device settings leaking between devices, including new devices
  inheriting the previously selected device's controls and saved non-preset
  values being overwritten while switching selection.
- Fix partial video-setting failures being recorded as a complete success.
  Render limits, image controls and decoder state now commit independently
  according to their native results.
- Fix image-adjustment dialogs blocking the main window or being hidden behind
  always-on-top detached previews.
- Fix duplicate localization keys that could pass compilation but crash WPF
  during application startup.

### Verification

- Pass native protocol, output-mode, wireless-host, application-logic and
  driver-installer tests.
- Pass Release builds for the WPF application and driver manager with zero
  warnings and zero errors.
- Pass Windows UI smoke checks for the main settings layout, modeless image
  adjustment window and minimum-size control alignment.

## [1.2.1] - 2026-07-27

### Added

- Add HDR-aware AVC/HEVC decoding with `avc1`, `avc3`, `hvc1` and `hev1`
  sample-entry support, 8-bit NV12 and 10-bit P010 output, BT.601/709/2020 and
  Display P3 color metadata, PQ/HLG transfer handling, HDR-to-SDR tone mapping
  and FP16 scRGB output on HDR-enabled displays.
- Add per-device decoder and color-output policies. Automatic, hardware-first
  and software-compatibility modes select Media Foundation transforms and
  recover through deterministic fallback when configuration or runtime decode
  fails.
- Add simultaneous wired-device sessions with independent capture, decode,
  audio, preview and shutdown ownership. USB-C iPad and newer iPhone layouts
  are discovered through dynamic QuickTime configurations, interfaces,
  alternate settings and endpoints rather than fixed descriptors.
- Add structured application, native-core, USB, decoder, renderer, media-cast,
  driver and shutdown diagnostics with bounded log rotation and privacy-safe
  device fingerprints.

### Changed

- Route video-app casting through the same main preview, detached window,
  fullscreen, context-menu, screenshot and statistics experience used by USB
  and AirPlay mirroring. Video casting uses a native Windows title bar and
  standard DWM corners, while mirroring keeps its borderless device-shaped
  window and corner toggle.
- Replace URL reloads used as playback controls with explicit pause, resume and
  seek IPC commands. The main Stop button now sends the receiver protocol stop
  request and remains synchronized with remote playback state.
- Negotiate HDR output dynamically for the monitor containing each preview
  window, including monitor moves and Windows Advanced Color changes, while
  keeping ordinary SDR previews on the lower-bandwidth BGRA8 path.
- Make release builds stage the freshly compiled native core and wireless host
  before both normal builds and publishing, ensuring UI smoke tests and release
  packages execute the same binaries.

### Fixed

- Fix live and HLS video streams that connected successfully but never started,
  including bounded recovery for transient network and media-backend failures.
- Fix video-cast status overlays remaining over active playback, missing stream
  resolution/frame-rate/audio statistics, blocked device-tab switching and
  application hangs during playback completion or teardown.
- Fix detached-window dragging stalls, double-click fullscreen, title-bar and
  corner-policy inconsistencies, and incorrect removal of the mirroring corner
  option.
- Fix phantom wired-device cards by reconciling usbmux identity with USB serial
  and physical-port topology and rejecting ambiguous topology matches.
- Fix iPad activation failures and cross-device USB reassociation during
  QuickTime re-enumeration, including PID/address changes after configuration
  switching.
- Fix duplicate flip-model swap chains on the main preview HWND. Preview
  ownership is serialized across legacy and multi-session renderers, repeated
  attachment is idempotent, and switching or stopping one device no longer
  disrupts another active session.
- Fix asynchronous Media Foundation event handling, transform shutdown, sample
  ownership and parameter/color changes that require decoder reconstruction.
- Harden driver operations, packaging paths, reparse-point checks, payload
  validation and elevated-process result handling.

### Verification

- Pass Release native protocol, wireless-host, application-logic and driver
  tests with zero build warnings or errors.
- Pass two-device USB testing with both sessions streaming concurrently,
  stable selection across refreshes, independent stop behavior and two
  QuickTime shutdown messages per device.
- Pass integrated video-cast, AirPlay media-control and native window-chrome
  smoke tests, including pause, seek, resume, remote stop, detached-window
  fullscreen and zero preview swap-chain attachment failures.

## [1.1.0-preview.1] - 2026-07-17

### Added

- Add video-app casting beside AirPlay screen mirroring. The receiver accepts
  HTTP(S) and HLS playback URLs, routes play/stop commands through bounded
  bidirectional IPC, and reports playback duration, position and rate back to
  the sending device.
- Add a lightweight DLNA/UPnP MediaRenderer with SSDP discovery, device and
  service descriptions, AVTransport, ConnectionManager and RenderingControl
  actions for video apps that use cast discovery instead of screen mirroring.
- Add an integrated WPF `MediaElement` playback surface, bilingual status and
  error messages, and an end-to-end media-cast UI smoke test.
- Add bounded native-log tail reading and additional aspect-ratio, wireless
  lifecycle and IPC regression coverage.

### Changed

- Refactor device-session, wireless-receiver and media-cast lifecycle ownership
  into dedicated services, reducing duplicated stop/destroy paths in the main
  view model.
- Replace the legacy XAML detached preview window with the native preview
  window path and keep aspect-ratio, rotation, corner and multi-device behavior
  coordinated by native HWND ownership.
- Use one combined AirPlay host and visible receiver identity while separating
  screen-mirroring frames from video-app playback commands through bounded IPC
  message types and independent application session state.
- Publish the WPF application and driver manager as compressed self-contained
  single-file executables while leaving required native and wireless runtimes
  beside the application for deterministic loading and licensing.
- Refresh the pinned AirPlay receiver build, source metadata, patches and
  SHA-256 manifest for screen-mirroring-only capability handling.

### Fixed

- Harden wireless host startup, authenticated named-pipe client validation,
  message bounds and shutdown cleanup so receiver processes cannot silently
  attach to the wrong parent or remain after the application exits.
- Accept fragmented DLNA HTTP headers and SOAP bodies by restoring blocking,
  timeout-bounded I/O on each accepted client socket.
- Improve Apple support installer process handling and release packaging checks.

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

[Unreleased]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.2...HEAD
[1.5.2]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.1...v1.5.2
[1.5.1]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.0...v1.5.1
[1.5.0]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.4.4...v1.5.0
[1.4.4]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.4.3...v1.4.4
[1.4.3]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.4.2...v1.4.3
[1.4.2]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.4.1...v1.4.2
[1.4.1]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.4.0...v1.4.1
[1.4.0]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.2.2...v1.3.0
[1.2.2]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.1.0-preview.1...v1.2.1
[1.1.0-preview.1]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.0.3...v1.1.0-preview.1
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
