using System.ComponentModel;
using System.Globalization;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal enum MappingWizardStep { Key, Action, Parameters, Position, Confirmation }

// A single draft survives view changes. Only BuildEntry crosses the save boundary.
internal sealed class KeyboardMappingWizardState : INotifyPropertyChanged
{
    private KeyboardMappingEntry _draft = new();
    private KeyboardMappingEntry _original = new();
    private bool _originalHasAction, _originalHasPosition;
    private readonly HashSet<MappingWizardStep> _completed = [];
    private readonly Func<MappedKey, string?> _conflict;
    private readonly IReadOnlyList<KeyboardMappingEntry> _mappings;
    private string _duration = "400", _interval = "100";
    private (uint Width, uint Height)? _previewSize;
    internal KeyboardMappingWizardState(KeyboardMappingEntry? entry,
        IReadOnlyList<KeyboardMappingEntry> mappings, Func<MappedKey, string?> conflict)
    {
        (_mappings, _conflict) = (mappings, conflict);
        Load(entry);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public KeyboardMappingEntry Draft => _draft;
    public MappingWizardStep Current { get; private set; } = MappingWizardStep.Key;
    public IReadOnlyList<MappingWizardStep> Steps => HasParameters
        ? [MappingWizardStep.Key, MappingWizardStep.Action, MappingWizardStep.Parameters, MappingWizardStep.Position, MappingWizardStep.Confirmation]
        : [MappingWizardStep.Key, MappingWizardStep.Action, MappingWizardStep.Position, MappingWizardStep.Confirmation];
    public bool HasAction { get; private set; }
    public bool HasPosition { get; private set; }
    public bool Capturing { get; set; }
    public bool HasKey => _draft.Key is not null;
    public bool KeyHasError => HasKey && ErrorFor(MappingWizardStep.Key) is not null;
    public bool IsSwipe => _draft.IsSwipe;
    public bool HasParameters => HasAction && (_draft.IsSwipe || _draft.Action is MappedTouchAction.LongPress or MappedTouchAction.DoubleTap);
    public bool ShowDuration => HasAction && _draft.Action == MappedTouchAction.LongPress;
    public bool ShowInterval => HasAction && _draft.Action == MappedTouchAction.DoubleTap;
    public string Duration { get => _duration; set { _duration = value; InvalidateFrom(MappingWizardStep.Parameters); Notify(); } }
    public string Interval { get => _interval; set { _interval = value; InvalidateFrom(MappingWizardStep.Parameters); Notify(); } }
    public string KeyText => _draft.Key is null ? L("WizardPressKey") : KeyboardMappingKeys.Display(_draft.Key);
    public string ActionText => HasAction ? L("MappingAction" + _draft.Action) : string.Empty;
    public string KeyStatus => Capturing ? L("WizardKeyWaiting") : _draft.Key is null ? L("WizardKeyStart") : L("WizardKeySelected");
    public string PositionText => !HasPosition ? string.Empty : _draft.DeviceCoordinates && _previewSize is { } size
        ? IsSwipe ? LocalizationService.Format("WizardSwipePixels", _draft.X * size.Width, _draft.Y * size.Height,
            _draft.EndPoint.X * size.Width, _draft.EndPoint.Y * size.Height, _draft.DurationMs)
            : LocalizationService.Format("WizardPointPixels", _draft.X * size.Width, _draft.Y * size.Height)
        : IsSwipe
        ? LocalizationService.Format("MappingSwipeSummary", _draft.X * 100, _draft.Y * 100,
            _draft.EndPoint.X * 100, _draft.EndPoint.Y * 100, _draft.DurationMs)
        : LocalizationService.Format("MappingPointSummary", _draft.X * 100, _draft.Y * 100);
    public string ParameterText => ShowDuration ? LocalizationService.Format("WizardMilliseconds", Duration)
        : ShowInterval ? LocalizationService.Format("WizardMilliseconds", Interval) : string.Empty;
    public string Title => L("WizardTitle" + (Current == MappingWizardStep.Position && IsSwipe ? "Gesture" : Current.ToString()));
    public string Subtitle => L("WizardHint" + (Current == MappingWizardStep.Position && IsSwipe ? "Gesture" : Current.ToString()));
    public int Index => Steps.ToList().IndexOf(Current);
    public string StepNumber => (Index + 1).ToString(CultureInfo.CurrentCulture);
    public string ProgressText => LocalizationService.Format("WizardProgress", Index + 1, Steps.Count);
    public string NextText => L(Current == MappingWizardStep.Confirmation ? "WizardFinish" : "WizardNext");
    public bool CanPrevious => Index > 0;
    public bool CanNext => ErrorFor(Current) is null;
    public Guid? ReplacementId { get; private set; }
    public KeyboardMappingEntry? Duplicate => _draft.Key is null ? null : _mappings.FirstOrDefault(m =>
        m.Id != _draft.Id && m.Key is not null && m.Key.SamePhysicalKey(_draft.Key));
    public bool IsDirty => _draft != _original || HasAction != _originalHasAction || HasPosition != _originalHasPosition ||
        Duration != _original.DurationMs.ToString(CultureInfo.CurrentCulture) || Interval != _original.IntervalMs.ToString(CultureInfo.CurrentCulture);

    internal void Load(KeyboardMappingEntry? entry)
    {
        _draft = _original = entry ?? new();
        HasAction = _originalHasAction = entry is not null;
        HasPosition = _originalHasPosition = entry?.Validate() is null && entry is not null;
        _duration = _draft.DurationMs.ToString(CultureInfo.CurrentCulture);
        _interval = _draft.IntervalMs.ToString(CultureInfo.CurrentCulture);
        ReplacementId = null;
        Current = MappingWizardStep.Key;
        _completed.Clear();
        if (entry is not null)
            foreach (var step in Steps.TakeWhile(s => s != MappingWizardStep.Confirmation && ErrorFor(s) is null)) _completed.Add(step);
        Notify();
    }
    internal void SetKey(MappedKey key)
    {
        _draft = _draft with { Key = key };
        ReplacementId = null;
        InvalidateFrom(MappingWizardStep.Key);
        Notify();
    }
    internal void SelectAction(MappedTouchAction action)
    {
        if (!Enum.IsDefined(action)) return;
        if (_draft.Action != action && (_draft.IsSwipe || action is >= MappedTouchAction.Swipe and <= MappedTouchAction.SwipeRight))
            HasPosition = false;
        _draft = _draft with { Action = action };
        HasAction = true;
        InvalidateFrom(MappingWizardStep.Action);
        Notify();
    }
    internal void SetPosition(KeyboardMappingEntry result)
    {
        // Copy geometry only: an asynchronous pick cannot overwrite the key or parameters.
        _draft = _draft with { X = result.X, Y = result.Y, EndX = result.EndX, EndY = result.EndY,
            DeviceCoordinates = result.DeviceCoordinates, DurationMs = IsSwipe ? result.DurationMs : _draft.DurationMs };
        HasPosition = true;
        InvalidateFrom(MappingWizardStep.Position);
        Notify();
    }
    internal void AcceptReplacement() { ReplacementId = Duplicate?.Id; Notify(); }
    internal void SetPreviewSize(uint width, uint height)
    { if (_previewSize == (width, height)) return; _previewSize = (width, height); Notify(); }
    internal void InvalidatePosition() { HasPosition = false; InvalidateFrom(MappingWizardStep.Position); Notify(); }
    internal bool IsComplete(MappingWizardStep step) => _completed.Contains(step) && ErrorFor(step) is null;
    internal bool CanVisit(MappingWizardStep step) => step == Current ||
        IsComplete(step) && Steps.TakeWhile(s => s != step).All(s => ErrorFor(s) is null);
    internal bool MoveNext()
    {
        if (!CanNext || Current == MappingWizardStep.Confirmation) return false;
        _completed.Add(Current);
        Current = Steps[Index + 1];
        Notify(); return true;
    }
    internal bool MovePrevious()
    {
        if (!CanPrevious) return false;
        Current = Steps[Index - 1]; Notify(); return true;
    }
    internal bool Visit(MappingWizardStep step)
    {
        if (!CanVisit(step)) return false;
        Current = step; Notify(); return true;
    }
    internal string? ErrorFor(MappingWizardStep step) => step switch
    {
        MappingWizardStep.Key => _draft.Key is null ? "MappingCaptureHint" : _draft.Key.Validate() ??
            _conflict(_draft.Key) ?? (Duplicate is { } duplicate && ReplacementId != duplicate.Id ? "WizardDuplicate" : null),
        MappingWizardStep.Action => !HasAction ? "WizardChooseAction" : null,
        MappingWizardStep.Parameters => ShowDuration && !ValidNumber(Duration, 50, 10000, out _) ? "MappingInvalidDuration" :
            ShowInterval && !ValidNumber(Interval, 40, 1000, out _) ? "MappingInvalidInterval" : null,
        MappingWizardStep.Position => !HasPosition ? "MappingPickRequired" : BuildDraft().Validate(),
        MappingWizardStep.Confirmation => Steps.Where(s => s != MappingWizardStep.Confirmation).Select(ErrorFor).FirstOrDefault(e => e is not null),
        _ => null,
    };
    internal KeyboardMappingEntry? BuildEntry() => ErrorFor(MappingWizardStep.Confirmation) is null ? BuildDraft() : null;
    private KeyboardMappingEntry BuildDraft()
    {
        var result = _draft;
        if (ShowDuration && ValidNumber(Duration, 50, 10000, out var duration)) result = result with { DurationMs = duration };
        if (ShowInterval && ValidNumber(Interval, 40, 1000, out var interval)) result = result with { IntervalMs = interval };
        return result;
    }
    private static bool ValidNumber(string text, int min, int max, out int number) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out number) && number >= min && number <= max;
    private void InvalidateFrom(MappingWizardStep step)
    {
        foreach (var item in Steps.SkipWhile(s => s != step)) _completed.Remove(item);
    }
    internal void Notify() => PropertyChanged?.Invoke(this, new(null));
    private static string L(string key) => LocalizationService.Get(key);
}
