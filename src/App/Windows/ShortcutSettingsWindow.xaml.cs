using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class ShortcutSettingsWindow : Wpf.Ui.Controls.FluentWindow,
    INotifyPropertyChanged
{
    private readonly Func<IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut>, string?> _apply;
    private string _statusText = string.Empty;
    public ObservableCollection<ShortcutBindingRow> Rows { get; } = [];
    public ObservableCollection<ShortcutBindingSection> Sections { get; } = [];

    internal ShortcutSettingsWindow(
        IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut> shortcuts,
        Func<IReadOnlyDictionary<BluetoothShortcutAction, KeyboardShortcut>, string?> apply)
    {
        _apply = apply;
        foreach (var action in Enum.GetValues<BluetoothShortcutAction>().Where(
            action => action != BluetoothShortcutAction.ReverseControl))
        {
            var row = new ShortcutBindingRow(action,
                shortcuts.TryGetValue(action, out var shortcut)
                    ? shortcut : KeyboardShortcut.DefaultFor(action));
            Rows.Add(row);
            var category = GetCategory(action);
            var section = Sections.FirstOrDefault(candidate =>
                candidate.Category == category);
            if (section is null)
            {
                section = new ShortcutBindingSection(category);
                Sections.Add(section);
            }
            section.Rows.Add(row);
        }
        InitializeComponent();
        DataContext = this;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal)) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnShortcutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutBindingRow row) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            row.Shortcut = KeyboardShortcut.Unbound;
            StatusText = string.Empty;
            e.Handled = true;
            return;
        }
        if (!KeyboardShortcut.TryCreate(key, Keyboard.Modifiers, out var shortcut))
        {
            StatusText = LocalizationService.Get("ShortcutSettingsInvalid");
            e.Handled = true;
            return;
        }

        row.Shortcut = shortcut;
        StatusText = string.Empty;
        e.Handled = true;
    }

    private void OnShortcutPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutBindingRow row) return;
        var button = e.ChangedButton switch
        {
            MouseButton.Right => ShortcutMouseButton.Right,
            MouseButton.Middle => ShortcutMouseButton.Middle,
            _ => ShortcutMouseButton.None,
        };
        if (!KeyboardShortcut.TryCreateMouse(button, Keyboard.Modifiers,
                out var shortcut))
            return;
        row.Shortcut = shortcut;
        StatusText = string.Empty;
        e.Handled = true;
    }

    private void OnResetRowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutBindingRow row) return;
        row.Shortcut = row.DefaultShortcut;
        StatusText = string.Empty;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var shortcuts = Rows.ToDictionary(row => row.Action, row => row.Shortcut);
        if (!KeyboardShortcut.HaveUniqueBoundValues(shortcuts.Values))
        {
            StatusText = LocalizationService.Get("ShortcutSettingsDuplicate");
            return;
        }
        var error = _apply(shortcuts);
        if (!string.IsNullOrWhiteSpace(error))
        {
            StatusText = error;
            return;
        }

        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var section in Sections) section.RefreshTitle();
        foreach (var row in Rows) row.RefreshLabel();
        StatusText = string.Empty;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static ShortcutBindingCategory GetCategory(BluetoothShortcutAction action) =>
        action switch
        {
            BluetoothShortcutAction.BluetoothControl or
            BluetoothShortcutAction.WirelessControl or
            BluetoothShortcutAction.WiredControl or
            BluetoothShortcutAction.VolumeUp or
            BluetoothShortcutAction.VolumeDown or
            BluetoothShortcutAction.LockScreen => ShortcutBindingCategory.Control,
            BluetoothShortcutAction.BossKey => ShortcutBindingCategory.Control,
            BluetoothShortcutAction.Siri => ShortcutBindingCategory.Assistant,
            _ => ShortcutBindingCategory.Navigation,
        };
}

public enum ShortcutBindingCategory
{
    Control,
    Navigation,
    Assistant,
}

public sealed class ShortcutBindingSection : INotifyPropertyChanged
{
    internal ShortcutBindingSection(ShortcutBindingCategory category)
    {
        Category = category;
        RefreshTitle();
    }

    internal ShortcutBindingCategory Category { get; }
    public ObservableCollection<ShortcutBindingRow> Rows { get; } = [];
    public string Title { get; private set; } = string.Empty;

    internal void RefreshTitle()
    {
        Title = LocalizationService.Get(Category switch
        {
            ShortcutBindingCategory.Control => "ShortcutSettingsCategoryControl",
            ShortcutBindingCategory.Navigation => "ShortcutSettingsCategoryNavigation",
            ShortcutBindingCategory.Assistant => "ShortcutSettingsCategoryAssistant",
            _ => "ShortcutSettingsCategoryControl",
        });
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ShortcutBindingRow : INotifyPropertyChanged
{
    private KeyboardShortcut _shortcut;
    internal ShortcutBindingRow(BluetoothShortcutAction action, KeyboardShortcut shortcut)
    {
        Action = action;
        DefaultShortcut = KeyboardShortcut.DefaultFor(action);
        _shortcut = shortcut;
        RefreshLabel();
    }
    internal BluetoothShortcutAction Action { get; }
    internal KeyboardShortcut DefaultShortcut { get; }
    public string Label { get; private set; } = string.Empty;
    public string ShortcutText => _shortcut.DisplayText;
    public bool IsBound => _shortcut.IsBound;
    internal KeyboardShortcut Shortcut
    {
        get => _shortcut;
        set
        {
            if (_shortcut == value) return;
            _shortcut = value;
            OnPropertyChanged(nameof(ShortcutText));
            OnPropertyChanged(nameof(IsBound));
        }
    }
    internal void RefreshLabel()
    {
        Label = LocalizationService.Get(Action switch
        {
            BluetoothShortcutAction.BluetoothControl => "ShortcutSettingsBluetoothControl",
            BluetoothShortcutAction.WirelessControl => "ShortcutSettingsWirelessControl",
            BluetoothShortcutAction.WiredControl => "ShortcutSettingsWiredControl",
            BluetoothShortcutAction.ControlCenter => "ShortcutSettingsControlCenter",
            BluetoothShortcutAction.NotificationCenter => "ShortcutSettingsNotificationCenter",
            BluetoothShortcutAction.AppSwitcher => "ShortcutSettingsAppSwitcher",
            BluetoothShortcutAction.Home => "ShortcutSettingsHome",
            BluetoothShortcutAction.BossKey => "ShortcutSettingsBossKey",
            BluetoothShortcutAction.Dock => "ShortcutSettingsDock",
            BluetoothShortcutAction.Siri => "ShortcutSettingsSiri",
            BluetoothShortcutAction.VolumeUp => "ShortcutSettingsVolumeUp",
            BluetoothShortcutAction.VolumeDown => "ShortcutSettingsVolumeDown",
            BluetoothShortcutAction.LockScreen => "ShortcutSettingsLockScreen",
            _ => "ShortcutSettingsBluetoothControl",
        });
        OnPropertyChanged(nameof(Label));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new(name));
}
