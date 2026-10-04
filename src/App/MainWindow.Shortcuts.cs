using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly HashSet<(string Device, byte Button)> _mouseShortcutButtons = [];
    private readonly HashSet<int> _shortcutKeysDown = [];
    private readonly HashSet<int> _ordinaryKeysDown = [];
    private readonly SemaphoreSlim _systemShortcutGate = new(1, 1);

    private bool TryHandlePointerShortcut(PreviewPointerEventArgs e, string? udid)
    {
        var identity = (udid?.ToUpperInvariant() ?? string.Empty, e.Button);
        if (e.Kind == PreviewPointerKind.Reset)
        {
            _mouseShortcutButtons.RemoveWhere(item => item.Device == identity.Item1);
            return false;
        }
        if (e.Kind == PreviewPointerKind.ButtonUp)
            return _mouseShortcutButtons.Remove(identity);
        if (e.Kind != PreviewPointerKind.ButtonDown ||
            !CanForwardControlKeyboard(udid, GetControlKeyboardWindow(udid))) return false;
        if (_mouseShortcutButtons.Contains(identity)) return true;
        if (!TryHandleMouseShortcut(e.Button)) return false;
        // Swallow the matching release even if the user releases Ctrl/Alt
        // first. Otherwise a navigation click can release an unrelated touch.
        _mouseShortcutButtons.Add(identity);
        return true;
    }

    private bool TryHandleConfiguredKey(int virtualKey, bool down)
    {
        if (IsKeyboardMappingInputModeActive) return false;
        // A press keeps its original disposition until release, even if
        // modifiers change during auto-repeat or before key-up.
        if (!down)
        {
            _ordinaryKeysDown.Remove(virtualKey);
            return _shortcutKeysDown.Remove(virtualKey);
        }
        if (_shortcutKeysDown.Contains(virtualKey)) return true;
        if (_ordinaryKeysDown.Contains(virtualKey)) return false;
        if (_shortcutSettingsWindow is not null ||
            !TryGetShortcutAction(virtualKey, out var action))
        {
            _ordinaryKeysDown.Add(virtualKey);
            return false;
        }
        _shortcutKeysDown.Add(virtualKey);
        if (!_registeredHotKeyIds.Contains(HotKeyId(action)))
            HandleConfiguredShortcut(action);
        return true;
    }

    private async Task SendUsbSystemShortcutAsync(byte usage, DirectKeyboardRoute route,
        Func<bool> canSend)
    {
        // Globe is a Consumer-page control, not a keyboard modifier bit.
        // Keep it pressed while the ordinary keyboard usage is delivered.
        const ushort page = CoreDeviceTouchProtocol.IndigoConsumerUsagePage;
        const ushort globe = CoreDeviceTouchProtocol.GlobeKeyboardLayoutUsage;
        if (route.SendButtonAsync is null) return;
        try
        {
            await route.SendButtonAsync(page, globe, "down", canSend);
            if (!canSend()) return;
            await route.SendAsync(0, [usage], canSend);
            await Task.Delay(20);
        }
        finally
        {
            // Both releases must be attempted even if either transport call
            // fails, and neither may inherit the expired focus guard.
            var keyboardRelease = route.SendAsync(0, [], null);
            var globeRelease = route.SendButtonAsync(page, globe, "up", null);
            await Task.WhenAll(keyboardRelease, globeRelease);
        }
    }
}
