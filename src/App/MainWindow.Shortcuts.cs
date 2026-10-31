using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly HashSet<(string Device, byte Button)> _mouseShortcutButtons = [];
    private readonly SemaphoreSlim _systemShortcutGate = new(1, 1);

    private bool CanSendDockShortcut(string? target)
    {
        // Globe+A is the iPad Dock command. On iPhone the same report can
        // become an ordinary A in the focused app. Use the actual target's
        // model, not a different device currently selected in the main list.
        var device = _viewModel.Devices.FirstOrDefault(candidate =>
            Models.DeviceViewModel.UdidEquals(candidate.Udid, target));
        if (device is null && Models.DeviceViewModel.UdidEquals(
                _viewModel.SelectedDevice?.Udid, target)) device = _viewModel.SelectedDevice;
        return device?.ProductType.StartsWith("iPad", StringComparison.OrdinalIgnoreCase) == true;
    }

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

    private SemaphoreSlim KeyboardSessionGate(DirectKeyboardRoute route) =>
        _viewModel.DeviceInput.KeyboardGate(route);

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
        // Keep the complete system chord on one ordered Indigo connection.
        const ushort page = CoreDeviceTouchProtocol.IndigoConsumerUsagePage;
        const ushort globe = CoreDeviceTouchProtocol.GlobeKeyboardLayoutUsage;
        if (route.SendButtonAsync is null) return;
        try
        {
            await route.SendButtonAsync(page, globe, "down", canSend);
            // The bridge also preserves these intervals at the actual send
            // boundary if host frames were queued behind a slow operation.
            await Task.Delay(60);
            if (!canSend()) return;
            await route.SendButtonAsync(CoreDeviceTouchProtocol.IndigoKeyboardUsagePage, usage, "down", canSend);
            await Task.Delay(80);
        }
        finally
        {
            // Both releases must be attempted even if either transport call
            // fails, and neither may inherit the expired focus guard.
            try { await route.SendButtonAsync(CoreDeviceTouchProtocol.IndigoKeyboardUsagePage, usage, "up", null); }
            finally
            {
                await Task.Delay(20);
                await route.SendButtonAsync(page, globe, "up", null);
            }
        }
    }
}
