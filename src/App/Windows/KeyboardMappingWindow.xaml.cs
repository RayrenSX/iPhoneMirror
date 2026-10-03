using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class KeyboardMappingWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private KeyboardMappingSettings _settings;
    private readonly Func<KeyboardMappingSettings, string?> _apply;
    private readonly Func<MappedKey, string?> _conflict;
    private readonly Func<Action<MappedKey>, string?> _beginCapture;
    private readonly Action _endCapture;
    private readonly Func<string> _runtimeStatus;
    private readonly Func<string?> _targetName;
    private readonly Func<KeyboardMappingEntry, Action<KeyboardMappingEntry?>, string?>? _beginPick;
    private readonly Action? _cancelPick;
    private KeyboardMappingEditorWindow? _editor;
    private string _statusKey = "MappingOff";
    public ObservableCollection<KeyboardMappingRow> Rows { get; } = [];
    internal KeyboardMappingWindow(KeyboardMappingSettings settings,
        Func<KeyboardMappingSettings, string?> apply, Func<MappedKey, string?> conflict,
        Func<Action<MappedKey>, string?> beginCapture, Action endCapture, Func<string> runtimeStatus,
        Func<string?>? targetName = null,
        Func<KeyboardMappingEntry, Action<KeyboardMappingEntry?>, string?>? beginPick = null, Action? cancelPick = null)
    {
        _settings = settings.Clone();
        (_apply, _conflict, _beginCapture, _endCapture, _runtimeStatus) =
            (apply, conflict, beginCapture, endCapture, runtimeStatus);
        _targetName = targetName ?? (() => null);
        _beginPick = beginPick;
        _cancelPick = cancelPick;
        InitializeComponent();
        DataContext = this;
        Refresh();
        LocalizationService.RefreshWhenLanguageChanges(this, Refresh);
    }

    internal void SetRuntimeStatus(string key)
    {
        _statusKey = key;
        RuntimeStatus.Text = _targetName() is { Length: > 0 } name
            ? LocalizationService.Format("MappingTargetStatus", name, LocalizationService.Get(key))
            : LocalizationService.Get(key);
    }

    private void Refresh()
    {
        ErrorText.Text = LocalizationService.RefreshText(ErrorText.Text);
        EnabledBox.IsChecked = _settings.Enabled;
        SuppressBox.IsChecked = _settings.SuppressOriginalKey;
        Rows.Clear();
        foreach (var entry in _settings.Mappings) Rows.Add(new(entry));
        EmptyText.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetRuntimeStatus(_runtimeStatus());
    }

    private string? Save(KeyboardMappingSettings next, string eventName)
    {
        var error = _apply(next);
        ErrorText.Text = error ?? string.Empty;
        if (error is null)
        {
            _settings = next.Clone();
            DiagnosticLogger.ReverseControl("keyboard_mapping", eventName, ("count", next.Mappings.Count));
        }
        Refresh();
        return error;
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        var next = _settings.Clone();
        next.Enabled = EnabledBox.IsChecked == true;
        Save(next, next.Enabled ? "enabled" : "disabled");
    }
    private void OnSuppressClick(object sender, RoutedEventArgs e)
    {
        var next = _settings.Clone();
        next.SuppressOriginalKey = SuppressBox.IsChecked == true;
        Save(next, "original_key_policy_changed");
    }
    private void OnRowEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: KeyboardMappingRow row } box) return;
        var next = _settings.Clone();
        next.Mappings[next.Mappings.FindIndex(m => m.Id == row.Entry.Id)] = row.Entry with { Enabled = box.IsChecked == true };
        Save(next, "mapping_edited");
    }
    private void OnAddClick(object sender, RoutedEventArgs e) => Edit(null);
    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KeyboardMappingRow row) Edit(row.Entry);
    }
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not KeyboardMappingRow row) return;
        if (!AppPromptWindow.ConfirmDestructive(LocalizationService.Get("MappingDelete"),
                LocalizationService.Format("MappingDeleteConfirm", row.KeyText),
                LocalizationService.Get("MappingDelete"), this)) return;
        var next = _settings.Clone();
        next.Mappings.RemoveAll(entry => entry.Id == row.Entry.Id);
        Save(next, "mapping_deleted");
    }
    private void Edit(KeyboardMappingEntry? entry)
    {
        if (_editor is not null) { _editor.Activate(); return; }
        var editor = new KeyboardMappingEditorWindow(entry, _settings.Mappings, _conflict,
            _beginCapture, _endCapture, (edited, replacedId) =>
            {
                var next = _settings.Clone();
                next.Mappings.RemoveAll(m => m.Id == edited.Id || m.Id == replacedId);
                next.Mappings.Add(edited);
                return Save(next, _settings.Mappings.Any(m => m.Id == edited.Id) ? "mapping_edited" : "mapping_created");
            }, BeginPick) { Owner = this };
        _editor = editor;
        editor.Closed += (_, _) => { _editor = null; _endCapture(); _cancelPick?.Invoke(); };
        editor.Show();
    }

    private string? BeginPick(KeyboardMappingEntry entry, Action<KeyboardMappingEntry?> completed)
    {
        if (_beginPick is null) return LocalizationService.Get("MappingPickNoPreview");
        var editor = _editor;
        editor?.Hide();
        Hide();
        void Restore(KeyboardMappingEntry? result)
        {
            if (editor is null || !ReferenceEquals(_editor, editor)) return;
            Show(); editor.Show(); editor.Activate();
            completed(result);
        }
        var error = _beginPick(entry, Restore);
        if (error is not null) Restore(null);
        return error;
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

public sealed class KeyboardMappingRow
{
    internal KeyboardMappingRow(KeyboardMappingEntry entry) => Entry = entry;
    internal KeyboardMappingEntry Entry { get; }
    public string KeyText => KeyboardMappingKeys.Display(Entry.Key);
    public string ActionText => LocalizationService.Get("MappingAction" + Entry.Action);
    public bool Enabled => Entry.Enabled;
    public string ParameterText => Entry.IsSwipe
        ? LocalizationService.Format("MappingSwipeSummary", Entry.X * 100, Entry.Y * 100,
            Entry.EndPoint.X * 100, Entry.EndPoint.Y * 100, Entry.DurationMs)
        : Entry.Action == MappedTouchAction.LongPress
            ? LocalizationService.Format("MappingHoldSummary", Entry.X * 100, Entry.Y * 100, Entry.DurationMs)
            : Entry.Action == MappedTouchAction.DoubleTap
                ? LocalizationService.Format("MappingDoubleSummary", Entry.X * 100, Entry.Y * 100, Entry.IntervalMs)
                : LocalizationService.Format("MappingPointSummary", Entry.X * 100, Entry.Y * 100);
}
