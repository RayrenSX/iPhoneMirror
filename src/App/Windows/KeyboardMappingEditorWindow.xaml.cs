using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows.MappingWizard;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace IPhoneMirror.App.Windows;

public partial class KeyboardMappingEditorWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly Func<Action<MappedKey>, string?> _beginCapture;
    private readonly Action _endCapture;
    private readonly Func<KeyboardMappingEntry, Guid?, string?> _save;
    private readonly Func<KeyboardMappingEntry, Action<KeyboardMappingEntry?>, string?>? _beginPick;
    private readonly Action? _cancelPick;
    private readonly Func<MappingPreviewSurface?>? _previewSurface;
    private readonly KeySelectionStep _keyView = new();
    private readonly ActionSelectionStep _actionView = new();
    private readonly ParameterStep _parameterView = new();
    private readonly PositionStep _positionView = new();
    private readonly ConfirmationStep _confirmationView = new();
    private readonly DispatcherTimer _statusTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _completionTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _loading, _closed, _committed, _discard, _picking, _disconnected, _everConnected;
    private long _captureGeneration, _pickGeneration;
    private MappingPreviewSurface? _lastSurface;
    private string? _captureError, _previewError, _pickError;
    private Action? _resumeOwnerClose;
    internal KeyboardMappingWizardState Wizard { get; }
    internal bool IsEditorClosed => _closed;
    private bool CanAdvance => !Wizard.Capturing && Wizard.CanNext && !_picking && !_disconnected && !_committed && _captureError is null;
    internal MappingEditorState State => _picking ? MappingEditorState.PickingPosition : Wizard.Capturing ? MappingEditorState.WaitingForKey :
        !Wizard.HasKey ? MappingEditorState.Idle : Wizard.HasPosition ? MappingEditorState.MappingReady : MappingEditorState.KeyCaptured;

    internal KeyboardMappingEditorWindow(KeyboardMappingEntry? entry, IReadOnlyList<KeyboardMappingEntry> mappings,
        Func<MappedKey, string?> conflict, Func<Action<MappedKey>, string?> beginCapture,
        Action endCapture, Func<KeyboardMappingEntry, Guid?, string?> save,
        Func<KeyboardMappingEntry, Action<KeyboardMappingEntry?>, string?>? beginPick = null,
        Action? cancelPick = null, Func<MappingPreviewSurface?>? previewSurface = null)
    {
        (_beginCapture, _endCapture, _save, _beginPick, _cancelPick, _previewSurface) =
            (beginCapture, endCapture, save, beginPick, cancelPick, previewSurface);
        Wizard = new(entry, mappings, conflict);
        InitializeComponent();
        DataContext = Wizard;
        foreach (var view in new FrameworkElement[] { _keyView, _actionView, _parameterView, _positionView, _confirmationView }) view.DataContext = Wizard;
        RegisterName("CaptureButton", _keyView.CaptureButton);
        RegisterName("ActionBox", _actionView.ActionBox);
        RegisterName("DirectionBox", _parameterView.DirectionBox);
        RegisterName("DirectionPanel", _parameterView.DirectionPanel);
        RegisterName("DurationBox", _parameterView.DurationBox);
        RegisterName("DurationPanel", _parameterView.DurationPanel);
        RegisterName("IntervalBox", _parameterView.IntervalBox);
        RegisterName("IntervalPanel", _parameterView.IntervalPanel);
        RegisterName("PickButton", _positionView.PickButton);
        _keyView.CaptureButton.Click += OnCaptureClick;
        _actionView.ActionBox.SelectionChanged += OnActionChanged;
        _parameterView.DirectionBox.SelectionChanged += OnDirectionChanged;
        _positionView.PickButton.Click += OnPickClick;
        _positionView.AddTargetButton.Click += (_, _) => PickPosition(true);
        _positionView.RemoveTargetButton.Click += (_, _) => Wizard.RemoveTarget();
        _parameterView.DownKeyButton.Click += (_, _) => StartCapture(1);
        _parameterView.LeftKeyButton.Click += (_, _) => StartCapture(2);
        _parameterView.RightKeyButton.Click += (_, _) => StartCapture(3);
        _keyView.InputBox.SelectionChanged += (_, _) =>
        { if (!_loading && _keyView.InputBox.SelectedItem is MappingInputOption option) { StopCapture(); Wizard.SetInput(option.Kind, option.Button); } };
        Wizard.PropertyChanged += OnDraftChanged;
        _statusTimer.Tick += OnPreviewStatusTick;
        _completionTimer.Tick += (_, _) => { _completionTimer.Stop(); Close(); };
        Loaded += (_, _) => { _statusTimer.Start(); EnterStep(); };
        PreviewKeyDown += OnWizardKeyDown;
        SizeChanged += (_, _) => RefreshLayout();
        StepScroller.SizeChanged += (_, _) => RefreshLayout();
        Closing += OnClosing;
        Closed += OnClosed;
        RefreshOptions(); Refresh();
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
        {
            if (_captureError is not null) _captureError = LocalizationService.RefreshText(_captureError);
            if (_previewError is not null) _previewError = LocalizationService.RefreshText(_previewError);
            if (_pickError is not null) _pickError = LocalizationService.RefreshText(_pickError);
            RefreshOptions(); Wizard.Notify();
        });
    }
    private static string L(string key) => LocalizationService.Get(key);
    private void RefreshOptions()
    {
        _loading = true;
        _actionView.ActionBox.SelectedValuePath = "Value";
        _actionView.ActionBox.ItemsSource = new[]
        {
            new MappingActionOption(MappedTouchAction.Tap, SymbolRegular.Cursor20),
            new MappingActionOption(MappedTouchAction.LongPress, SymbolRegular.HandLeft20),
            new MappingActionOption(MappedTouchAction.HoldUntilRelease, SymbolRegular.HandLeft20),
            new MappingActionOption(MappedTouchAction.DoubleTap, SymbolRegular.CursorClick20),
            new MappingActionOption(MappedTouchAction.Swipe, SymbolRegular.ArrowSwap20),
            new MappingActionOption(MappedTouchAction.Joystick, SymbolRegular.ArrowSwap20),
            new MappingActionOption(MappedTouchAction.RelativeDrag, SymbolRegular.Cursor20),
            new MappingActionOption(MappedTouchAction.CycleTargets, SymbolRegular.CursorClick20),
            new MappingActionOption(MappedTouchAction.ReleasePointer, SymbolRegular.HandLeft20),
        };
        _keyView.InputBox.ItemsSource = new[] { new MappingInputOption(MappingInputKind.Keyboard),
            new MappingInputOption(MappingInputKind.MouseButton, 1), new MappingInputOption(MappingInputKind.MouseButton, 2),
            new MappingInputOption(MappingInputKind.MouseButton, 4), new MappingInputOption(MappingInputKind.MouseButton, 8), new MappingInputOption(MappingInputKind.MouseButton, 16),
            new MappingInputOption(MappingInputKind.WheelUp), new MappingInputOption(MappingInputKind.WheelDown) };
        _parameterView.DirectionBox.ItemsSource = new[] { MappedTouchAction.Swipe, MappedTouchAction.SwipeUp,
            MappedTouchAction.SwipeDown, MappedTouchAction.SwipeLeft, MappedTouchAction.SwipeRight }
            .Select(a => new MappingSwipeDirectionOption(a)).ToArray();
        SyncSelection(); _loading = false;
    }
    private void SyncSelection()
    {
        _keyView.InputBox.SelectedItem = _keyView.InputBox.Items.Cast<MappingInputOption>().FirstOrDefault(o => o.Kind == Wizard.Draft.InputKind &&
            (o.Kind != MappingInputKind.MouseButton || o.Button == Wizard.Draft.MouseButton));
        _actionView.ActionBox.SelectedValue = Wizard.HasAction ? Wizard.IsSwipe ? MappedTouchAction.Swipe : Wizard.Draft.Action : null;
        _parameterView.DirectionBox.SelectedValue = Wizard.IsSwipe ? Wizard.Draft.Action : MappedTouchAction.Swipe;
    }
    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        if (_closed) return;
        _loading = true; SyncSelection(); _loading = false;
        StepContent.Content = Wizard.Current switch
        {
            MappingWizardStep.Key => _keyView, MappingWizardStep.Action => _actionView,
            MappingWizardStep.Parameters => _parameterView, MappingWizardStep.Position => _positionView, _ => _confirmationView,
        };
        PreviousButton.Visibility = Wizard.CanPrevious ? Visibility.Visible : Visibility.Hidden;
        SaveButton.IsEnabled = CanAdvance;
        PreviousButton.IsEnabled = !_picking && !_committed;
        _positionView.PickButton.IsEnabled = _beginPick is not null && !_picking && !_disconnected;
        _positionView.AddTargetButton.IsEnabled = Wizard.HasPosition && Wizard.Draft.Targets.Length < 31 && _positionView.PickButton.IsEnabled;
        _positionView.RemoveTargetButton.IsEnabled = Wizard.Draft.Targets.Length > 0 && !_picking;
        _positionView.PickButton.SetResourceReference(ContentControl.ContentProperty,
            Wizard.HasPosition ? "MappingRepick" : Wizard.IsSwipe ? "WizardRecordGesture" : "MappingPick");
        _positionView.PickStatus.Text = L(Wizard.HasPosition ? Wizard.IsSwipe ? "WizardGestureSelected" : "WizardPositionSelected" : "WizardReadyToPick");
        var error = Wizard.ErrorFor(Wizard.Current);
        RefreshError();
        ConflictPanel.Visibility = error == "WizardDuplicate" ? Visibility.Visible : Visibility.Collapsed;
        RefreshIndicator(); RefreshLayout();
    }
    private void RefreshError()
    {
        var error = Wizard.Current switch
        {
            MappingWizardStep.Key when Wizard.HasKey => Wizard.ErrorFor(Wizard.Current),
            MappingWizardStep.Parameters or MappingWizardStep.Confirmation => Wizard.ErrorFor(Wizard.Current),
            MappingWizardStep.Position when Wizard.HasPosition => Wizard.ErrorFor(Wizard.Current),
            _ => null,
        };
        if (Wizard.Current == MappingWizardStep.Confirmation && error == "MappingPickRequired")
            error = "WizardRepickRequired";
        ErrorText.Text = _previewError ?? _pickError ?? _captureError ?? (error is null ? "" : L(error));
    }
    private void RefreshIndicator()
    {
        StepIndicator.Children.Clear(); StepIndicator.ColumnDefinitions.Clear();
        var steps = Wizard.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            if (i > 0)
            {
                StepIndicator.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
                var line = new Border { Height = 1, Width = 24, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 17, 0, 0) };
                line.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
                Grid.SetColumn(line, i * 2 - 1); StepIndicator.Children.Add(line);
            }
            StepIndicator.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var step = steps[i];
            var current = step == Wizard.Current;
            var complete = Wizard.IsComplete(step);
            var label = L("WizardStep" + step);
            var panel = new StackPanel { MinWidth = 46 };
            var mark = new TextBlock { Text = current ? "●" : complete ? "✓" : "○", FontSize = 18, TextAlignment = TextAlignment.Center };
            mark.SetResourceReference(TextBlock.ForegroundProperty, current ? "AccentBrush" : "MutedTextBrush");
            panel.Children.Add(mark);
            panel.Children.Add(new TextBlock { Text = label, FontSize = 11, TextAlignment = TextAlignment.Center,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 4, 0, 0) });
            var button = new Button { Content = panel, Padding = new Thickness(4), IsEnabled = Wizard.CanVisit(step), Focusable = !current,
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            button.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(button, label + (current ? " · " + Wizard.ProgressText : ""));
            button.Click += (_, _) => Navigate(() => Wizard.Visit(step), (int)step < (int)Wizard.Current ? -1 : 1);
            Grid.SetColumn(button, i * 2); StepIndicator.Children.Add(button);
        }
    }
    private void RefreshLayout()
    {
        var compact = StepScroller.ActualWidth < 520;
        CompactIndicator.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        StepIndicator.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        StepNumberText.Visibility = StepTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        StepHeading.Margin = new Thickness(0, 0, 0, compact ? 12 : 24);
        StepPage.MinHeight = Math.Max(300, StepScroller.ActualHeight - 24);
    }
    private void EnterStep()
    {
        if (_closed || _committed) return;
        Refresh(); StepScroller.ScrollToTop();
        if (Wizard.Current == MappingWizardStep.Key)
        {
            _keyView.CaptureButton.Focus();
            if (Wizard.Draft.Key is null) StartCapture();
        }
        else if (Wizard.Current == MappingWizardStep.Action) _actionView.ActionBox.Focus();
        else if (Wizard.Current == MappingWizardStep.Parameters)
        {
            if (Wizard.ShowDuration) _parameterView.DurationBox.Focus();
            else if (Wizard.ShowInterval) _parameterView.IntervalBox.Focus();
            else _parameterView.DirectionBox.Focus();
        }
        else if (Wizard.Current == MappingWizardStep.Position) { OnPreviewStatusTick(this, EventArgs.Empty); if (Wizard.HasPosition) SaveButton.Focus(); else _positionView.PickButton.Focus(); }
        else SaveButton.Focus();
    }
    private void Navigate(Func<bool> move, int direction)
    {
        if (_picking || _committed) return;
        var snapshot = WizardPageTransition.Snapshot(StepScroller);
        StopCapture();
        _captureError = _previewError = _pickError = null;
        if (!move()) { Refresh(); return; }

        EnterStep();
        WizardPageTransition.Play(StepPage, OutgoingPage, snapshot, direction);
    }
    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        OnPreviewStatusTick(this, EventArgs.Empty);
        if (!SaveButton.IsEnabled) return;
        if (Wizard.Current == MappingWizardStep.Confirmation) OnSaveClick(sender, e);
        else Navigate(Wizard.MoveNext, 1);
    }
    private void OnPreviousClick(object sender, RoutedEventArgs e) => Navigate(Wizard.MovePrevious, -1);
    private void OnWizardKeyDown(object sender, KeyEventArgs e)
    {
        if (_committed) { e.Handled = true; return; }
        if (DiscardPanel.Visibility == Visibility.Visible)
        { if (e.Key == Key.Escape) { OnContinueEditingClick(sender, e); e.Handled = true; } return; }
        // Capture owns physical Enter/Escape too; navigation resumes after release.
        if (Wizard.Capturing) return;
        e.Handled = HandleNavigationKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
    }
    internal bool HandleNavigationKey(Key key, ModifierKeys modifiers)
    {
        if (Wizard.Capturing || _closed || _committed) return false;
        if (key == Key.Escape) { Close(); return true; }
        if (modifiers == ModifierKeys.Alt && key == Key.Left)
        { OnPreviousClick(this, new RoutedEventArgs()); return true; }
        if (modifiers == ModifierKeys.Alt && key == Key.Right ||
            key == Key.Enter && modifiers == ModifierKeys.None &&
            (Keyboard.FocusedElement is not Button || ReferenceEquals(Keyboard.FocusedElement, _keyView.CaptureButton)) &&
            !_parameterView.DirectionBox.IsDropDownOpen)
        { OnNextClick(this, new RoutedEventArgs()); return true; }
        return false;
    }
    private void OnCaptureClick(object sender, RoutedEventArgs e)
    { if (Wizard.Capturing) StopCapture(); else StartCapture(); }
    private void StartCapture(int direction = 0)
    {
        if (_closed || (direction == 0 ? Wizard.Current != MappingWizardStep.Key || !Wizard.IsKeyboard : Wizard.Current != MappingWizardStep.Parameters)) return;
        StopCapture();
        _captureError = null; Wizard.Capturing = true;
        var generation = ++_captureGeneration; Wizard.Notify();
        var error = _beginCapture(key =>
        {
            if (_closed || !Wizard.Capturing || generation != _captureGeneration) return;
            StopCapture();
            if (key.Validate() is { } invalid) { _captureError = L(invalid); Refresh(); return; }
            if (direction == 0) Wizard.SetKey(key); else Wizard.SetDirectionKey(direction, key);
        });
        if (error is not null) { StopCapture(); _captureError = error; Refresh(); }
    }
    private void StopCapture()
    {
        ++_captureGeneration;
        if (Wizard.Capturing) _endCapture();
        Wizard.Capturing = false; Wizard.Notify();
    }
    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_loading && _actionView.ActionBox.SelectedValue is MappedTouchAction action) Wizard.SelectAction(action); }
    private void OnDirectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_loading && Wizard.IsSwipe && _parameterView.DirectionBox.SelectedValue is MappedTouchAction action) Wizard.SelectAction(action); }
    private void OnPreviewStatusTick(object? sender, EventArgs e)
    {
        if (!Wizard.NeedsPosition || _closed || _committed || _previewSurface is null || !IsVisible || DiscardPanel.Visibility == Visibility.Visible) return;
        var surface = _previewSurface();
        var disconnected = surface is null && _everConnected;
        if (surface is not null)
        {
            if (_lastSurface is { } previous && (previous.Device != surface.Device || previous.Width != surface.Width ||
                previous.Height != surface.Height || previous.Rotation != surface.Rotation || previous.Portrait != surface.Portrait ||
                previous.Landscape != surface.Landscape || previous.ReverseX != surface.ReverseX || previous.ReverseY != surface.ReverseY))
                Wizard.InvalidatePosition();
            _lastSurface = surface; _everConnected = true;
            Wizard.SetPreviewSize(surface.Width, surface.Height);
        }
        var statusChanged = disconnected != _disconnected;
        _disconnected = disconnected;
        if (_disconnected) { _previewError = L("WizardDisconnected"); }
        else if (statusChanged) _previewError = null;
        if (Wizard.Current == MappingWizardStep.Position)
        {
            _previewError = surface is null && !Wizard.HasPosition
                ? L(_disconnected ? "WizardDisconnected" : "MappingPickNoPreview") : _disconnected ? L("WizardDisconnected") : null;
            _positionView.PickButton.IsEnabled = surface is not null && !_picking && _beginPick is not null;
        }
        if (Wizard.Current == MappingWizardStep.Position || _disconnected || statusChanged ||
            Wizard.Current == MappingWizardStep.Confirmation && !Wizard.CanNext)
            RefreshError();
        SaveButton.IsEnabled = CanAdvance;
    }
    private void OnPickClick(object sender, RoutedEventArgs e) => PickPosition(false);
    private void PickPosition(bool additional)
    {
        if (Wizard.Current != MappingWizardStep.Position || _picking || _beginPick is null || !Wizard.HasKey) return;
        StopCapture();
        _picking = true; _previewError = _pickError = null;
        var generation = ++_pickGeneration; Refresh();
        var error = _beginPick(Wizard.Draft, result =>
        {
            if (_closed || !_picking || generation != _pickGeneration || Wizard.Current != MappingWizardStep.Position) return;
            ++_pickGeneration;
            _picking = false;
            if (result is not null) { if (additional) Wizard.AddTarget(result); else Wizard.SetPosition(result); }
            Refresh();
        });
        if (error is not null) { _picking = false; _pickError = error; Refresh(); }
    }
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_closed || _committed || Wizard.Current != MappingWizardStep.Confirmation || _disconnected || Wizard.BuildEntry() is not { } entry) return;
        StopCapture();
        if (_save(entry, Wizard.ReplacementId) is { } error) { ErrorText.Text = error; return; }
        _committed = true; _statusTimer.Stop();
        SuccessText.Text = $"{Wizard.KeyText} → {Wizard.ActionText}";
        SuccessPanel.Visibility = Visibility.Visible; SetEditingEnabled(false);
        _completionTimer.Start();
    }
    private void OnReplaceClick(object sender, RoutedEventArgs e) => Wizard.AcceptReplacement();
    private void OnEditOriginalClick(object sender, RoutedEventArgs e)
    { if (Wizard.Duplicate is { } original) { StopCapture(); Wizard.Load(original); EnterStep(); } }
    private void LoadEntry(KeyboardMappingEntry entry) { StopCapture(); Wizard.Load(entry); EnterStep(); }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    internal bool RequestOwnerClose(Action resumeClose)
    {
        _resumeOwnerClose = resumeClose;
        Close();
        return _closed;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_committed || _discard || !Wizard.IsDirty) return;
        e.Cancel = true; StopCapture();
        DiscardPanel.Visibility = Visibility.Visible; SetEditingEnabled(false); ContinueButton.Focus();
    }
    private void OnContinueEditingClick(object sender, RoutedEventArgs e)
    { _resumeOwnerClose = null; DiscardPanel.Visibility = Visibility.Collapsed; SetEditingEnabled(true); EnterStep(); }
    private void SetEditingEnabled(bool enabled)
    { StepScroller.IsEnabled = enabled; NavigationBar.IsEnabled = enabled; StepIndicator.IsEnabled = enabled; }
    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        var resumeClose = _resumeOwnerClose;
        _discard = true; Close();
        if (resumeClose is not null) Dispatcher.BeginInvoke(resumeClose);
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true; _resumeOwnerClose = null; ++_pickGeneration; StopCapture(); _endCapture();
        _statusTimer.Stop(); _completionTimer.Stop(); _statusTimer.Tick -= OnPreviewStatusTick;
        _cancelPick?.Invoke();
        Wizard.PropertyChanged -= OnDraftChanged; StepPage.BeginAnimation(OpacityProperty, null);
        OutgoingPage.BeginAnimation(OpacityProperty, null); OutgoingPage.Source = null;
    }
}
