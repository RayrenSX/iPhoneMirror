using System.Runtime.InteropServices;
using System.Windows;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly Dictionary<Guid, (nint Window, string? Device, Action Restore)> _settingShortcutOverrides = [];
    private nint _settingMouseHook;
    private LowLevelKeyboardProc? _settingMouseHookProc;

    private IReadOnlyList<SettingShortcutBinding> ConfiguredSettingShortcuts =>
        (Application.Current as App)?.UpdateSettings.SettingShortcuts ?? [];

    private IEnumerable<KeyboardShortcut> AllConfiguredShortcutKeys =>
        GetConfiguredShortcuts().Values.Concat(ConfiguredSettingShortcuts.Select(binding => binding.Shortcut));

    private void ConfigureSettingShortcutBindings(List<RoutedShortcut> bindings)
    {
        ReleaseSettingShortcutOverrides();
        foreach (var binding in ConfiguredSettingShortcuts.Where(binding => binding.IsValid))
        {
            bool Available() => !_mappingClosing && !_bossKeyHidden && _shortcutSettingsWindow is null &&
                _mappingWindow?.IsEditing != true && !_mappingCapture.Waiting &&
                _secondaryMirrors.ContainsWindow(_keyboardForegroundWindow());
            bindings.Add(new(binding.Shortcut, $"Setting:{binding.Id}", Available, () =>
            {
                var window = _keyboardForegroundWindow();
                if (Available() && !_settingShortcutOverrides.ContainsKey(binding.Id) &&
                    _secondaryMirrors.BeginTemporarySetting(window, binding) is { } restore)
                    _settingShortcutOverrides[binding.Id] = (window, _secondaryMirrors.DeviceForWindow(window), restore);
            }, () =>
            {
                if (_settingShortcutOverrides.Remove(binding.Id, out var held)) held.Restore();
            }));
        }
    }

    private void ReleaseSettingShortcutOverrides(nint window = 0, string? device = null)
    {
        foreach (var (id, held) in _settingShortcutOverrides.ToArray())
            if ((window == 0 || held.Window == window) &&
                (device is null || string.Equals(held.Device, device, StringComparison.OrdinalIgnoreCase)))
            {
                _settingShortcutOverrides.Remove(id);
                held.Restore();
            }
    }

    private bool ProcessSettingMouseButton(int virtualKey, bool down)
    {
        ObserveKeyboardForeground();
        if (!down) return _keyboardRouter.ReleaseMouseShortcut(virtualKey);
        // The global mouse adapter handles only the additional window settings.
        // Existing phone shortcuts retain their preview-specific input paths.
        if (!ConfiguredSettingShortcuts.Any(binding => binding.VirtualKey == virtualKey && binding.IsValid &&
            binding.Shortcut.Modifiers == _keyboardRouter.PressedModifiers)) return false;
        return _keyboardRouter.RouteShortcutTrigger(virtualKey);
    }

    private void ReconcileSettingMouseHook()
    {
        var needed = !_mappingClosing && Application.Current is App { IsUiPreviewMode: false } &&
            ConfiguredSettingShortcuts.Any(binding => binding.IsValid && binding.Shortcut.IsMouse && binding.Shortcut.IsBound);
        if (!needed)
        {
            if (_settingMouseHook != 0) UnhookWindowsHookEx(_settingMouseHook);
            _settingMouseHook = 0;
            return;
        }
        if (_settingMouseHook != 0) return;
        _settingMouseHookProc ??= (code, message, data) =>
        {
            if (code >= 0)
            {
                var mouse = Marshal.PtrToStructure<SettingMouseData>(data);
                var key = (int)message switch
                {
                    0x204 or 0x205 => (int)KeyboardShortcut.MouseRight,
                    0x207 or 0x208 => (int)KeyboardShortcut.MouseMiddle,
                    0x20B or 0x20C => (mouse.Data >> 16) == 1
                        ? (int)KeyboardShortcut.MouseXButton1 : (int)KeyboardShortcut.MouseXButton2,
                    _ => 0,
                };
                if (key != 0 && ProcessSettingMouseButton(key, (int)message is 0x204 or 0x207 or 0x20B)) return 1;
            }
            return CallNextHookEx(_settingMouseHook, code, message, data);
        };
        _settingMouseHook = SetWindowsHookEx(14, _settingMouseHookProc, GetModuleHandle(null), 0);
        if (_settingMouseHook == 0)
            DiagnosticLogger.ReverseControlWarning("setting_shortcuts", "mouse_hook_install_failed",
                ("error", Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SettingMouseData
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint Extra;
    }
}
