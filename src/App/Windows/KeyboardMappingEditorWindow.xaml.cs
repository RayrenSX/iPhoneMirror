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
    private KeyboardMappingEntry? _pending, _duplicate;
    private bool _capturing;

    internal KeyboardMappingEditorWindow(KeyboardMappingEntry? entry, IReadOnlyList<KeyboardMappingEntry> mappings,
        Func<MappedKey, string?> conflict, Func<Action<MappedKey>, string?> beginCapture,
        Action endCapture, Func<KeyboardMappingEntry, Guid?, string?> save)
    {
        _entry = entry ?? new();
        (_mappings, _conflict, _beginCapture, _endCapture, _save) = (mappings, conflict, beginCapture, endCapture, save);
        InitializeComponent();
        RefreshActions();
        LoadEntry(_entry);
        Closed += (_, _) => StopCapture();
        Deactivated += (_, _) => StopCapture();
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
        _entry = entry;
        _key = entry.Key;
        ActionBox.SelectedValue = entry.Action;
        XBox.Text = Number(entry.X * 100); YBox.Text = Number(entry.Y * 100);
        EndXBox.Text = Number(entry.EndX * 100); EndYBox.Text = Number(entry.EndY * 100);
        DistanceBox.Text = Number(entry.Distance * 100);
        DurationBox.Text = Number(entry.DurationMs); IntervalBox.Text = Number(entry.IntervalMs);
        EntryEnabledBox.IsChecked = entry.Enabled;
        RefreshCapture();
        RefreshParameters();
    }
    private static string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private void RefreshCapture() => CaptureButton.Content = LocalizationService.Get(_capturing ? "MappingWaiting" : "MappingCapture") +
        (_capturing ? string.Empty : " · " + KeyboardMappingKeys.Display(_key));

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_capturing) { StopCapture(); return; }
        ConflictPanel.Visibility = Visibility.Collapsed;
        _capturing = true;
        SaveButton.IsEnabled = false;
        RefreshCapture();
        var error = _beginCapture(key =>
        {
            StopCapture();
            if (key.Validate() is { } invalid)
            {
                ErrorText.Text = LocalizationService.Get(invalid);
                DiagnosticLogger.ReverseControlWarning("keyboard_mapping", "unsupported_key", ("key", key.VirtualKey));
                return;
            }
            _key = key;
            RefreshCapture();
            ErrorText.Text = _conflict(key) is { } conflict ? LocalizationService.Get(conflict) : string.Empty;
        });
        if (error is not null) { StopCapture(); ErrorText.Text = error; }
    }
    private void StopCapture()
    {
        _endCapture();
        _capturing = false;
        SaveButton.IsEnabled = true;
        RefreshCapture();
    }
    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndPanel is not null) RefreshParameters();
        if (ConflictPanel is not null) ConflictPanel.Visibility = Visibility.Collapsed;
    }
    private void RefreshParameters()
    {
        var action = ActionBox.SelectedValue is MappedTouchAction value ? value : MappedTouchAction.Tap;
        EndPanel.Visibility = action == MappedTouchAction.Swipe ? Visibility.Visible : Visibility.Collapsed;
        DistancePanel.Visibility = action >= MappedTouchAction.SwipeUp ? Visibility.Visible : Visibility.Collapsed;
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
        if (!TryNumber(XBox, out var x) || !TryNumber(YBox, out var y)) return Invalid("MappingInvalidCoordinates");
        var endX = _entry.EndX * 100; var endY = _entry.EndY * 100;
        var distance = _entry.Distance * 100; var duration = (double)_entry.DurationMs; var interval = (double)_entry.IntervalMs;
        if (action == MappedTouchAction.Swipe && (!TryNumber(EndXBox, out endX) || !TryNumber(EndYBox, out endY)))
            return Invalid("MappingInvalidCoordinates");
        if (action >= MappedTouchAction.SwipeUp && !TryNumber(DistanceBox, out distance)) return Invalid("MappingInvalidDistance");
        if ((action >= MappedTouchAction.Swipe || action == MappedTouchAction.LongPress) &&
            (!TryNumber(DurationBox, out duration) || duration != Math.Truncate(duration) || duration is < 50 or > 10000))
            return Invalid("MappingInvalidDuration");
        if (action == MappedTouchAction.DoubleTap && (!TryNumber(IntervalBox, out interval) ||
            interval != Math.Truncate(interval) || interval is < 40 or > 1000)) return Invalid("MappingInvalidInterval");
        var entry = _entry with { Key = _key, Action = action, X = x / 100, Y = y / 100,
            EndX = endX / 100, EndY = endY / 100, Distance = distance / 100,
            DurationMs = (int)duration, IntervalMs = (int)interval, Enabled = EntryEnabledBox.IsChecked == true };
        if (entry.Validate() is { } error) return Invalid(error);
        if (_conflict(entry.Key!) is { } conflict) return Invalid(conflict);
        return entry;
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
