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
    public IReadOnlyList<MappingWizardStep> Steps => new[] { MappingWizardStep.Key, MappingWizardStep.Action, MappingWizardStep.Parameters, MappingWizardStep.Position, MappingWizardStep.Confirmation }
        .Where(s => (s != MappingWizardStep.Parameters || HasParameters) && (s != MappingWizardStep.Position || _draft.NeedsPosition)).ToArray();
    public bool HasAction { get; private set; }
    public bool HasPosition { get; private set; }
    public bool Capturing { get; set; }
    public bool HasKey => !IsKeyboard || _draft.Key is not null;
    public bool IsKeyboard => _draft.InputKind == MappingInputKind.Keyboard;
    public bool IsJoystick => HasAction && _draft.Action == MappedTouchAction.Joystick;
    public bool IsRelativeDrag => HasAction && _draft.Action == MappedTouchAction.RelativeDrag;
    public bool IsCycle => HasAction && _draft.Action == MappedTouchAction.CycleTargets;
    public bool IsContinuous => IsJoystick || IsRelativeDrag;
    public bool NeedsPosition => _draft.NeedsPosition;
    public bool CanReleaseAfter => !IsContinuous && NeedsPosition;
    public string Name { get => _draft.Name; set => Change(_draft with { Name = value }); }
    public bool Control { get => (_draft.Modifiers & 2) != 0; set => SetModifier(2, value); }
    public bool Alt { get => (_draft.Modifiers & 1) != 0; set => SetModifier(1, value); }
    public bool Shift { get => (_draft.Modifiers & 4) != 0; set => SetModifier(4, value); }
    public bool Windows { get => (_draft.Modifiers & 8) != 0; set => SetModifier(8, value); }
    public bool ReleasePointerAfter { get => _draft.ReleasePointerAfter; set => Change(_draft with { ReleasePointerAfter = value }); }
    public double Radius { get => IsJoystick ? _draft.Joystick.Radius : _draft.RelativeDrag.Radius; set => Change(IsJoystick ?
        _draft with { Joystick = _draft.Joystick with { Radius = value } } : _draft with { RelativeDrag = _draft.RelativeDrag with { Radius = value } }); }
    public double StartupMs { get => _draft.Joystick.StartupMs; set => Change(_draft with { Joystick = _draft.Joystick with { StartupMs = (int)value } }); }
    public double TurnMs { get => _draft.Joystick.TurnMs; set => Change(_draft with { Joystick = _draft.Joystick with { TurnMs = (int)value } }); }
    public bool OppositeNeutral { get => _draft.Joystick.OppositeNeutral; set => Change(_draft with { Joystick = _draft.Joystick with { OppositeNeutral = value } }); }
    public double SensitivityX { get => _draft.RelativeDrag.SensitivityX; set => Change(_draft with { RelativeDrag = _draft.RelativeDrag with { SensitivityX = value } }); }
    public double SensitivityY { get => _draft.RelativeDrag.SensitivityY; set => Change(_draft with { RelativeDrag = _draft.RelativeDrag with { SensitivityY = value } }); }
    public bool InvertX { get => _draft.RelativeDrag.InvertX; set => Change(_draft with { RelativeDrag = _draft.RelativeDrag with { InvertX = value } }); }
    public bool InvertY { get => _draft.RelativeDrag.InvertY; set => Change(_draft with { RelativeDrag = _draft.RelativeDrag with { InvertY = value } }); }
    public bool ToggleDrag { get => _draft.RelativeDrag.Toggle; set => Change(_draft with { RelativeDrag = _draft.RelativeDrag with { Toggle = value } }); }
    public bool Recenter { get => _draft.RelativeDrag.Recenter; set => Change(_draft with { RelativeDrag = _draft.RelativeDrag with { Recenter = value } }); }
    public string DownText => KeyboardMappingKeys.Display(_draft.Joystick.Down);
    public string LeftText => KeyboardMappingKeys.Display(_draft.Joystick.Left);
    public string RightText => KeyboardMappingKeys.Display(_draft.Joystick.Right);
    public string TargetsText => LocalizationService.Format("MappingTargetsCount", (HasPosition ? 1 : 0) + _draft.Targets.Length);
    private void Change(KeyboardMappingEntry entry) { _draft = entry; InvalidateFrom(MappingWizardStep.Parameters); Notify(); }
    private void SetModifier(uint mask, bool enabled) { _draft = _draft with { Modifiers = enabled ? _draft.Modifiers | mask : _draft.Modifiers & ~mask }; InvalidateFrom(MappingWizardStep.Key); Notify(); }
    internal void SetInput(MappingInputKind kind, int button = 1)
    { _draft = _draft with { InputKind = kind, MouseButton = button }; ReplacementId = null; InvalidateFrom(MappingWizardStep.Key); Notify(); }
    internal void SetDirectionKey(int direction, MappedKey key) => Change(_draft with { Joystick = direction switch
    { 1 => _draft.Joystick with { Down = key }, 2 => _draft.Joystick with { Left = key }, _ => _draft.Joystick with { Right = key } } });
    internal void AddTarget(KeyboardMappingEntry point)
    { if (_draft.Targets.Length < 31) Change(_draft with { Targets = [.. _draft.Targets, new(point.X, point.Y)] }); }
    internal void RemoveTarget()
    { if (_draft.Targets.Length > 0) Change(_draft with { Targets = _draft.Targets[..^1] }); }
    public bool KeyHasError => HasKey && ErrorFor(MappingWizardStep.Key) is not null;
    public bool IsSwipe => _draft.IsSwipe;
    public bool HasParameters => HasAction && (_draft.IsSwipe || _draft.Action is MappedTouchAction.LongPress or MappedTouchAction.DoubleTap or MappedTouchAction.Joystick or MappedTouchAction.RelativeDrag);
    public bool ShowDuration => HasAction && _draft.Action == MappedTouchAction.LongPress;
    public bool ShowInterval => HasAction && _draft.Action == MappedTouchAction.DoubleTap;
    public string Duration { get => _duration; set { _duration = value; InvalidateFrom(MappingWizardStep.Parameters); Notify(); } }
    public string Interval { get => _interval; set { _interval = value; InvalidateFrom(MappingWizardStep.Parameters); Notify(); } }
    public string KeyText => !HasKey ? L("WizardPressKey") : KeyboardMappingKeys.DisplayInput(_draft);
    public string ActionText => HasAction ? L("MappingAction" + _draft.Action) : string.Empty;
    public string KeyStatus => Capturing ? L("WizardKeyWaiting") : !HasKey ? L("WizardKeyStart") : L("WizardKeySelected");
    public string PositionText => !_draft.NeedsPosition ? L("MappingLocalCommand") : IsCycle ? TargetsText : !HasPosition ? string.Empty : _draft.DeviceCoordinates && _previewSize is { } size
        ? IsSwipe ? LocalizationService.Format("WizardSwipePixels", _draft.X * size.Width, _draft.Y * size.Height,
            _draft.EndPoint.X * size.Width, _draft.EndPoint.Y * size.Height, _draft.DurationMs)
            : LocalizationService.Format("WizardPointPixels", _draft.X * size.Width, _draft.Y * size.Height)
        : IsSwipe
        ? LocalizationService.Format("MappingSwipeSummary", _draft.X * 100, _draft.Y * 100,
            _draft.EndPoint.X * 100, _draft.EndPoint.Y * 100, _draft.DurationMs)
        : LocalizationService.Format("MappingPointSummary", _draft.X * 100, _draft.Y * 100);
    public string ParameterText => ShowDuration ? LocalizationService.Format("WizardMilliseconds", Duration)
        : ShowInterval ? LocalizationService.Format("WizardMilliseconds", Interval) : IsContinuous ? LocalizationService.Format("MappingRadiusSummary", Radius * 100) : string.Empty;
    public string Title => L("WizardTitle" + (Current == MappingWizardStep.Position && IsSwipe ? "Gesture" : Current.ToString()));
    public string Subtitle => L("WizardHint" + (Current == MappingWizardStep.Position && IsSwipe ? "Gesture" : Current.ToString()));
    public int Index => Steps.ToList().IndexOf(Current);
    public string StepNumber => (Index + 1).ToString(CultureInfo.CurrentCulture);
    public string ProgressText => LocalizationService.Format("WizardProgress", Index + 1, Steps.Count);
    public string NextText => L(Current == MappingWizardStep.Confirmation ? "WizardFinish" : "WizardNext");
    public bool CanPrevious => Index > 0;
    public bool CanNext => ErrorFor(Current) is null;
    public Guid? ReplacementId { get; private set; }
    public KeyboardMappingEntry? Duplicate => _mappings.FirstOrDefault(m => m.Id != _draft.Id && m.ConflictsWith(_draft));
    public bool IsDirty => !_draft.ContentEquals(_original) || HasAction != _originalHasAction || HasPosition != _originalHasPosition ||
        Duration != _original.DurationMs.ToString(CultureInfo.CurrentCulture) || Interval != _original.IntervalMs.ToString(CultureInfo.CurrentCulture);

    internal void Load(KeyboardMappingEntry? entry)
    {
        _draft = _original = entry?.Copy() ?? new();
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
        _draft = _draft with { Action = action, ReleasePointerAfter = action is MappedTouchAction.Joystick or MappedTouchAction.RelativeDrag ? false : _draft.ReleasePointerAfter };
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
        MappingWizardStep.Key => !HasKey ? "MappingCaptureHint" : (IsKeyboard ? _draft.Key!.Validate() ?? (_draft.Modifiers == 0 ? _conflict(_draft.Key) : null) : null) ??
            (Duplicate is { } duplicate && ReplacementId != duplicate.Id ? "WizardDuplicate" : null),
        MappingWizardStep.Action => !HasAction ? "WizardChooseAction" : _draft.Action == MappedTouchAction.Joystick ?
            !IsKeyboard || _draft.Modifiers != 0 ? "MappingFourKeysRequired" : null : _draft.ValidateInput(),
        MappingWizardStep.Parameters => ShowDuration && !ValidNumber(Duration, 50, 10000, out _) ? "MappingInvalidDuration" :
            ShowInterval && !ValidNumber(Interval, 40, 1000, out _) ? "MappingInvalidInterval" :
            IsJoystick ? _draft.ValidateInput() ?? _draft.Joystick.Validate() ?? _draft.InputKeys.Select(_conflict).FirstOrDefault(e => e is not null) ??
                (Duplicate is { } duplicate && ReplacementId != duplicate.Id ? "WizardDuplicate" : null) :
            IsRelativeDrag ? _draft.RelativeDrag.Validate() : null,
        MappingWizardStep.Position => !HasPosition ? "MappingPickRequired" : BuildDraft().Validate(),
        MappingWizardStep.Confirmation => Steps.Where(s => s != MappingWizardStep.Confirmation).Select(ErrorFor).FirstOrDefault(e => e is not null),
        _ => null,
    };
    internal KeyboardMappingEntry? BuildEntry() => ErrorFor(MappingWizardStep.Confirmation) is null && BuildDraft().Validate() is null ? BuildDraft() : null;
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
