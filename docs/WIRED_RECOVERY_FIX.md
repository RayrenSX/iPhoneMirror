# Wired reverse-control recovery investigation — 2026-10-01

> Historical first-pass report. The subsequent audit found additional recovery failures. Use `WIRED_RECOVERY_REAUDIT.md` for the current result and delivered build; the status below describes only the earlier measured run.

Status: **FIX VERIFIED** for the tested real device and build. A single 839.6-second (approximately 14-minute) capture/control session completed three proactive refreshes, recovered both a forced actual socket closure and a subsequent natural timeout, and acknowledged GUI-triggered keyboard releases after every recovery and refresh. This validates recovery; it does not establish that disconnects have been eliminated or guarantee indefinite reliability. The installed Program Files build has not been replaced; use the normal-use launcher below.

## Scope and reproducible artifacts

- Original source: `C:\Users\Ray\Documents\iphoneMirror`.
- Buildable recovery source: `C:\Users\Ray\.codex\worktrees\wired-recovery\iphoneMirror`, base `76e962f37f55a05e4a7b4c86235338957d879c9f`.
- The original workspace has concurrent unrelated theme, localization, countdown, serialization, and driver-UI changes. They were preserved. The isolated checkout includes the relevant pre-task changes and the recovery changes, without the concurrent redesign. Verification applies to the delivered binaries and recorded isolated-source hashes, not to every concurrent edit in the original workspace.
- Final built application: `C:\Users\Ray\Documents\iphoneMirror\outputs\recovery-validation\iPhoneMirror.exe`.
- Normal-use launcher: `C:\Users\Ray\Documents\iphoneMirror\Launch-Recovery-Build.cmd`; it clears the diagnostic fault-injection environment variable before launch.
- Its bridge is the adjacent `tools\iUsbBridge.exe`; the application resolves it from `AppContext.BaseDirectory`.
- Test runner: `scripts\Test-WiredRecoveryDevice.ps1`, deterministic Windows UI Automation, 660 seconds by default.
- Manual deterministic UIA helper: `scripts\Test-WiredControlUia.ps1`.
- Complete source hashes: `work\recovery-tested-source-sha256.csv`.
- Review patch against the saved pre-task source: `work\recovery-only.patch` (validated with `git apply --check`). It excludes an incidental menu-style refactor inherited by the validation checkout.
- Complete bridge runtime hashes: `outputs\recovery-validation\tools\iUsbBridge.runtime.json`; all 925 listed runtime file hashes were checked after the live test.
- Frozen evidence before test teardown: `work\recovery-device-20261001-101542\evidence-soak.json`; final continuation monitor result: `summary-soak-monitor.json` in that directory.

## Initial failure and the point where recovery stopped

All timestamps below use the outer log timestamp in UTC. Embedded Python log timestamps in the historical files use a different local offset and must not be compared directly.

The earlier explicit disconnect was **2026-09-29 13:00:18.352 UTC**: `send_failed`, `ConnectionResetError: Connection lost`. The application entered Recovering at `.353`; detection and triggering occurred.

The latest pre-task run gives the more diagnostic chain (app PID 1664, bridge PID 21856):

| UTC on 2026-09-30 | Evidence |
|---|---|
| 01:43:53.937 | Initial startup completed. |
| 01:46:54.005 | Proactive HID refresh reported success. |
| 01:49:52.466 | Reactive HID refresh started. The preceding failed operation was caught; its original exception was not separately logged here. |
| 01:49:52.608 | Device advertised HID services 257, 512, 1026, 1280. |
| 01:49:52.610 | Refresh reported success. |
| 01:49:52.642–.669 | Retried `keyboard_batch seq=1` failed in `create_keyboard_service`: `IncompleteReadError: 0 bytes read on a total of 9 expected bytes`; then `send_failed`. |
| 01:49:52.683 | Bridge began its local tunnel recovery. |
| 01:49:53.660–.665 | Recovery reused the old session and failed with `ConnectionTerminatedError`. |
| 01:50:21.728; 01:50:50.553; 01:51:20.975 | Replacement bridge attempts failed. Capture-mux VERSION handshakes failed; Apple usbmux could not find the selected device. No recovered HID ready or resumed input was recorded. |

Source inspection explains why a successful refresh did not repair the chain: the refresh discarded the advertised keyboard service ID 512. The next keyboard send attempted duplicate registration instead of using the discovered surface. Subsequent recovery reused stale Lockdown/service state, and replacing the bridge discarded the working capture-mux owner. The failure stopped before replacement RSD/HID became ready.

Historical evidence is preserved under `artifacts\backup-before-recovery-fix-20260930-101108\logs-before`, principally `reverse-control.log`. Full recovery-related search results are in `work\recovery-call-sites.txt`.

## Root-cause classification

| Class | Confidence | Evidence |
|---|---|---|
| F — stale HID/session state | CONFIRMED BY LOG/CODE | Refresh discovers 512, then clears the field; the next frame fails while registering a keyboard. The exact private iOS reason for rejecting registration is not available. |
| G — stale usbmux/RSD session | CONFIRMED BY LOG/CODE for stale Lockdown reuse; STRONGLY SUPPORTED for mux state loss as the handshake cause | Old recovery uses the failed session. Replacement bridge VERSION handshakes fail while capture continues; preserving the original mux lets the new recovery succeed. |
| C — recovery task cancellation | CONFIRMED BY CODE and regression test | Health/rotation cancelled the task that also owned supervision. The new sender is an independent child task. |
| E — concurrency/locking | CONFIRMED BY CODE and regression test | Cancelling executor-backed stdin reads does not stop their threads; another reader can steal the next packet. Keyboard creation/button initialization also needed bounded lifecycle ownership. |
| D/H — retry and bridge lifecycle | CONFIRMED BY CODE and tests | Terminal recovery could leave a live process waiting on stdin; repeated bridge replacement lost the session it needed. Recovery now has one owner, bounded attempts, terminal cleanup, and a budget reset after verified ready. |
| J/K — sender and UI synchronization | CONFIRMED BY CODE and lifecycle tests | The host gate could close while the ViewModel ignored an event; replacement ready did not consistently restore input routing/UI. Recovery now suspends input and restores it only for a matching ready bridge. |
| D/J — premature recovery completion | CONFIRMED BY LOG/CODE and regression test | A packet already in flight could observe the health task's `_recovering` flag and emit ready without repairing HID. The rejected 03:47:58 UTC run logged completion after 1 ms, before failed HID reinitialization. Only the owner that actually repaired and verified HID may complete recovery; queued input waits for ready. |
| K — recovery UI intercepts active input | STRONGLY SUPPORTED; exact old cancellation gesture unlogged | On 2026-09-30 at 03:53:30 UTC the app activated a recovery Cancel dialog during active input; the window closed 66 ms later and the bridge was stopped. Automatic wired recovery now updates existing status without activating that dialog. Explicit Cancel invocation is logged. |
| A/B — missing detection/trigger | Not the cause of the historical failures above | Both `send_failed` and Recovering are present. Idle health detection was nevertheless added. |
| I — Developer Image lifecycle | Not established as the root cause | The existing bundle is internally consistent and its mounted services support successful recovery. |

An intermediate candidate used repeated `connectedServices` queries as a health check. A real-device probe showed the second inventory query on the same HID socket closes it (`work\probe-hid-health.log`). That candidate was rejected. The final implementation queries inventory once per new HID connection and uses HTTP/2 PING thereafter. Four real keyboard releases and four corresponding PING ACKs succeeded on one socket (`work\probe-hid-ping.log`).

## Recovery call graph and state ownership

```text
UsbControlButton -> MainWindow handler -> MainViewModel.StartUsbControlAsync
  -> actual prerequisite prompt -> ControlPromptPrimaryButton
  -> UsbTouchBridgeHost.StartAsync -> DirectUsbInputBridge.StartAsync
  -> actual iUsbBridge.exe -> TouchSession.connect
  -> existing capture-mux owner -> fresh Lockdown -> developer-service check
  -> _connect_with_lockdown (supervisor)
  -> _run_tunnel_attempt -> CoreDevice tunnel -> RSD -> Universal HID
  -> one inventory query -> release report + same-socket PING ACK -> ready
  -> host validates device/transport/gate -> router Begin -> Connected

Normal input:
NativePreviewHost -> MainWindow input handler -> ViewModel
  -> input router / UsbTouchBridgeHost -> DirectUsbInputBridge writer
  -> framed stdin -> _serve -> live HID -> first-input PING ACK

Reactive failure:
send exception OR 15-second PING health failure
  -> recovery_triggered -> host gate closed -> UI Recovering, router Stop
  -> try HID-only refresh over current RSD
  -> if unavailable: stop sender, preserve capture mux, dispose failed tunnel
  -> 1/2/3-second backoff, at most three consecutive transport attempts
  -> fresh Lockdown + developer-service check + CoreDevice/RSD/HID
  -> verified ready -> sender_restored -> matching host/router -> Connected
  -> normal GUI input -> input_verified

Exhaustion:
recovery_failed / direct_hid_recovery_exhausted
  -> Failed UI, closed gate, stopped router, dispose bridge/close stdin
```

Initial startup uses `start_complete`. In-process recovery uses `ready`, `sender_restored`, and `recovery_completed`; it does not fabricate another application startup event.

## Meaningful changes and concurrency audit

- `tools\usb_touch_bridge.py`: preserve the discovered keyboard ID; retain a shielded complete stdin-frame read across sender cancellation; separate sender from recovery owner; preserve raw capture-mux ownership while rebuilding Lockdown/CoreDevice/RSD/HID; verify readiness, first input, and keyboard releases using a same-socket PING ACK; health checks every 15 seconds; proactive refresh every 180 seconds; bounded recovery and stage diagnostics; preserve a touch-up slot until its send succeeds; bound keyboard/button initialization; finish failed tunnel-reader cleanup. An in-flight packet cannot announce another task's recovery, and queued packets wait for verified readiness.
- An explicit, per-process test environment variable closes the actual CoreDevice socket once. It is off by default, affects only reverse control, and synthesizes neither errors nor ready events.
- `DirectUsbInputBridge.cs`: close readiness/gate before recovery/error notification; clear terminal flags on validated replacement ready; recheck readiness after taking the writer semaphore; detect unexpected stdout EOF; retain bridge diagnostic messages.
- `UsbTouchBridgeHost.cs`: explicit Recovering/Ready/Error transitions.
- `MainViewModel.cs`: stop input routing during bridge-local recovery; validate target binding before restoring it; restore Connected UI after ready; display terminal exhaustion and dispose the failed bridge instead of starting another competing mux owner. Automatic recovery does not activate a Cancel dialog over ongoing input. Touch packets whose writer loses readiness are safely dropped rather than becoming unobserved task failures.
- `MainWindow.xaml` and `ReverseControlStatusWindow.xaml`: expose wired status and stable confirmation/status IDs to UI Automation.
- `NativePreviewHost.cs`: retain WPF's built-in HwndHost automation provider and hand logical focus to the native child after WPF's focus transaction. WPF-wrapper focus alone did not reliably generate the normal keyboard-release path. No test-only input handler was introduced. An earlier custom-provider attempt was rejected after a UI-discovery exception.
- Targeted Python and C# tests plus deterministic UIA scripts cover the recovery behaviors. The final UIA helper reacquires controls and retries focus actions within bounded limits, requires a fresh keyboard-batch acknowledgement, and polls for an observable Connected UI with active capture before its final result. Missing controls are never treated as success.

Lock order is keyboard lock -> HID lifecycle lock -> HID operation/XPC request lock. Rotation holds the lifecycle lock and does not acquire the keyboard lock. Recovery is triggered after the failed operation unwinds its locks. The supervisor survives sender cancellation, while application shutdown cancellation still propagates. The retained stdin read prevents competing executor readers. Host sends serialize through `_sendLock` and recheck readiness inside it. ViewModel events are dispatched to the UI thread and reject obsolete bridge instances/cancelled sessions. Bridge-local recovery does not acquire the native capture lifecycle gate or stop capture.

## Binaries, backup, and hashes

Backup created before bridge changes: `C:\Users\Ray\Documents\iphoneMirror\artifacts\backup-before-recovery-fix-20260930-101108`. It contains the executable, manifest, complete `_internal`, original source patch, original logs, and per-file SHA-256 CSVs for the installed, previous-GUI, and latest pre-task bridge runtimes. No official backup was removed.

| Bridge/runtime | SHA-256 |
|---|---|
| Installed original `iUsbBridge.exe` (still installed) | `CC13825F1D128393BB07034A71B1BB73A4C786336582D23200F2BC5C40EA8A1F` |
| Previous GUI bridge | `823FC95F70FCCE96E5E96B9867BA14937E21E00F709403BBD8868EA09B99A375` |
| Latest pre-task bridge | `48C6BE36F6B5D1A920CCED3A8A2CEDF3914308467B46955F0CDF192755A3E2D9` |
| Final rebuilt bridge | `944164D1268CF6592040BE2BD13C0D07837BE0CA066FFE1DFBA1DF25320D99C8` |
| Installed original runtime manifest | `64DD65312790D97C715EC7B9986EEC543B74F0FE9C7861C9B4D71601F2326065` |
| Final runtime manifest | `0EAFC4299CA01842C11438C03076A1D367BE26FB2D6200BEE7F0EF87929B7052` |

Final application SHA-256: `3AAFCA1435B8DD6B3D237F2E1424E08D8985033125D77A8164F614594E5A62A2`. The application is published as a self-contained single file. The bridge requires its complete onedir runtime; copying only the bridge executable is insufficient. Its manifest was regenerated and every listed file validated.

## Device and DDI

The actual device is `iPhone13,1`, iOS `18.7.8`, `BuildVersion=22H352`; Lockdown did not supply `ProductBuildVersion`. The DDI BuildManifest and mounted DDI report `27A5228h` (CoreDevice 642.4). These are separate build identities, not evidence that bundle components were mixed. This device has a matching build identity in the manifest. Image.dmg SHA-384 matches the manifest; the project's `Image.trustcache` alias matches the manifest's `Image.dmg.trustcache` digest. No DDI was replaced or remounted. Evidence: `work\recovery-ddi-check.log`, `work\recovery-ddi-trustcache-check.log`, and `work\probe-hid-ping.log`.

## Test results and actual recovery timeline

- Python bridge/security suite: **109 passed** (`work\recovery-python-tests-final.log`). Includes keyboard-ID retention, one-shot inventory, readiness failure, PING data buffering/GOAWAY, fresh Lockdown, budget reset/exhaustion, cancellation isolation, stdin frame retention, failed touch release, and concurrent health/input recovery ownership.
- C# targeted lifecycle tests: **11 assertions passed** (`work\recovery-csharp-tests-final.log`), including a real queued writer whose gate closes before transmission; the ViewModel drops it without faulting or writing bytes.
- Existing application logic tests passed; all **11 native tests passed**. Bridge build-source checks passed.
- Final .NET publish and PyInstaller build passed. Five existing nullable warnings remain in unrelated capture calls.
- The full WPF runtime suite does **not** pass: its workspace/theme test hits `The Application object is being shut down` while constructing MainWindow. Preserved in `work\recovery-isolated-runtime-tests.log`; not counted as a recovery-test pass and not changed as part of this fix.

The final GUI run is `work\recovery-device-20261001-101542`, app PID **33332**, bridge PID **34740**. UIA invoked CaptureActionButton, UsbControlButton, the real Continue confirmation, and the ready status Close button. The test uses the normal preview focus-loss keyboard-release path and requires an acknowledgement after every scripted release. The session ran from 02:16:00.485 to 02:30:00.118 UTC, including its recovery intervals; capture was not stopped during it.

| UTC on 2026-10-01 | Actual event |
|---|---|
| 02:16:00 | Initial direct HID ready. |
| 02:16:30.466 | Opt-in test closed the actual CoreDevice socket. |
| 02:16:33.529 | Health timeout detected; `recovery_triggered`. |
| 02:16:36.539 | HID-only repair failed because its tunnel was closed. |
| 02:16:37.549 | Fresh transport reconnect began, capture mux preserved. |
| 02:16:37.796 | RSD reconnect began. |
| 02:16:37.989 | HID reinitialization began. |
| 02:16:38.144 | HID ready and sender restored. |
| 02:16:38.149 | Application restored the existing bridge's input router. |
| 02:16:38.156 | Recovery completed, about 4.6 s after detection / 7.7 s after socket closure. |
| 02:16:45.099 | UIA-triggered empty `keyboard_batch seq=9 generation=2` acknowledged on the HID connection. |
| 02:19:38.562 | Proactive refresh 1 completed; UIA release acknowledgement confirmed at 02:19:40.231. |
| 02:21:54.659 | A natural timeout triggered a second reactive recovery. |
| 02:22:01.719 | Fresh transport reconnect began after the local HID repair failed. |
| 02:22:01.987 | RSD reconnect began. |
| 02:22:02.347–.358 | HID ready, sender restored, recovery completed, about 7.7 seconds after detection. |
| 02:22:06.630 | UIA confirmed the post-recovery keyboard release acknowledgement. |
| 02:25:02.790 | Proactive refresh 2 completed; UIA release acknowledgement confirmed at 02:25:21.639. |
| 02:28:03.225 | Proactive refresh 3 completed. |
| 02:28:07.340 | Monitoring helper hit a UIA SetFocus exception. App and bridge remained alive; no application exception occurred. |
| 02:28:56.196 | A continuation helper confirmed a fresh UIA-triggered release acknowledgement in the same session. |
| 02:30:00.118 | Continuation monitor completed successfully; Connected UI and active mirroring confirmed. |

A PING ACK proves delivery to the live HID transport, not a visually observed iOS screen effect. There were seven scripted GUI release checks and 454 total acknowledged input events in the frozen evidence. The application and bridge PIDs remained unchanged, with exactly one bridge startup. Capture retained session `00008234-000000004FD1FD55`: video-output counters increased from 1 to 48,240 and audio-packet counters from 1 to 39,825. No capture stop, explicit Cancel, recovery exhaustion, or application exception occurred during the measured run. FPS samples ranged from 29.3 to 61.9, generally near 60; a uniformly steady 60 fps is not claimed.

The monitoring PowerShell helper was first replaced to extend observation without restarting the application or bridge. A later helper failed on SetFocus and was retried; its failure is retained in `uia.log` and `evidence-soak.json`, not counted as a successful script invocation. The final continuation exited with code 0. Its `summary-soak-monitor.json` duration of 64 seconds describes only that final helper invocation; the frozen evidence's 839.6 seconds describes the full unchanged application/capture session. The transient UIA failure's external cause was not established.

## Test teardown and scope limits

A rejected candidate's GUI test on September 30 was stopped and the app closed before USB restoration finished. That left the phone undiscoverable until a cable reconnect. An earlier custom UIA provider also failed during UI discovery and was removed. Those runs were rejected rather than counted as verification.

The corrected teardown was subsequently demonstrated: Stop mirroring at 03:50:35.947 UTC, then `usb_configuration_restore finalized ... normal_observed=true` at 03:50:47.150 UTC, before closing the app. Future teardown must wait for that confirmation. No drivers, filters, Apple components, or device stack were reset or reinstalled.

For the final October 1 run, UIA invoked Stop at 02:30:30.439 UTC, confirmed both normal USB restoration and capture-stop completion at 02:30:46.216, and then closed the app. It exited at 02:30:47.062. This restart was test teardown to remove the diagnostic environment, not the recovery mechanism. Details are in the run's `teardown.log`.

The same tested binaries were then launched with fault injection disabled. The normal-use run is `work\recovery-device-20261001-103118`, app PID **35364**, bridge PID **31840**. UIA invoked the actual Capture, Wired Control, Continue, and Close controls; Connected was reached at 02:31:43.520 UTC. Its first smoke helper failed because a single final UIA lookup returned null for the wired control while capture and acknowledged input continued. The failed result is preserved as `summary-transient-uia-failure.json`. After the bounded UIA polling improvement, a continuation helper acknowledged another real GUI release at 02:33:38.812 and completed with exit code 0 at 02:33:52.477, reporting Connected and Stop mirroring. No application/bridge restart occurred between these helper invocations. This normal app/capture session is left running; there were no diagnostic disconnects or application exceptions in its saved evidence.

A real-device test is evidence for this device/build and the observed duration, not a guarantee of indefinite reliability. Bluetooth/wireless recovery is outside this wired-device test. Final normal use must launch without the opt-in fault-injection environment variable.

## Computer Use declaration

```text
Computer Use: NOT USED
Visual desktop agent: NOT USED
Screenshot-based clicking: NOT USED
Coordinate clicking: NOT USED
UI automation: Script-based Windows UI Automation
Real iPhoneMirror GUI: USED
Real device: USED
```
