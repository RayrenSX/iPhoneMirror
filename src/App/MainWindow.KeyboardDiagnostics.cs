using System.Windows.Threading;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly int[] _functionKeyDownCounts = new int[10];
    private long _functionKeySequence;
    private readonly Queue<(string, object?)[]> _functionKeyDiagnostics = new();
    private bool _functionKeyDiagnosticDrainPending;
    private int _functionKeyDiagnosticsDropped;

    private Action<KeyboardRouteDecision>? ObserveFunctionKey(MappedKey key, bool down,
        string? target, string source, uint flags = 0)
    {
        if (key.VirtualKey is < 0x70 or > 0x79) return null;
        var sequence = ++_functionKeySequence;
        var index = key.VirtualKey - 0x70;
        var count = _functionKeyDownCounts[index];
        if (down) _functionKeyDownCounts[index] = ++count;
        else _functionKeyDownCounts[index] = 0;
        // Keep the first repeat (including a stale lifetime), periodic hold
        // samples, and every up. Do not queue a disk write per auto-repeat.
        if (down && count > 2 && count % 64 != 0) return null;
        var window = GetControlKeyboardWindow(target);
        var fields = new (string, object?)[] {
            ("sequence", sequence), ("observed_at", DateTimeOffset.UtcNow.ToString("O")),
            ("source", source), ("virtual_key", key.VirtualKey), ("down", down),
            ("scan_code", key.ScanCode), ("extended", key.Extended), ("flags", flags),
            ("downs_since_up", count), ("key_tracked", _keyboardRouter.HasPressedKey(key)),
            ("modifiers", _keyboardRouter.PressedModifiers),
            ("owner", _keyboardRouter.Mode), ("requested_owner", _keyboardRouter.RequestedMode),
            ("generation", _keyboardRouter.Generation), ("handoff_pending", !_keyboardHandoff.IsCompleted),
            ("send_pending", _keyboardSends.Count), ("shortcut_busy", _systemShortcutGate.CurrentCount == 0),
            ("focus_suspended", _keyboardFocusSuspended), ("foreground", _keyboardForegroundWindow()),
            ("target_window", window), ("mapping_editor", _mappingWindow?.IsEditing == true),
            ("window_allows_control", _secondaryMirrors.AllowsControlInput(window)),
            ("main_editor", window == _windowSource?.Handle && IsMainKeyboardEditorFocused()),
            ("shortcut_settings", _shortcutSettingsWindow is not null), ("capture", _mappingCapture.Waiting),
            ("closing", _mappingClosing), ("boss_hidden", _bossKeyHidden),
            ("human_allowed", _viewModel.AutomationHumanAllowed(target)),
            ("allowed", CanForwardControlKeyboard(target, window)) };
        return decision => EnqueueFunctionKeyDiagnostic([
            .. fields, ("decision", decision.Reason), ("route_owner", decision.Owner),
            ("shortcut", decision.Shortcut), ("generation_after", _keyboardRouter.Generation)]);
    }

    private void EnqueueFunctionKeyDiagnostic((string, object?)[] fields)
    {
        // The hook only captures bounded snapshots. File I/O runs after the
        // callback returns, and a busy dispatcher cannot grow this queue forever.
        if (_functionKeyDiagnostics.Count == 256)
        {
            _functionKeyDiagnostics.Dequeue();
            _functionKeyDiagnosticsDropped++;
        }
        _functionKeyDiagnostics.Enqueue(fields);
        if (_functionKeyDiagnosticDrainPending) return;
        _functionKeyDiagnosticDrainPending = true;
        Dispatcher.BeginInvoke(DrainFunctionKeyDiagnostics, DispatcherPriority.Background);
    }

    private void DrainFunctionKeyDiagnostics()
    {
        if (_functionKeyDiagnosticsDropped != 0)
        {
            DiagnosticLogger.ReverseControl("keyboard_input", "function_key_diagnostics_dropped",
                ("count", _functionKeyDiagnosticsDropped));
            _functionKeyDiagnosticsDropped = 0;
        }
        // Yield between small batches so diagnostics cannot monopolize the
        // dispatcher that also services the native keyboard hook.
        for (var count = 0; count < 16 && _functionKeyDiagnostics.TryDequeue(out var fields); count++)
            DiagnosticLogger.ReverseControl("keyboard_input", "function_key_route", fields);
        if (_functionKeyDiagnostics.Count != 0)
            Dispatcher.BeginInvoke(DrainFunctionKeyDiagnostics, DispatcherPriority.Background);
        else _functionKeyDiagnosticDrainPending = false;
    }
}
