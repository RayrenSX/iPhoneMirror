using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class ShortcutSettingsWindow
{
    public ObservableCollection<SettingShortcutRow> SettingRows { get; } = [];
    public bool CanAddSettingShortcut => SettingRows.Count < 16;

    private void OnAddSettingShortcut(object sender, RoutedEventArgs e)
    {
        if (SettingRows.Count >= 16) return;
        SettingRows.Add(new(new SettingShortcutBinding { Trigger = SettingShortcutTrigger.Toggle }));
        OnPropertyChanged(nameof(CanAddSettingShortcut));
    }

    private void OnRemoveSettingShortcut(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SettingShortcutRow row) SettingRows.Remove(row);
        OnPropertyChanged(nameof(CanAddSettingShortcut));
        StatusText = string.Empty;
    }

    internal bool TryAssignSettingShortcut(SettingShortcutRow row, KeyboardShortcut shortcut)
    {
        if (!KeyboardShortcut.IsValid((int)shortcut.Modifiers, (int)shortcut.VirtualKey))
        {
            StatusText = LocalizationService.Get("ShortcutSettingsInvalid");
            return false;
        }
        if (shortcut.IsBound && (Rows.Any(other => other.Shortcut == shortcut) ||
            SettingRows.Any(other => other != row && other.Shortcut == shortcut)))
        {
            StatusText = LocalizationService.Get("ShortcutSettingsDuplicate");
            return false;
        }
        row.SetShortcut(shortcut);
        StatusText = string.Empty;
        return true;
    }

    private void CaptureSettingKey(SettingShortcutRow row, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is Key.Tab or Key.Escape) return;
        if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
            TryAssignSettingShortcut(row, KeyboardShortcut.Unbound);
        else if (KeyboardShortcut.TryCreate(key, Keyboard.Modifiers, out var shortcut))
            TryAssignSettingShortcut(row, shortcut);
        else StatusText = LocalizationService.Get("ShortcutSettingsInvalid");
        e.Handled = true;
    }

    private void CaptureSettingMouse(SettingShortcutRow row, MouseButtonEventArgs e)
    {
        var button = e.ChangedButton switch
        {
            MouseButton.Right => ShortcutMouseButton.Right,
            MouseButton.Middle => ShortcutMouseButton.Middle,
            MouseButton.XButton1 => ShortcutMouseButton.XButton1,
            MouseButton.XButton2 => ShortcutMouseButton.XButton2,
            _ => ShortcutMouseButton.None,
        };
        if (!KeyboardShortcut.TryCreateMouse(button, Keyboard.Modifiers, out var shortcut)) return;
        TryAssignSettingShortcut(row, shortcut);
        e.Handled = true;
    }
}

public sealed class SettingShortcutRow : INotifyPropertyChanged
{
    private SettingShortcutBinding _binding;
    internal SettingShortcutRow(SettingShortcutBinding binding) => _binding = binding with { };
    public IReadOnlyList<string> SettingLabels =>
        [LocalizationService.Get("IndependentWindowOpacity"), LocalizationService.Get("IndependentWindowPin")];
    public string CardTitle => SettingLabels[SettingIndex];
    public IReadOnlyList<string> TriggerLabels =>
        [LocalizationService.Get("SettingShortcutsHold"), LocalizationService.Get("SettingShortcutsToggle")];
    public int TriggerIndex
    {
        get => (int)_binding.Trigger;
        set
        {
            if (value is < 0 or > 1 || value == TriggerIndex) return;
            _binding = _binding with { Trigger = (SettingShortcutTrigger)value };
            RefreshLabels();
        }
    }
    public string TriggerDescription => LocalizationService.Get(TriggerIndex == 1
        ? "SettingShortcutsToggleHint" : "SettingShortcutsHoldHint");
    public string TargetLabel => LocalizationService.Get(TriggerIndex == 1
        ? "SettingShortcutsToggleValue" : "SettingShortcutsHeldValue");
    public string BooleanLabel => LocalizationService.Get(TriggerIndex == 1
        ? "SettingShortcutsToggleEnabled" : "SettingShortcutsHeldEnabled");
    public int SettingIndex
    {
        get => (int)_binding.Setting;
        set
        {
            if (value is < 0 or > 1 || value == SettingIndex) return;
            _binding = _binding with { Setting = (PreviewShortcutSetting)value, Value = value == 0 ? 100 : 1 };
            RefreshLabels();
        }
    }
    public double Value
    {
        get => SettingIndex == 0 ? _binding.Value : 100;
        set
        {
            if (SettingIndex != 0 || !double.IsFinite(value)) return;
            _binding = _binding with { Value = Math.Clamp(Math.Round(value), 10, 100) };
            Notify(nameof(Value));
        }
    }
    public bool BooleanValue
    {
        get => _binding.Value != 0;
        set
        {
            if (SettingIndex != 1) return;
            _binding = _binding with { Value = value ? 1 : 0 };
            Notify(nameof(BooleanValue));
        }
    }
    public Visibility OpacityVisibility => SettingIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BooleanVisibility => SettingIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    internal KeyboardShortcut Shortcut => _binding.Shortcut;
    public bool IsBound => Shortcut.IsBound;
    public string ShortcutText => Shortcut.DisplayText;
    internal void SetShortcut(KeyboardShortcut shortcut)
    {
        _binding = _binding with { Modifiers = (int)shortcut.Modifiers, VirtualKey = (int)shortcut.VirtualKey };
        Notify(nameof(ShortcutText));
        Notify(nameof(IsBound));
    }
    internal SettingShortcutBinding ToBinding() => _binding with { };
    internal void RefreshLabels()
    {
        foreach (var name in new[] { nameof(SettingLabels), nameof(CardTitle), nameof(SettingIndex), nameof(Value),
            nameof(TriggerLabels), nameof(TriggerIndex), nameof(TriggerDescription), nameof(TargetLabel), nameof(BooleanLabel),
            nameof(BooleanValue), nameof(OpacityVisibility), nameof(BooleanVisibility), nameof(ShortcutText) }) Notify(name);
    }
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
