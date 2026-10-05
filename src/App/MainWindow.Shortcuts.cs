using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly HashSet<(string Device, byte Button)> _mouseShortcutButtons = [];
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

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SemaphoreSlim> _keyboardSessionGates = new();
    private SemaphoreSlim KeyboardSessionGate(DirectKeyboardRoute route) =>
        _keyboardSessionGates.GetValue(route.Session, _ => new SemaphoreSlim(1, 1));

    private async Task SendRoutedKeyboardAsync(DirectKeyboardRoute route, byte modifiers,
        byte[] usages, Func<bool> canSend)
    {
        var gate = KeyboardSessionGate(route);
        await gate.WaitAsync();
        try
        {
            if (canSend() && route.IsCurrent()) await route.SendAsync(modifiers, usages, canSend);
        }
        finally { gate.Release(); }
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
