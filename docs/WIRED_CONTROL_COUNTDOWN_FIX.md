# Wired Control success-window countdown investigation

Verified on Windows on 2026-09-30. Baseline commit: `76e962f37f55a05e4a7b4c86235338957d879c9f`.

## Root cause and reproduction

The original countdown had two owners operating at different times. `OnStatusChanged(Ready)` immediately called `ArmCountdown()` and set `_countdownArmed = 1`, but UI snapshots were applied later through `Dispatcher.BeginInvoke`. The USB startup continuation reports Connecting, InitializingServices, StartingInputRouter, and Ready in one burst. Once that continuation yielded, an older queued `Apply(Connecting)` stopped the one-shot deadline timer without resetting `_countdownArmed`. When `Apply(Ready)` finally ran, `ArmCountdown()` returned because it was already marked armed. The deadline timer therefore remained disabled.

The display `DispatcherTimer` still ran against the saved deadline. At zero, its original `OnTick` only stopped the display timer and returned. It did not call the completion method, raise `CountdownElapsed`, or request `Close()`. It also returned before notifying the final label change, so the last displayed label could remain at one second. This explains a successful connection followed by a window that stays open.

This was reproduced using the real production XAML window and timers, replacing only the window code-behind with its baseline version in an isolated build. After six seconds: `closed=False`, `visible=True`, `callback=0`; the observed labels progressed from 5 through 1. The test failed specifically because the displayed window did not reach `Closed`. See [original-tests.log](../artifacts/countdown-investigation/original-tests.log).

Existing device logs also support this sequence: PID 1664 reached `wired_control_connected` and `wired_control_auto_close_countdown_started` at 2026-09-30 01:43:53 UTC, without subsequent countdown-completed/close events. Those historical logs alone did not establish the cause; the baseline reproduction did.

The original window handler also used native `ShowWindow(SW_HIDE)` before queuing WPF `Close()`. That was not the reason for the reproduced failure: the completion event never reached that handler. The final implementation directly closes the WPF window on its dispatcher.

## Actual execution flow

| Step | Production implementation |
| --- | --- |
| Wired Control clicked | `MainWindow.OnStartUsbControlClick` calls `ShowReverseControlStatus(Usb)`, then awaits `MainViewModel.ToggleWiredControlAsync`. The independent preview entry uses `StartUsbControlAsync`. |
| Status window created/shown | `ShowReverseControlStatus` starts the shared `ControlStatusService` with `Begin`, then calls `ReverseControlStatusWindow.Show`. The manager reuses a visible `_active` window or constructs, shows, and activates one. |
| Connecting | `EnableUsbControlCoreAsync` reports preparation stages. Bridge startup events also report stages; after successful startup the continuation reports Connecting, InitializingServices, and StartingInputRouter. |
| Connection success detected | `DirectUsbInputBridge` validates the bridge's real `ready` message, including the open gate, and completes `_readySignal`. `UsbTouchBridgeHost.StartAsync` validates the requested UDID. `EnableUsbControlCoreAsync` sets the connected flags and calls `ControlStatus.Ready`. |
| Connected state reaches UI | `ControlStatusService.Ready` stores a Ready snapshot and raises `StatusChanged`; the window view model queues `Apply(snapshot)` on its captured UI dispatcher. |
| Countdown starts | `Apply` transitions to Ready without a pending prompt and calls `ArmCountdown`. It sets the existing absolute five-second deadline, starts the existing one-shot `System.Threading.Timer`, and starts the existing 100 ms `DispatcherTimer` for whole-second display updates. |
| Countdown completes | The one-shot callback dispatches to the window's UI dispatcher. `OnTick` also invokes `OnCountdownDeadline` when zero is reached, covering a missing/early one-shot callback. A once guard prevents double completion. |
| Auto-close requested | `OnCountdownDeadline` checks disposal, Ready/prompt state, the service's current state, armed state, and deadline, then raises `CountdownElapsed`. |
| Displayed instance closed | The subscribed instance method `ReverseControlStatusWindow.OnCountdownElapsed` verifies dispatcher access and calls `Close()` on `this`. It logs the window ID, native handle, and `IsVisible`. |
| Closed cleanup | `Closed` marks the window closed, unsubscribes the completion handler, disposes both timers, unsubscribes status changes, and clears `_active` only when it refers to this instance. |

```text
Wired Control → Status Window Shown → Real Connection Succeeds
→ Connected/Ready UI → Five-Second Countdown → Countdown Completed
→ Window UI Dispatcher → Close() on displayed instance → Closed
```

**Previous break:** Ready armed the deadline before queued Connecting snapshots applied; those snapshots disabled the deadline, and the display timer's zero branch never completed the close path.

There is no `Task.Delay(5000)` or cancellation token in this window's success countdown. Startup/disable cancellation tokens belong to the transport operation. The separate Bluetooth input countdown fallback is outside this USB timer path.

## Fix and changed files

The checkout already contained the principal countdown fix and initial runtime tests when this investigation started. Those changes were preserved, reproduced against the earlier implementation, and verified. Concurrent UI and USB-recovery work in the same checkout was not reverted or claimed as part of this fix.

| File and location | Countdown-related change |
| --- | --- |
| `src/App/Windows/ReverseControlStatusWindow.xaml.cs`: `OnStatusChanged`, `Apply`, `ArmCountdown` | Timer arming/disarming is owned by dispatched `Apply`; non-Ready states and prompts reset the armed flag. This investigation consolidated those cancellation branches and added `wired_control_auto_close_countdown_cancelled`, with stage/reason, plus a precise ordering comment. |
| Same file: `OnTick`, `OnCountdownDeadline` | Zero on the display timer reaches the shared completion path. Completion runs on the captured window dispatcher and checks current success/prompt/disposal state before raising its once-only event. |
| Same file: `OnCountdownElapsed`, `Closed`, `CloseActive` | Close the subscribed displayed instance directly; preserve its identity across queued closes; dispose subscriptions/timers on `Closed`; log close failures. These lifecycle corrections were already in the working tree and retained. |
| `src/App/Services/ControlStatusService.cs`: `Ready` | Existing working-tree addition logs `wired_control_connected` for USB. Retained. |
| `src/App.Runtime.Tests/ReverseControlCountdownTests.cs` | Retained success/error/prompt/manual-close/replacement tests; added unclosed-timer diagnostic output and a test that suppresses the one-shot timer, invokes an early deadline callback, and requires the display timer to close the actual window and clear `_active`. |
| `src/App.Runtime.Tests/WiredControlLiveCountdownTest.cs` | New opt-in hardware integration test using production discovery, saved bindings, the normal toolbar handler, the real USB bridge, and the production status window. It never manufactures Ready. |
| `src/App.Runtime.Tests/Program.cs`: command-line dispatch | Retained `--reverse-control-countdown`; added `--wired-control-live-countdown`. |
| `docs/WIRED_CONTROL_COUNTDOWN_FIX.md` | This report. |

No second status window implementation or extra five-second delay was introduced. The window XAML, connection-failure messages, transport implementation, and Bluetooth fallback were not changed by this investigation.

## Verification evidence

### Build

The Release runtime-test build also builds the application. It succeeded with **0 errors**, with the same five existing nullable warnings emitted twice by WPF's temporary and normal project builds. They concern native capture handles in `MainViewModel`, outside the countdown fix. See [final-build.log](../artifacts/countdown-investigation/final-build.log).

### Real device: passed

The live test used the physically connected iPhone, real discovery and its existing binding, the production `OnStartUsbControlClick` entry point, real `ToggleWiredControlAsync`/bridge startup, and the visible production status window. The in-app informational prerequisite acknowledgement was supplied for this explicit test; phone trust/Developer Mode and driver settings were not modified by the harness. This was a programmatic invocation of the toolbar handler, not a desktop mouse click.

The real bridge returned `ready`, with `gate_open=True` and `auth_mode=direct`. The same displayed window reached `Closed` **5.048 seconds after the test observed Ready**. The more precise logger interval from countdown-start to Closed was approximately **5.085 seconds**. Normal USB teardown then logged `stop_complete`.

| UTC timestamp | Event | Instance evidence |
| --- | --- | --- |
| 02:35:57.048 | window created | `window_id=device#dd8d4fe8ca` |
| 02:35:57.154 | window shown | same ID, `handle=3214836` |
| 02:36:01.760 | wired connected | USB, PID 32880 |
| 02:36:01.813 | countdown started | same window ID |
| 02:36:02.911–05.853 | countdown ticks | remaining 4, 3, 2, 1 |
| 02:36:06.872 | countdown completed | same window ID |
| 02:36:06.875 | auto-close requested | same window ID |
| 02:36:06.876 | `Close()` called | same ID/handle, `visible=True`, UI thread 2 |
| 02:36:06.898 | `Closed` | same ID/handle, UI thread 2 |

See [live-verified-test.log](../artifacts/countdown-investigation/live-verified-test.log) and [real-device reverse-control.log](../artifacts/countdown-investigation/live-verified-logs/reverse-control.log).

An earlier harness attempt reached real USB Ready but failed because the console harness had not installed WPF's synchronization context around the toolbar call. The harness was corrected to use `DispatcherSynchronizationContext` and rerun successfully. That failed harness attempt is not counted as a passing device test. Desktop automation also could not reliably distinguish the installed app from the test build; the verified run used the exact built test executable instead.

### Simulated transport, real WPF window/timers

The regression tests simulate status reports only. They show the actual XAML window on an STA dispatcher and run the production timers and close handlers:

- USB, Wireless, and Bluetooth success; USB reports from a worker thread.
- Multiple startup reports queued before the UI applies them—the original failing order.
- Duplicate Ready notifications and repeated Show reuse the visible instance and preserve the deadline.
- Early/missing one-shot callback: reaching zero on the display timer still raises `Closed` exactly once.
- Failed/timeout status cancels success auto-close and remains visible beyond five seconds.
- Unresolved Developer Mode/trust-style action prompt remains open beyond five seconds; resolving it and recovering rearms a full countdown.
- Manual failure close and manual Bluetooth success close work.
- A queued close for an old window does not close its replacement; queued updates after disposal do not reopen the window or access disposed timers.
- `Closed` occurs on the window dispatcher, removes that exact instance from `Application.Windows`, and clears the popup manager's reference.

Results: [verified-regressions.log](../artifacts/countdown-investigation/verified-regressions.log). Diagnostic events: [regression reverse-control.log](../artifacts/countdown-investigation/verified-regression-logs/reverse-control.log).

### Broader repository suite

The general `App.Logic.Tests` suite was also attempted. Its first invocation from the isolated artifacts directory failed because the existing test runner assumes the standard `src/.../bin` layout when locating localization files. Running it from that standard layout with a separate `CountdownVerification` configuration progressed to an unrelated source assertion and failed: `main-window dragging preserves the configured DWM backdrop without a black/white flash` (`Program.cs:1161`). That assertion checks main-window drag/backdrop code, not the countdown. Concurrent UI work affects that area; this task did not alter it. Therefore the broad repository suite is **not** reported as passing. See [logic-verified-tests.log](../artifacts/countdown-investigation/logic-verified-tests.log).

### Code-level checks and limits

The success countdown has no task token that `DisableUsbControlAsync` can prematurely cancel. The timer stops intentionally for pending prompts, non-Ready states, or disposal. There is no `Closing` handler in this status window or its XAML that cancels a successful close. Its `Closed` handler performs cleanup and does not show the window again. Recovery/failure/prompt transitions can intentionally show the shared status window; the success transition does not recreate it. Boss-key hiding and main-window shutdown hiding are separate actions and were not responsible for the reproduced failure.

All prompt types share the pending-prompt guard, so error, trust/pairing, Developer Mode, USB-device, and driver messages do not acquire a success countdown. A representative failure/timeout and user-action prompt were exercised; each physical hardware error was not independently induced. Real Bluetooth/Wireless connections and long-duration USB recovery were not tested here. Their shared status-window behavior was covered with simulated transport reports. The installed application was not replaced by this task.

## Reproduction commands

From the repository root, the recorded build used:

```powershell
dotnet build src/App.Runtime.Tests/IPhoneMirror.App.Runtime.Tests.csproj -c Release -p:UseArtifactsOutput=true -p:ArtifactsPath=C:/Users/Ray/Documents/iphoneMirror/artifacts/countdown-investigation/current
```

Run the built `IPhoneMirror.App.Runtime.Tests.exe` with `--reverse-control-countdown` for the simulated-transport WPF suite, or `--wired-control-live-countdown` for the opt-in real-device integration test. The live test requires exactly one eligible, discovered, bound target, returns code 2 when prerequisites prevent verification, and cleans up the control connection afterward. Set `IPHONE_MIRROR_APP_LOG_DIRECTORY` to an absolute per-run directory to keep diagnostic evidence separate.
