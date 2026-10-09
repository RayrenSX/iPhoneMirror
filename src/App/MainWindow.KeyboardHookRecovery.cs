using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private DispatcherTimer? _keyboardHookRefreshTimer;
    private readonly Func<int, bool> _isPhysicalKeyboardKeyDown = key => GetAsyncKeyState(key) < 0;
    private bool IsKeyboardHookNeeded => !_mappingClosing &&
        (Application.Current is not App { IsUiPreviewMode: true } ||
         _mappingSettings.Enabled || _mappingCapture.Waiting || _mappingCapture.HasHeldKeys);

    private void SetKeyboardHookRefreshEnabled(bool enabled)
    {
        if (!enabled)
        {
            _keyboardHookRefreshTimer?.Stop();
            return;
        }
        if (_keyboardHookRefreshTimer is null)
        {
            _keyboardHookRefreshTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            _keyboardHookRefreshTimer.Tick += (_, _) => RefreshKeyboardHook();
        }
        _keyboardHookRefreshTimer.Start();
    }

    private void RefreshKeyboardHook()
    {
        Dispatcher.VerifyAccess();
        if (!IsKeyboardHookNeeded)
        {
            ReconcileKeyboardHook();
            return;
        }

        // Windows can silently remove WH_KEYBOARD_LL after a dispatcher stall.
        // The saved HHOOK stays nonzero, and there is no API to query its health.
        // Renew the subscription even while idle; both shortcuts and direct
        // input otherwise keep trusting that stale handle indefinitely.
        var replacement = InstallKeyboardHook();
        if (replacement == 0) return; // retain the old subscription and retry
        var previous = _keyboardHook;
        _keyboardHook = replacement;
        // No dispatcher yield between install and removal: healthy renewal
        // preserves held keys/chords without delivering an event twice.
        if (previous != 0 && previous != replacement && UnhookWindowsHookEx(previous)) return;
        var error = previous == 0 || previous == replacement ? 0 : Marshal.GetLastWin32Error();
        // Sample outside the hook callback, after Windows updates async key
        // state. A key-up lost while the hook was missing will never be replayed.
        var held = Enumerable.Range(8, 248)
            .Where(key => key is not (0x10 or 0x11 or 0x12) && _isPhysicalKeyboardKeyDown(key))
            .ToHashSet();
        // Release device state before pruning lost lifetimes. Still-held keys,
        // including presses missed during the outage, wait for physical up.
        ResetKeyboardOwnership();
        _keyboardRouter.ReconcileRetiredKeys(held);
        _mappingCapture.ReconcilePhysicalKeys(held);
        DiagnosticLogger.ReverseControl("keyboard_input", "hook_restored",
            ("previous_missing", previous != 0), ("error", error));
    }
}
