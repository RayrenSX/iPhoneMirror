using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly KeyboardInputRouter _keyboardRouter = new();
    private Task _keyboardHandoff = Task.CompletedTask;
    private readonly HashSet<Task> _keyboardSends = [];
    private DirectKeyboardRoute? _directKeyboardRoute;
    private DirectKeyboardRoute? _unreleasedKeyboardRoute;
    private KeyboardInputMode CurrentKeyboardInputMode => _keyboardRouter.Mode;
    private long KeyboardInputModeGeneration => _keyboardRouter.Generation;
    private bool IsKeyboardMappingInputModeActive =>
        _keyboardRouter.RequestedMode == KeyboardInputMode.Mapping;
    private bool IsDirectKeyboardInputModeActive => _keyboardRouter.Mode == KeyboardInputMode.Direct;

    private bool TryEnterDirectKeyboardInputMode()
    {
        if (IsKeyboardMappingInputModeActive || _mappingClosing) return false;
        if (_keyboardRouter.RequestedMode != KeyboardInputMode.Direct)
            ChangeKeyboardOwner(KeyboardInputMode.Direct);
        return IsDirectKeyboardInputModeActive;
    }

    private bool TryEnterKeyboardMappingInputMode()
    {
        if (_mappingClosing) return false;
        if (!IsKeyboardMappingInputModeActive)
            ChangeKeyboardOwner(KeyboardInputMode.Mapping, sampleHeldKeys: true);
        return true;
    }

    private void LeaveKeyboardMappingInputMode()
    {
        if (IsKeyboardMappingInputModeActive)
            ChangeKeyboardOwner(_mappingClosing ? KeyboardInputMode.None : KeyboardInputMode.Direct,
                sampleHeldKeys: true);
    }

    private void LeaveDirectKeyboardInputMode()
    {
        if (_keyboardRouter.RequestedMode == KeyboardInputMode.Direct)
            ChangeKeyboardOwner(KeyboardInputMode.None);
    }

    private void ResetKeyboardOwnership() => ChangeKeyboardOwner(_keyboardRouter.RequestedMode);

    private void ChangeKeyboardOwner(KeyboardInputMode next, bool sampleHeldKeys = false)
    {
        Dispatcher.VerifyAccess();
        var previous = _keyboardRouter.Mode;
        var generation = _keyboardRouter.BeginHandoff(next, sampleHeldKeys
            ? Enumerable.Range(8, 248).Where(k => k is not (0x10 or 0x11 or 0x12) && GetAsyncKeyState(k) < 0)
            : null);
        PublishKeyboardInputMode(KeyboardInputMode.None);
        if (next == KeyboardInputMode.Mapping) RegisterRawInput(_rawMouseInputEnabled, false);
        Interlocked.Increment(ref _keyboardInputGeneration);
        CancelMappedGesture();
        _controlKeyboardUsages.Clear();
        _controlModifierKeys.Clear();
        _controlKeyboardModifiers = 0;
        _pasteVPending = false;
        var oldRoute = _directKeyboardRoute;
        _directKeyboardRoute = null;
        var pending = Task.WhenAll(_keyboardSends.ToArray());
        _keyboardHandoff = FinishKeyboardHandoffAsync(_keyboardHandoff, pending,
            Task.WhenAll(_mappingExecutor.Completion, _mappingContinuous.Completion), oldRoute, generation);
        DiagnosticLogger.ReverseControl("keyboard_input", "owner_changing",
            ("from", previous), ("to", next), ("generation", generation),
            ("device", AppLog.Device(oldRoute?.Target)), ("transport", oldRoute?.Transport));
        RefreshDeviceHotkeys();
        ReconcileKeyboardHook();
    }

    private async Task FinishKeyboardHandoffAsync(Task previous, Task pending, Task mapping,
        DirectKeyboardRoute? oldRoute, long generation)
    {
        try
        {
            await previous;
            // A press which already entered a writer finishes before its empty
            // release. The next owner stays closed for the entire drain.
            try { await Task.WhenAll(pending, mapping); }
            catch (Exception error)
            {
                DiagnosticLogger.ReverseControlWarning("keyboard_input", "drain_failed", ("error", error.Message));
            }
            await _systemShortcutGate.WaitAsync();
            try
            {
                oldRoute ??= _unreleasedKeyboardRoute;
                if (oldRoute?.IsCurrent() == true)
                    await oldRoute.SendAsync(0, [], null);
                _unreleasedKeyboardRoute = null;
            }
            finally { _systemShortcutGate.Release(); }
            if (!_keyboardRouter.CompleteHandoff(generation)) return;
            if (!_mappingClosing)
                RegisterRawInput(IsBluetoothControlActive && _activeControlWindow == 0,
                    _keyboardRouter.Mode == KeyboardInputMode.Direct &&
                    (IsBluetoothControlActive || IsUsbControlActive) && _activeControlWindow == 0);
            PublishKeyboardInputMode(_keyboardRouter.Mode);
            RefreshDeviceHotkeys();
            ReconcileKeyboardHook();
            DiagnosticLogger.ReverseControl("keyboard_input", "owner_ready",
                ("owner", _keyboardRouter.Mode), ("generation", generation));
        }
        catch (Exception error)
        {
            _unreleasedKeyboardRoute = oldRoute;
            // A failed release must not open another owner over a stuck key.
            DiagnosticLogger.ReverseControlWarning("keyboard_input", "release_failed",
                ("device", AppLog.Device(oldRoute?.Target)), ("error", error.Message));
        }
    }

    private async Task TrackKeyboardSendAsync(Task send)
    {
        _keyboardSends.Add(send);
        try { await send; }
        finally { _keyboardSends.Remove(send); }
    }

    private void PublishKeyboardInputMode(KeyboardInputMode mode) =>
        _viewModel.SetKeyboardInputModeStatus(mode switch
        {
            KeyboardInputMode.Mapping => "KeyboardInputModeMapping",
            KeyboardInputMode.Direct => "KeyboardInputModeDirect",
            _ => "KeyboardInputModeNone",
        });
}
