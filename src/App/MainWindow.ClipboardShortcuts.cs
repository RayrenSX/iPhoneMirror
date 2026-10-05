using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly Func<string> _readClipboardText = System.Windows.Clipboard.GetText;

    private bool IsDeviceClipboardShortcutAvailable() => !_mappingClosing &&
        _keyboardRouter.Mode != KeyboardInputMode.None && _mappingWindow?.IsEditing != true &&
        _shortcutSettingsWindow is null && !_mappingCapture.Waiting &&
        CanForwardControlKeyboard(ActiveInputDeviceUdid, GetControlKeyboardWindow(ActiveInputDeviceUdid)) &&
        _viewModel.CanUseDeviceClipboard(ActiveInputDeviceUdid);

    private void QueueDeviceClipboardShortcut(bool copy)
    {
        var route = _viewModel.CaptureDirectKeyboardRoute(ActiveInputDeviceUdid);
        if (route?.PasteTextAsync is null || route.ReadClipboardAsync is null) return;
        var canSend = CaptureKeyboardSendGuard(GetControlKeyboardWindow(route.Target), _keyboardRouter.Mode);
        QueueKeyboardShortcut(() =>
        {
            // Mapping mode can also own an in-flight clipboard operation.
            // Existing handoff cleanup must release/cancel that exact bridge.
            _directKeyboardRoute = route;
            _ = TrackKeyboardSendAsync(SendDeviceClipboardShortcutAsync(route, copy, canSend));
        }, () => IsDeviceClipboardShortcutAvailable() && route.IsCurrent() && canSend());
    }

    private async Task SendDeviceClipboardShortcutAsync(DirectKeyboardRoute route, bool copy, Func<bool> canSend)
    {
        var sessionGate = KeyboardSessionGate(route);
        await _systemShortcutGate.WaitAsync();
        await sessionGate.WaitAsync();
        var commandPressed = false;
        try
        {
            if (!canSend() || !route.IsCurrent()) return;
            if (!copy)
            {
                var text = await ClipboardTextReader.ReadAsync(_readClipboardText,
                    () => canSend() && route.IsCurrent());
                if (!string.IsNullOrEmpty(text)) await route.PasteTextAsync!(text, canSend);
                return;
            }
            // iOS Copy uses Command+C. Send the modifier before the key so
            // the device cannot interpret a literal C before seeing Command.
            commandPressed = true;
            await route.SendAsync(0x08, [0xE3], canSend);
            await Task.Delay(60);
            if (!canSend() || !route.IsCurrent()) return;
            await route.SendAsync(0x08, [0xE3, 0x06], canSend);
            await Task.Delay(80);
            if (!canSend() || !route.IsCurrent()) return;
            await route.SendAsync(0x08, [0xE3], canSend);
            await Task.Delay(60);
            await route.SendAsync(0, [], null);
            commandPressed = false;
            // A repeated copy of the same phone text must still replace a
            // newer Windows copy. The bridge's normal poll only sees changes.
            if (canSend() && route.IsCurrent()) await route.ReadClipboardAsync!(canSend);
        }
        catch (Exception error)
        {
            _viewModel.AddDiagnosticLog(AppLog.Event("clipboard_shortcut_failed",
                ("action", copy ? "copy" : "paste"), ("device", AppLog.Device(route.Target)),
                ("error", AppLog.Error(error))));
        }
        finally
        {
            try
            {
                if (commandPressed) await route.SendAsync(0, [], null);
                await RestoreDirectKeyboardStateAsync(route, canSend);
            }
            catch (Exception error)
            {
                DiagnosticLogger.ReverseControlWarning("keyboard_input", "clipboard_release_failed", ("error", error.Message));
                ResetKeyboardOwnership();
            }
            finally { sessionGate.Release(); _systemShortcutGate.Release(); }
        }
    }

    private Task RestoreDirectKeyboardStateAsync(DirectKeyboardRoute route, Func<bool> canSend)
    {
        if (!canSend() || !route.IsCurrent() || _directKeyboardRoute?.SameSession(route) != true ||
            (_controlKeyboardUsages.Count == 0 && _controlModifierKeys.Count == 0)) return Task.CompletedTask;
        var held = route.Transport == "BluetoothDirect" ? _controlKeyboardUsages.ToArray() :
            _controlKeyboardUsages.Concat(ModifierUsages(_controlModifierKeys)).ToArray();
        return route.SendAsync(_controlKeyboardModifiers, held, canSend);
    }
}
