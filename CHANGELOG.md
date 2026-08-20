# Changelog

All notable changes to iPhoneMirror are documented here. The project follows
[Semantic Versioning](https://semver.org/) for published releases.

## [Unreleased]

## [1.6.9] - 2026-08-20

### Added

- Add a five-click version-button entry point for the independent Developer
  Tools window. It can preview every workspace page and existing child window
  without performing real network, device, update, or destructive operations.
- Add a read-only runtime inspector for the current version, culture, theme,
  DPI scale, open-window count, diagnostics, theme/language selection, opacity,
  topmost state, and compact/default/maximized window presets.
- Add conventional controls to AirPlay and DLNA video playback, including
  pause/resume, seek, 10-second skip, volume, mute, time display, and keyboard
  shortcuts, while disabling seek for live streams.
- Unify cast-video playback controls into a responsive themed overlay with a
  centered primary action, consistent Fluent icons, full-screen access, and
  lightweight hover, press, state-change, and idle-fade feedback.
- Add safe preview entry points for confirmation prompts, recovery guidance,
  image settings, projection settings, media output, USB mode details, startup
  errors, updates, instance conflicts, and the native independent preview.
- Add native NV12 frame export from decoded sessions for recording and live
  output, including aspect-preserving letterboxing and P010-to-NV12 conversion.
- Let media-app casting use recording, live publishing, virtual camera, and an
  independent preview window from the same toolbar as device mirroring.
- Decode the media source audio track directly for recording and live output,
  without capturing unrelated Windows system, notification, or microphone audio.

### Changed

- Standardize top-level window chrome, drag surfaces, close-button behavior,
  theme resources, and light/dark contrast across the main window and child
  windows.
- Let Windows/DWM own the outer rounded corners of the Developer Tools and
  instance-conflict windows; remove their duplicate hand-drawn outer corner
  radius and border while retaining rounded internal cards and warning panels.
- Keep the Developer Tools window independent, draggable, non-topmost, and
  coverable by the main window, matching the requested multi-window workflow.
- Keep WPF on its hardware-first composition path and flush the local HLS bridge
  immediately, improving high-resolution media-cast frame pacing and smoothness.
- Remove the root-page opacity transition that could leave a newly selected
  page transparent, and keep page content visible through navigation changes.
- Apply final work-area size and centered position to an independent preview
  while its HWND is hidden, then show it in one operation to prevent the first
  frame from flashing at the default origin or jumping during aspect fitting.
- Add stable aspect-ratio sizing for portrait, landscape, rotated, mixed-DPI,
  and high-DPI native previews, with work-area and minimum-size constraints.
- Keep native preview windows independently manageable per device, with
  fullscreen, rotation, audio, fixed-size, corner, display, and context-menu
  controls isolated from the main preview.
- Improve AirPlay/DLNA playback state handling around buffering, live streams,
  pending seeks, volume/mute commands, and Play/Stop command boundaries.
- Prefer an available hardware H.264 encoder (NVENC, AMF, QSV, or Media
  Foundation) after probing the requested output size, while retaining libx264
  fallback behavior.

### Fixed

- Keep a localized loading card visible until cast video playback advances or
  buffering ends, and remove the redundant close button from the preview.
- Make direct clicks on the cast-video progress track seek deterministically
  and keep the selected position stable while the media backend catches up.
- Match Platinum/i4AirPlayer AVTransport behavior for video-app casting by
  advertising and accepting `Next`/`Previous`, preserving current and queued
  media metadata, and promoting a queued next URI without returning an action
  error that prevents iQIYI from automatically playing the next episode.
- Prevent Windows MediaElement's short HLS segment duration from being exposed
  as the programme duration, preserving playback state and progress across
  segment changes instead of restarting or advancing after a few seconds.
- Route HLS cast URLs through the bundled FFmpeg demuxer and a continuous local
  MPEG-TS stream so playlist refresh, encryption keys, discontinuities,
  reconnects, seeks, and genuine end-of-stream are coordinated in one pipeline.
- Keep mouse and mobile seeks stable while a replacement HLS stream opens, and
  prevent the progress thumb from jumping through transient segment timestamps.
- Prevent the elevated update PowerShell host from creating a console window
  by launching it through Windows ShellExecuteEx no-console mode.
- Reject elevated portable updates when the current user can replace any
  installation path component, including through topology-only ACL rights.
- Restrict the virtual-camera frame channel to the current user and Windows
  Frame Server identities instead of every packaged application.
- Copy hash-verified virtual-camera installer payloads into an administrator-only
  directory before launching the elevated helper, preventing DLL side-loading
  from a user-writable staging path.
- Run recording and live-output frame, pipe, and audio pumps outside the WPF
  dispatcher so encoding backpressure cannot freeze the application UI.
- Feed recording and live-output FFmpeg directly with native letterboxed NV12
  frames, eliminating redundant BGRA conversion and high-resolution frame copies.
- Prefer a working hardware H.264 encoder (NVENC, AMF, QSV, or Media Foundation),
  probe the requested output size, and retain libx264 as an automatic fallback.
- Bound repeated-frame catch-up after prolonged FFmpeg stalls while preserving
  normal short-delay compensation and resynchronizing future output to current
  wall-clock time.
- Keep media-volume coalescing within the current Play/Stop command boundary
  so an earlier cast cannot change a later cast's mute state.
- Prevent header drag handling from intercepting close buttons and other
  interactive controls, restoring reliable close behavior for every child
  window.
- Make Developer Tools confirmation and cancellation previews close safely
  without triggering a process crash, and keep read-only preview actions
  isolated from real application operations.
- Restore the close button on all existing child windows, including settings,
  update, recovery, prompt, and instance-conflict surfaces.
- Prevent duplicate outer borders and corner artifacts from reappearing after
  opening a child window, dragging a window, changing DPI, or switching pages.
- Keep the main window and every native preview hidden immediately during
  shutdown before bounded background cleanup begins.
- Match Apple USB parent devices by exact VID/PID/topology identity, record the
  active USB configuration, and avoid treating interface nodes or stale device
  matches as the selected phone.
- Restore QuickTime/Apple USB configuration only when the active configuration
  is known to require it, reducing unnecessary reconfiguration during capture
  start, failure, cancellation, and teardown.
- Add deterministic native Media Foundation decoder and NV12/P010 frame-copy
  validation for malformed dimensions, strides, buffers, and odd output sizes.
- Keep virtual-camera frame exchange scoped to the current user and Windows
  Frame Server identities, rejecting unauthorized mapping attempts.
- Bound FFmpeg catch-up after prolonged stalls and resynchronize future output
  to wall-clock time instead of emitting an unbounded burst of repeated frames.
- Move recording/live-output frame, pipe, and audio pumps off the WPF dispatcher
  so encoder backpressure cannot freeze the application UI.

### Security

- Revalidate portable update path ownership at the elevation boundary,
  including topology-only ACL rights that could permit replacement of a parent
  directory or file.
- Copy hash-verified virtual-camera installer payloads into an
  administrator-only staging directory before launching the elevated helper,
  preventing DLL side-loading from user-writable paths.
- Lock the current driver-manager executable across its elevation boundary and
  reject unsafe replacement paths before privileged operations begin.
- Launch elevated update helpers through a no-console ShellExecuteEx path and
  preserve the required UAC boundary without exposing an extra console window.

### Tests

- Add runtime coverage for the five-click Developer Tools entry point, all
  read-only preview surfaces, drag behavior, independent/non-topmost state,
  outer-corner ownership, and safe confirmation actions.
- Add logic and native coverage for media command ordering, volume/mute state,
  NV12/P010 frame export, DLNA control handling, USB identity/configuration,
  update boundaries, and driver elevation-path locking.

## [1.6.8] - 2026-08-17

### Added

- Add AirPlay audio-only reception for music playback, including automatic
  source creation when a RAOP sender omits the generic connection callback.
- Show a dedicated music state for audio-only sessions and disable video-only
  preview tools until the sender starts delivering video.

### Changed

- Use a bounded network-jitter playback mode for AirPlay music, with deeper
  startup and high-water buffers sized for RAOP retransmission bursts.

### Fixed

- Decode audio-only AirPlay ALAC frames and recover smoothly from missing or
  delayed Wi-Fi packets instead of producing silence or repeated underruns.
- Keep recording duration and playback speed aligned with elapsed wall-clock
  time, including when FFmpeg encoding briefly falls behind and catches up.
- Hide the main window, child windows, and native preview windows immediately
  after a close request, then finish bounded media and USB cleanup in the
  background before the process exits.

## [1.6.7] - 2026-08-15

### Fixed

- Keep the elevated PowerShell host hidden while verified installer and
  portable ZIP updates are applied, without suppressing the required UAC prompt.

## [1.6.6] - 2026-08-15

### Changed

- Speed up update downloads on constrained GitHub routes by testing GitHub and
  all 115 mirrors listed by MoreTools, measuring a 256 KB sample only from
  reachable routes, and failing over through the resulting throughput ranking.

## [1.6.5] - 2026-08-14

### Added

- Add structured capture failure kind, stage, and error-code reporting across
  the native API, managed UI, diagnostic logs, and USB lifecycle probe.
- Add an isolated USB configuration switch helper and include it in installed
  and portable release packages.
- Add localized recovery guidance for USB connection, driver, stream, decoder,
  timeout, duplicate-session, device-disconnect, and phone-closed failures.

### Changed

- Serialize start and stop work per device, queue requests behind in-progress
  teardown, and revoke session handles before native cleanup begins.
- Suspend wired enumeration and Lockdown metadata refresh while USB devices are
  switching configurations, then require stable exact-device evidence before
  treating a restored device as ready.
- Keep wired capture available for Apple/libusb0 stacked filter installations
  through a conservative open, activation, and teardown path.
- Hide preview-only controls while the selected device is not actively
  mirroring and show explicit queued and cleanup states during transitions.
- Disable real-device USB stress probes in default builds unless explicitly
  enabled with the dangerous-tools build option.

### Fixed

- Restore the application icon on the Windows taskbar by assigning the
  executable icon to top-level windows, registering a stable AppUserModelID
  icon source, and removing stale per-user shortcuts during all-users upgrades.
- Restore the normal Apple USB configuration more reliably after stopping,
  including repeated start/stop cycles and multi-device sessions.
- Avoid stale or cross-device USB matches by correlating exact serial,
  topology, PnP interface transitions, descriptor state, and usbmux presence.
- Prevent concurrent stop callers, background error cleanup, and window-close
  shutdown from destroying or reusing the same native session twice.
- Distinguish user-initiated mirroring stops from cable disconnects and provide
  the correct recovery action without exposing raw diagnostics in prompts.
- Bound socket shutdown waits and tighten QuickTime control-channel cleanup so
  teardown cannot hang indefinitely on a stalled peer.

## [1.6.3] - 2026-08-13

### Changed

- Show the complete tagged release document in the updater, include the release
  title, and provide a trusted link to the corresponding GitHub Release page.
- Stream release metadata, release notes, and checksum manifests through
  explicit size limits instead of buffering unbounded responses.

### Fixed

- Make installed and portable updates revalidate the downloaded package at the
  elevation boundary, and execute only the verified installer or embedded ZIP
  helper from access-controlled staging directories.
- Make portable updates transactional: reject unsafe or oversized archives,
  avoid reparse-point traversal, verify every copied file, and roll back
  replaced or removed files when an update cannot complete.
- Restrict update-cache cleanup and single-instance process discovery to their
  intended directories and executable paths.
- Compare arbitrarily large numeric prerelease identifiers without integer
  overflow.
- Clean up FFmpeg processes, named pipes, and recording staging files after
  every partial startup failure.
- Reject malformed decoder, virtual-camera pipe, registry, frame-size, and
  bitrate inputs instead of accepting truncated data or entering stalled paths.
- Dispose timed-out Apple device metadata connections deterministically.

## [1.6.2] - 2026-08-12

### Added

- Add fully localized Traditional Chinese (Hong Kong) resources for the main
  application and driver manager, including automatic `zh-HK` system-language
  selection, Hong Kong terminology, and the Microsoft JhengHei UI font.

### Fixed

- Keep the running version open until Setup is ready to replace its files, so
  cancelled or early-failing updates no longer look like the app uninstalled
  itself.

## [1.6.1] - 2026-08-11

### Changed

- Upgrade the bundled AirPlay receiver to v1.1.2 and keep its source,
  licensing, patch and SHA-256 records synchronized.
- Publish version-tagged Windows releases directly from the GitHub Actions
  build runner, keeping failed uploads in draft state instead of transferring
  large packages through an intermediate workflow artifact.
- Stop producing and uploading the duplicate `-latest.zip` alias; portable
  updates already prefer the versioned `win-x64.zip` asset.

### Fixed

- Restore wireless iPad screen mirroring by upgrading the AirPlay receiver to
  v1.1.2 and keeping Bonjour, server-info, and `/info` capability negotiation
  and receiver identity on the same mode-specific, upstream-compatible profile.

## [1.6.0] - 2026-08-10

### Changed

- Use one shared self-contained .NET/WPF runtime in the Setup payload for the
  main app and driver manager, while keeping the portable ZIP single-file and
  self-contained.
- Keep FFmpeg 8 bundled by default for recording, RTMP, SRT and WHIP output;
  add an explicit compact build option for systems that provide FFmpeg.
- Keep the five required .NET diagnostic runtime files in the shared installer
  payload and validate their presence during packaging and installer tests.

### Fixed

- Close `iPhoneMirror.Driver.exe` during Setup upgrades and portable ZIP
  updates before replacing shared runtime files.
- Select Setup updates for installed copies and ZIP updates for portable copies,
  including legacy single-file installations registered by App Paths.
- Remove stale FFmpeg files left by older installations or ZIP overlay updates.
- Retry external FFmpeg discovery, consider every `where.exe` result, and
  re-probe after a failed lookup without requiring an application restart.
- Preserve existing update and language settings when either setting changes,
  and remove incomplete update downloads after failures.
- Validate release asset sizes and SHA-256 digests before updating the fallback
  release manifest.

## [1.5.11] - 2026-08-05

### Added

- Add a repository maintenance utility that lists currently connected Apple
  mobile devices, previews the exact PnP nodes and third-party Driver Store
  packages associated with one selected device, and requires an
  administrator-confirmed serial-tail challenge before removal.
- Add a deterministic USB configuration restoration policy test covering
  missing devices, normal configuration, repeated QuickTime observations and
  transport failures after the disconnect request.

### Changed

- Adapt WASAPI startup and high-water thresholds to the recent PCM packet size
  and the actual endpoint buffer, preserving suitable jitter reserve for 1024,
  2048 and 4096-frame Apple audio packets.
- Close claimed streaming handles before restoring the normal Apple USB
  configuration, then observe re-enumeration through a separate control handle.
  Start and stop transitions are serialized without blocking independent
  devices' steady-state bulk media transfers.
- Let `usb_close` own legacy libusb-win32 interface cleanup instead of issuing
  an explicit release immediately before close during PnP teardown.

### Fixed

- Prevent intermittent wired-audio gaps caused by treating a 4096-frame packet
  as the entire queue high-water mark and discarding the jitter reserve that
  should bridge USB or scheduler delays.
- Prevent multi-device wired sessions from starving one another by keeping
  independent libusb0 bulk reads and writes concurrent while discovery,
  configuration and close operations remain exclusive.
- Avoid reading libusb-win32's process-global, non-thread-safe error string from
  concurrent bulk paths; timeout and failure handling now use stable numeric
  transfer results.
- Send the QuickTime configuration disable request at most once per restore
  attempt, including when the expected device disconnect surfaces as an I/O
  error, and wait for the normal configuration instead of forcing USBMux on the
  disconnecting handle.
- Reduce real-time logging pressure by throttling repeated WASAPI catch-up
  records while retaining initial and periodic diagnostics.

## [1.5.10] - 2026-08-05

### Changed

- Keep startup, periodic refreshes and device selection out of third-party USB
  kernel filters. Exact backend access now starts only after an explicit wired
  mirroring request, while all existing USB backends and fallback paths remain
  available.
- Serialize wired preflight, QuickTime configuration activation,
  re-enumeration, interface claim and handshake until the first media sample.
  New sessions prefer the backend already used by an active session, reducing
  cross-backend descriptor access without preventing multi-device capture.
- Target USB discovery by physical topology, Product ID and serial, stopping as
  soon as the requested device is found instead of opening every Apple device
  to build a complete candidate list.
- Report automatically polled USB backend values as unknown until an explicit
  wired start performs the authoritative probe, rather than presenting an
  unprobed zero value as a confirmed backend failure.

### Fixed

- Restore the normal Apple USB configuration on cancellation, open/claim or
  handshake failure, capture errors and normal shutdown after QuickTime
  configuration activation. Successful normal shutdown disarms the fallback
  restoration so the device is not reconfigured twice.
- Prevent the main window and an independent device window from creating two
  native sessions or running duplicate wired preflight for the same device
  during a concurrent start.
- Avoid loading the legacy USB runtime merely to show automatic environment
  status; runtime availability is now determined by file metadata until the
  user starts wired mirroring.
- Publish AirPlay DNS-SD records on a selected physical interface and keep the
  logical registration alive across the upstream per-adapter references, so
  multi-homed PCs do not expose duplicate receivers or drop the service when
  one adapter reference is released.

## [1.5.9] - 2026-08-03

### Changed

- Centralize every application and driver-manager scrollbar on a named modern
  style, while keeping a shared right-edge variant for dense content panels.
- Keep blue action buttons legible in light mode by explicitly carrying the
  theme-aware action foreground through the rendered button label.
- Give GitHub Actions runs a stable workflow display name instead of inheriting
  commit or pull-request titles.

### Fixed

- Replace the update window release-notes viewer's legacy WPF scrollbar with
  the same thin, rounded and animated scrollbar used by the main settings view.
- Prevent the global `TextBlock` style from overriding white text inside
  `PrimaryButton` controls in light mode, including the immediate-update action.
- Keep the shared scrollbar geometry consistent at a 6 px layout slot, 2 px
  resting thumb, 4 px hover thumb and 6 px right-edge offset where requested.
- Add runtime visual-tree checks for the real update button label and the
  internally generated release-notes scrollbar to prevent style regressions.

## [1.5.8] - 2026-08-03

### Added

- Detect an already-running iPhoneMirror instance and present a localized
  Fluent Acrylic choice to close the other instance or leave the active
  mirroring session untouched.

### Changed

- Replace the shared default scrollbars with a slimmer, theme-aware treatment
  that expands smoothly on hover without covering settings content.
- Keep the settings scrollbar aligned close to the right edge while preserving
  visible spacing from the rounded workspace border.
- Keep the About page update action legible with an explicit white foreground
  in light mode.

### Fixed

- Avoid showing duplicate conflict dialogs when two launches race at nearly the
  same time by distinguishing an older running process from a newer contender.
- Dispose filtered process handles and conservatively report inaccessible
  processes during the graceful-close and bounded forced-close workflow.
- Detect an FFmpeg process that exits while the projection audio pipe is still
  connecting, reporting the real startup failure instead of a later timeout.

## [1.5.7] - 2026-08-03

### Changed

- Keep app theme selection in the main App preferences only, removing the
  duplicate control from the About window.
- Rename virtual-camera COM implementation parameters that shadow inherited
  interface names, keeping warning-clean Release builds without behavior changes.

### Fixed

- Refresh every open child window with its own backdrop type after switching
  between light and dark themes, preventing mixed or incorrectly tinted colors.
- Resolve theme dictionaries through the application assembly so theme changes
  remain reliable when the UI is hosted by runtime tests or another executable.

## [1.5.6] - 2026-08-02

### Added

- Automatically open the Sources workspace when a second mirroring source is
  added, while preserving the user's panel choice during refreshes and removals.
- Reuse the window work-area controller in the standalone driver manager so its
  windows receive the same high-DPI and mixed-monitor fitting behavior as the
  main application.

### Changed

- Limit oversized windows to 80 percent of the active monitor work area when
  Windows display scaling is above 100 percent, leaving room to reach window
  borders and surrounding desktop controls.
- Move shared window fitting behavior into `SharedUI` so the application and
  driver manager cannot drift into separate implementations.
- Upgrade the Windows build workflow from `actions/setup-dotnet@v5` to v6.

### Fixed

- Prevent very large high-DPI windows from consuming the entire usable desktop
  even after their minimum WPF dimensions have been reduced to fit.
- Avoid reporting a Sources-panel auto-open diagnostic event when that panel was
  already visible.

## [1.5.5] - 2026-07-31

### Added

- Add a shared segmented HTTP downloader that uses concurrent byte-range
  requests when the server supports them and safely falls back to one stream.
- Add an optional GitHub asset mirror fallback for update downloads in network
  environments where direct GitHub release assets are slow or unavailable.
- Add Apple Software Update Catalog discovery for the latest official x64
  Apple Mobile Device Support standalone MSI.

### Changed

- Download the roughly 38.4 MB Apple Mobile Device Support MSI directly from
  Apple's CDN instead of downloading the roughly 208 MB iTunes installer first.
- Use up to eight parallel segments for Apple driver packages and segmented
  downloads for application updates, including redirected CDN URL reuse.
- Keep the full official iTunes package as a compatibility fallback when the
  standalone catalog route is unavailable or fails validation.
- Fit application windows to the active monitor work area after startup and DPI
  changes so large Windows display scaling cannot place controls off-screen.

### Fixed

- Prevent the application and driver manager from exceeding the usable desktop
  area on high-DPI or mixed-DPI Windows configurations.
- Improve update and Apple driver download reliability on slow or restricted
  networks, including servers that inconsistently implement HTTP Range.

### Security

- Accept update metadata only from the official GitHub API or
  `raw.githubusercontent.com`; third-party mirrors can only provide asset bytes
  after an official SHA-256 digest has been obtained.
- Require exact package sizes, trusted Apple download hosts and a valid Apple
  Authenticode signer before installing Apple Mobile Device Support.
- Require SHA-256 verification before launching any downloaded application
  update installer.

## [1.5.4] - 2026-07-31

### Changed

- Remove the WPF resize-grip glyph from every resizable application and driver
  manager window while keeping border and corner resizing available.
- Use an opaque themed window background for the driver manager so light mode
  no longer composites a gray translucent shell behind the white workspace.

### Fixed

- Prevent the bottom-right resize handle from appearing in the main window,
  child settings windows, update/error windows and driver manager windows.
- Keep the driver manager background consistent after switching between light,
  dark and system theme modes.

## [1.5.3] - 2026-07-31

### Added

- Allow the right-side projection settings panel to remain open alongside
  either the mirroring actions or source-selection panel.
- Add language-specific navigation font resources and regression checks for
  the Chinese and English navigation templates.

### Changed

- Keep the left-side Mirroring and Sources pages mutually exclusive while
  giving projection settings an independent toggle and close action.
- Rename the Chinese navigation label from "设备" to "投屏来源" and the
  English label from "Devices" to "Sources".
- Render navigation labels with explicit text, normal inactive weight and
  semibold active weight using Microsoft YaHei UI for Chinese and Segoe UI for
  English.
- Animate only the workspace side whose state changed, while settling the
  unchanged side immediately to prevent competing transition revisions.

### Fixed

- Cancel pending workspace width, opacity and translation animations when
  entering full screen so an old completion callback cannot restore hidden
  panels during full-screen preview.

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
- Recompute the embedded SPDX manifest's SHA-256 sidecar after enriching the
  generated SBOM, so portable release archives contain a valid manifest hash.

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

[Unreleased]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.6.8...HEAD
[1.6.8]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.6.7...v1.6.8
[1.6.7]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.6.6...v1.6.7
[1.6.6]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.6.5...v1.6.6
[1.6.5]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.6.3...v1.6.5
[1.6.3]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.6.2...v1.6.3
[1.6.2]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.6.1...v1.6.2
[1.6.1]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.6.0...v1.6.1
[1.6.0]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.11...v1.6.0
[1.5.11]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.10...v1.5.11
[1.5.10]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.9...v1.5.10
[1.5.9]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.8...v1.5.9
[1.5.8]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.7...v1.5.8
[1.5.7]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.6...v1.5.7
[1.5.6]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.5...v1.5.6
[1.5.5]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.4...v1.5.5
[1.5.4]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.3...v1.5.4
[1.5.3]: https://github.com/RayrenSX/iPhoneMirror/compare/v1.5.2...v1.5.3
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
