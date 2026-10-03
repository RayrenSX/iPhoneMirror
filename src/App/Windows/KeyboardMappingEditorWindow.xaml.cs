using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class KeyboardMappingEditorWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private KeyboardMappingEntry _entry;
    private MappedKey? _key;
    private readonly IReadOnlyList<KeyboardMappingEntry> _mappings;
    private readonly Func<MappedKey, string?> _conflict;
    private readonly Func<Action<MappedKey>, string?> _beginCapture;
    private readonly Action _endCapture;
    private readonly Func<KeyboardMappingEntry, Guid?, string?> _save;
    private readonly Func<KeyboardMappingEntry, Action<KeyboardMappingEntry?>, string?>? _beginPick;
    private KeyboardMappingEntry? _pending, _duplicate;
    private bool _capturing;
    private bool _positionSet;
    private bool _loading;
    private long _captureGeneration;
    internal MappingEditorState State { get; private set; }

    internal KeyboardMappingEditorWindow(KeyboardMappingEntry? entry, IReadOnlyList<KeyboardMappingEntry> mappings,
        Func<MappedKey, string?> conflict, Func<Action<MappedKey>, string?> beginCapture,
        Action endCapture, Func<KeyboardMappingEntry, Guid?, string?> save,
        Func<KeyboardMappingEntry, Action<KeyboardMappingEntry?>, string?>? beginPick = null)
    {
        _entry = entry ?? new();
        (_mappings, _conflict, _beginCapture, _endCapture, _save) = (mappings, conflict, beginCapture, endCapture, save);
        _beginPick = beginPick;
        InitializeComponent();
        RefreshActions();
        LoadEntry(_entry);
        Closed += (_, _) => StopCapture();
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
        {
            RefreshActions();
            RefreshCapture();
            ErrorText.Text = LocalizationService.RefreshText(ErrorText.Text);
        });
    }

    private void RefreshActions()
    {
        var selected = ActionBox.SelectedValue is MappedTouchAction current ? current : _entry.Action;
        ActionBox.ItemsSource = Enum.GetValues<MappedTouchAction>().Select(value => new MappingActionOption(value)).ToArray();
        ActionBox.SelectedValue = selected;
    }
    private void LoadEntry(KeyboardMappingEntry entry)
    {
        _loading = true;
        _entry = entry;
        _key = entry.Key;
        _positionSet = entry.Key is not null;
        ActionBox.SelectedValue = entry.Action;
        DurationBox.Text = Number(entry.DurationMs); IntervalBox.Text = Number(entry.IntervalMs);
        EntryEnabledBox.IsChecked = entry.Enabled;
        _loading = false;
        UpdateState();
        RefreshCapture();
        RefreshParameters();
    }
    private static string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private void RefreshCapture()
    {
        CaptureButton.Content = LocalizationService.Get(_capturing ? "MappingWaiting" : "MappingCapture") +
            (_capturing ? string.Empty : " · " + KeyboardMappingKeys.Display(_key));
        PickButton.IsEnabled = _key is not null && !_capturing && State != MappingEditorState.PickingPosition;
        PickButton.SetResourceReference(ContentControl.ContentProperty, _positionSet ? "MappingRepick" : "MappingPick");
        SaveButton.IsEnabled = State == MappingEditorState.MappingReady;
        PositionSummary.Text = _positionSet ? new KeyboardMappingRow(_entry).ParameterText : LocalizationService.Get("MappingPickRequired");
        StateText.Text = LocalizationService.Get("MappingState" + State);
    }

    private void UpdateState()
    {
        State = _capturing ? MappingEditorState.WaitingForKey : _key is null ? MappingEditorState.Idle :
            _positionSet ? MappingEditorState.MappingReady : MappingEditorState.KeyCaptured;
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_capturing) { StopCapture(); return; }
        ConflictPanel.Visibility = Visibility.Collapsed;
        _capturing = true;
        var generation = ++_captureGeneration;
        UpdateState();
        RefreshCapture();
        var error = _beginCapture(key =>
        {
            if (!_capturing || generation != _captureGeneration) return;
            StopCapture();
            if (key.Validate() is { } invalid)
            {
                ErrorText.Text = LocalizationService.Get(invalid);
                DiagnosticLogger.ReverseControlWarning("keyboard_mapping", "unsupported_key", ("key", key.VirtualKey));
                return;
            }
            _key = key;
            UpdateState();
            RefreshCapture();
            ErrorText.Text = _conflict(key) is { } conflict ? LocalizationService.Get(conflict) : string.Empty;
        });
        if (error is not null) { StopCapture(); ErrorText.Text = error; }
    }
    private void StopCapture()
    {
        ++_captureGeneration;
        _endCapture();
        _capturing = false;
        if (State != MappingEditorState.PickingPosition) UpdateState();
        RefreshCapture();
    }
    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DurationPanel is not null) RefreshParameters();
        if (ConflictPanel is not null) ConflictPanel.Visibility = Visibility.Collapsed;
        if (!_loading && ActionBox.SelectedValue is MappedTouchAction action && _entry.Action != action)
        {
            if (action >= MappedTouchAction.Swipe) _positionSet = false;
            _entry = _entry with { Action = action };
            UpdateState();
            if (PickButton is not null) RefreshCapture();
        }
    }
    private void RefreshParameters()
    {
        var action = ActionBox.SelectedValue is MappedTouchAction value ? value : MappedTouchAction.Tap;
        DurationPanel.Visibility = action >= MappedTouchAction.Swipe || action == MappedTouchAction.LongPress
            ? Visibility.Visible : Visibility.Collapsed;
        IntervalPanel.Visibility = action == MappedTouchAction.DoubleTap ? Visibility.Visible : Visibility.Collapsed;
    }
    private KeyboardMappingEntry? ReadEntry()
    {
        ErrorText.Text = string.Empty;
        var action = (MappedTouchAction)ActionBox.SelectedValue;
        bool TryNumber(TextBox box, out double number) => double.TryParse(box.Text,
            NumberStyles.Float, CultureInfo.CurrentCulture, out number) && double.IsFinite(number);
        if (!_positionSet) return Invalid("MappingPickRequired");
        var duration = (double)_entry.DurationMs; var interval = (double)_entry.IntervalMs;
        if ((action >= MappedTouchAction.Swipe || action == MappedTouchAction.LongPress) &&
            (!TryNumber(DurationBox, out duration) || duration != Math.Truncate(duration) || duration is < 50 or > 10000))
            return Invalid("MappingInvalidDuration");
        if (action == MappedTouchAction.DoubleTap && (!TryNumber(IntervalBox, out interval) ||
            interval != Math.Truncate(interval) || interval is < 40 or > 1000)) return Invalid("MappingInvalidInterval");
        var entry = _entry with { Key = _key, Action = action,
            DurationMs = (int)duration, IntervalMs = (int)interval, Enabled = EntryEnabledBox.IsChecked == true };
        if (entry.Validate() is { } error) return Invalid(error);
        if (_conflict(entry.Key!) is { } conflict) return Invalid(conflict);
        return entry;
    }

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        if (_capturing || _key is null || _beginPick is null) return;
        ErrorText.Text = string.Empty;
        var entry = _entry with { Key = _key, Action = (MappedTouchAction)ActionBox.SelectedValue };
        StopCapture();
        State = MappingEditorState.PickingPosition;
        RefreshCapture();
        var error = _beginPick(entry, result =>
        {
            if (result is not null)
            {
                _entry = result;
                _positionSet = true;
                DurationBox.Text = Number(result.DurationMs);
            }
            UpdateState();
            RefreshCapture();
        });
        if (error is not null) { ErrorText.Text = error; UpdateState(); RefreshCapture(); }
    }
    private KeyboardMappingEntry? Invalid(string key)
    {
        ErrorText.Text = LocalizationService.Get(key);
        DiagnosticLogger.ReverseControlWarning("keyboard_mapping", "invalid_mapping", ("reason", key));
        return null;
    }
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_capturing || ReadEntry() is not { } entry) return;
        _duplicate = _mappings.FirstOrDefault(m => m.Id != entry.Id && m.Key!.SamePhysicalKey(entry.Key!));
        if (_duplicate is not null)
        {
            _pending = entry;
            ErrorText.Text = LocalizationService.Get("MappingDuplicate");
            ConflictPanel.Visibility = Visibility.Visible;
            DiagnosticLogger.ReverseControlWarning("keyboard_mapping", "key_conflict", ("scan", entry.Key!.ScanCode));
            return;
        }
        Commit(entry, null);
    }
    private void Commit(KeyboardMappingEntry entry, Guid? replaced)
    {
        if (_save(entry, replaced) is { } error) ErrorText.Text = error;
        else Close();
    }
    private void OnReplaceClick(object sender, RoutedEventArgs e)
    {
        if (_pending is null || _duplicate is null || ReadEntry() is not { } current) return;
        // Revalidate after any field/key edits while the conflict choices were open.
        if (!current.Key!.SamePhysicalKey(_duplicate.Key!)) { OnSaveClick(sender, e); return; }
        Commit(current, _duplicate.Id);
    }
    private void OnEditOriginalClick(object sender, RoutedEventArgs e)
    {
        if (_duplicate is null) return;
        LoadEntry(_duplicate);
        OnCancelConflictClick(sender, e);
    }
    private void OnCancelConflictClick(object sender, RoutedEventArgs e)
    {
        _pending = _duplicate = null;
        ErrorText.Text = string.Empty;
        ConflictPanel.Visibility = Visibility.Collapsed;
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

internal sealed record MappingActionOption(MappedTouchAction Value)
{
    public string Label => LocalizationService.Get("MappingAction" + Value);
}
