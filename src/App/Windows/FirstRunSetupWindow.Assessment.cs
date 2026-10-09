using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Windows;

public partial class FirstRunSetupWindow
{
    internal SetupCheckRuntime CheckRuntime { get; set; } = new();
    private bool _assessmentVisible, _assessing, _smartSetup, _fullSetup, _selectingTask;
    private CancellationTokenSource? _assessmentCancellation;
    private IReadOnlyList<SetupCheckResult> _assessmentResults = [];
    private readonly HashSet<string> _deferredTasks = new(StringComparer.OrdinalIgnoreCase);
    private sealed record AssessmentRowView(Border Root, System.Windows.Shapes.Path Mark, TextBlock Name, TextBlock State, TextBlock Hint);
    private readonly Dictionary<string, AssessmentRowView> _checkRows = [];
    private StackPanel? _assessmentItems;
    private WrapPanel? _assessmentActions;
    private SetupTask? _activeTask;
    private SetupAssessmentSession? _assessmentSession;
    private bool _refreshingTask;
    internal IReadOnlyList<SetupCheckResult> AssessmentResults => _assessmentResults;
    internal bool IsAssessing => _assessing || _refreshingTask;

    private async Task CheckSettingsAsync()
    {
        if (_assessing || _refreshingTask || _closed) return;
        StopDiscovery(); _assessing = true; _assessmentVisible = true;
        _assessmentResults = [];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _assessmentCancellation = cancellation;
        var token = cancellation.Token;
        var timer = Stopwatch.StartNew();
        RenderAssessment(); AnimatePage(1); AnimateAssessmentSpinner(PageSymbol);
        try
        {
            await _discoveryTask;
            var progress = new Progress<SetupCheckProgress>(p =>
            {
                if (_closed || token.IsCancellationRequested || !_assessing) return;
                UpdateAssessmentRow(p.Id, p.NameKey, p.Result, animate: true);
            });
            _assessmentSession = new SetupAssessmentSession(CheckRuntime);
            _assessmentResults = await _assessmentSession.CheckAsync(_state, progress, token);
            token.ThrowIfCancellationRequested();
            if (SystemParameters.ClientAreaAnimation && timer.ElapsedMilliseconds < 550)
                await Task.Delay((int)(550 - timer.ElapsedMilliseconds), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _assessmentResults = [new("cancelled", "SetupTitle", SetupState.Unknown, SetupStep.Validation,
                DetailKey: "SetupCancelled")];
        }
        catch (Exception error)
        {
            _assessmentResults = [new("failed", "SetupTitle", SetupState.Unknown, SetupStep.Validation,
                DetailKey: "SetupCheckUnavailable", Detail: error.Message)];
        }
        finally
        {
            StopAssessmentAnimations(); _assessing = false; _assessmentCancellation = null;
            ReleaseSetupOwnership();
        }
        if (_closed) return;
        RenderAssessment(preserveRows: true); AnimateAssessmentResult();
    }

    private void ShowAssessmentSummary()
    {
        StopDiscovery();
        _assessmentVisible = true;
        RenderAssessment(); AnimateAssessmentResult();
    }

    private async Task AdvancePendingTasksAsync(bool skip = false)
    {
        if (_closed || _refreshingTask || _selectingTask) return;
        if (!skip)
        {
            _refreshingTask = true; StopDiscovery();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _assessmentCancellation = cancellation;
            Status.Text = L("SetupWorking"); UpdateNavigation();
            try
            {
                await _discoveryTask;
                _assessmentSession ??= new SetupAssessmentSession(CheckRuntime);
                var results = await _assessmentSession.RefreshAsync(_state, _state.Step, CurrentTaskDeviceId(), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _assessmentResults = results;
            }
            catch (OperationCanceledException) { if (!_closed) Status.Text = L("SetupCancelled"); return; }
            catch (Exception error) { if (!_closed) Failure(error); return; }
            finally
            {
                _refreshingTask = false; _assessmentCancellation = null; ReleaseSetupOwnership();
                if (!_closed) UpdateNavigation();
            }
        }
        if (_closed) return;
        var tasks = SetupTaskPlanner.Plan(_assessmentResults, _deferredTasks);
        if (!skip && tasks.FirstOrDefault(t => t.Key == _activeTask?.Key) is { } unresolved)
        {
            // An attempted repair is not proof of success. Keep its explanation on the current page.
            _activeTask = unresolved;
            var check = unresolved.Checks.First();
            Status.Text = L("SetupState" + check.State) + " · " + (check.DetailKey is { } key ? L(key) : check.Detail);
            Status.Visibility = Visibility.Visible; return;
        }
        if (tasks.Count == 0) { ShowAssessmentSummary(); return; }
        await ContinueAssessmentAsync();
    }

    private void RefreshAssessmentLabels()
    {
        var remaining = _assessmentResults.Count(r => r.NeedsAttention);
        Heading.Text = L(_assessing ? "SetupCheckHeading" : remaining == 0 ? "SetupCheckReady" : "SetupCheckFinished");
        Description.Text = _assessing ? L("SetupCheckDescription") : remaining == 0 ? L("SetupCheckReadyHint") :
            LocalizationService.Format("SetupCheckRemaining", remaining);
        DeviceLabel.Visibility = Visibility.Collapsed;
        StepIndicator.Visibility = Visibility.Collapsed;
        PageSymbol.Data = _assessing ? Geometry.Parse("M12,2 A10,10 0 1 1 2,12") : Symbol(remaining == 0 ? "check" : "settings");
    }
    private void RenderAssessment(bool preserveRows = false)
    {
        StopAssessmentAnimations(); RefreshAssessmentLabels();
        PromptPanel.Children.Clear(); _installationPanel = null;
        Status.Text = Details.Text = ""; Help.Visibility = DetailsExpander.Visibility = Visibility.Collapsed;
        Page.Margin = new(16, 4, 16, 4); Page.MaxWidth = 600; Heading.FontSize = 28;
        Scroller.VerticalContentAlignment = VerticalAlignment.Top;
        PageSymbol.Width = PageSymbol.Height = 40;
        PageSymbol.Margin = new(0, 0, 0, 16);
        PageSymbol.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            _assessing ? "AccentBrush" : _assessmentResults.Any(r => r.NeedsAttention) ? "WarningBrush" : "SuccessBrush");
        Description.Margin = new(12, 10, 12, 12); Description.MinHeight = 32;
        if (!preserveRows || _assessmentItems is null)
        {
            Body.Children.Clear(); _actions.Clear(); _checkRows.Clear();
            _assessmentItems = new StackPanel { Margin = new(0, 0, 16, 0) };
            // Reserve the same card and action area throughout the scan, so rows do not move with the heading.
            var list = new ScrollViewer { Content = _assessmentItems, Height = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var card = new Border { CornerRadius = new(16), BorderThickness = new(1), Padding = new(20, 4, 20, 4), Child = list };
            card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
            Body.Children.Add(card);
            _assessmentActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 10, 0, 0) };
            Body.Children.Add(_assessmentActions);
            foreach (var button in new[] { Action("SetupCheckAgain", () => CheckSettingsAsync(), _assessmentActions),
                Action("SetupCheckReconfigure", ReconfigureAllAsync, _assessmentActions) })
            {
                button.SetResourceReference(StyleProperty, "GhostButton");
                button.SetResourceReference(ForegroundProperty, "MutedTextBrush");
                button.FontSize = 12; button.Padding = new(12, 8, 12, 8); button.Margin = new(4, 0, 4, 0);
            }
        }
        _assessmentActions!.Visibility = _assessing ? Visibility.Hidden : Visibility.Visible;
        if (_assessing)
        {
            foreach (var step in new[] { SetupStep.Preferences, SetupStep.ApplicationMode, SetupStep.Appearance })
                UpdateAssessmentRow(step.ToString(), "SetupCheck" + step, null, waiting: true);
            UpdateAssessmentRow("usage", "SetupCheckUsage", null, waiting: true);
        }
        else
        {
            foreach (var result in _assessmentResults)
                UpdateAssessmentRow(result.Id, result.NameKey, result);
            foreach (var pair in _checkRows.Where(p => !_assessmentResults.Any(r => r.Id == p.Key)))
                pair.Value.Root.Visibility = Visibility.Collapsed;
            RefreshAssessmentSeparators();
            Details.Text = string.Join("\n\n", _assessmentResults.Where(r => r.NeedsAttention && r.DetailKey is not null && !string.IsNullOrWhiteSpace(r.Detail))
                .Select(r => L(r.NameKey) + ": " + r.Detail));
            DetailsExpander.Visibility = string.IsNullOrWhiteSpace(Details.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateNavigation();
    }
    private AssessmentRowView CreateAssessmentRow()
    {
        var grid = new Grid { Margin = new(0, 10, 0, 10) };
        grid.ColumnDefinitions.Add(new() { Width = new(30) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(100) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var mark = new System.Windows.Shapes.Path { Width = 16, Height = 16, Stretch = Stretch.Uniform,
            StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(mark);
        var name = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        var state = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(state, 2); grid.Children.Add(state);
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 6, 0, 0), LineHeight = 18, Visibility = Visibility.Collapsed };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Grid.SetColumn(hint, 1); Grid.SetColumnSpan(hint, 2); Grid.SetRow(hint, 1); grid.Children.Add(hint);
        var row = new Border { Child = grid };
        row.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return new(row, mark, name, state, hint);
    }

    private void UpdateAssessmentRow(string id, string nameKey, SetupCheckResult? result, bool animate = false, bool waiting = false)
    {
        if (!_checkRows.TryGetValue(id, out var row))
        {
            row = CreateAssessmentRow(); _checkRows[id] = row; _assessmentItems!.Children.Add(row.Root);
        }
        row.Root.Visibility = result?.State == SetupState.NotRequired ? Visibility.Collapsed : Visibility.Visible;
        row.Name.SetResourceReference(TextBlock.TextProperty, nameKey);
        row.State.SetResourceReference(TextBlock.TextProperty, result is null ? waiting ? "SetupCheckWaiting" : "SetupCheckRunning" : "SetupState" + result.State);
        var color = result is null ? waiting ? "MutedTextBrush" : "AccentBrush" : result.NeedsAttention ? "WarningBrush" : "SuccessBrush";
        row.Mark.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, color);
        row.State.SetResourceReference(TextBlock.ForegroundProperty, result is { NeedsAttention: false } ? "MutedTextBrush" : color);
        row.Mark.Data = Geometry.Parse(result is null ? waiting
            ? "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2" : "M8,2 A6,6 0 1 1 2,8"
            : result.NeedsAttention ? "M8,1 A7,7 0 1 1 8,15 A7,7 0 1 1 8,1 M8,4 L8,8 M8,11 L8,11.5" : "M2,8 L6,12 14,3");
        StopAssessmentRowAnimation(row);
        // Keep diagnostics out of completed rows; device names still disambiguate multiple targets.
        var detail = result?.NeedsAttention == true ? result.DetailKey is null ? result.Detail : L(result.DetailKey) : null;
        if (result?.DeviceId is not null)
            detail = (_state.Devices.FirstOrDefault(d => SetupAssessment.SameDevice(d.Id, result.DeviceId))?.Name ?? result.DeviceId) +
                (string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail);
        row.Hint.Text = detail;
        row.Hint.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        RefreshAssessmentSeparators();
        if (!animate || !SystemParameters.ClientAreaAnimation || _previewOnly || row.Root.Visibility != Visibility.Visible) return;
        if (result is null)
        {
            AnimateAssessmentSpinner(row.Mark);
            row.State.BeginAnimation(OpacityProperty, new DoubleAnimation(.5, 1, TimeSpan.FromMilliseconds(550))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else if (result.State == SetupState.Completed) AnimateCheckmark(row.Mark);
        else if (result.NeedsAttention)
        {
            var shift = new TranslateTransform(); row.Mark.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(2, 0, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        }
    }
    private void RefreshAssessmentSeparators()
    {
        var first = true;
        foreach (var row in _checkRows.Values.Where(r => r.Root.Visibility == Visibility.Visible))
        { row.Root.BorderThickness = new(0, first ? 0 : 1, 0, 0); first = false; }
    }
    private void AnimateAssessmentSpinner(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation || _previewOnly) return;
        var rotation = new RotateTransform(); element.RenderTransform = rotation; element.RenderTransformOrigin = new(.5, .5);
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
    }
    private static void StopAssessmentRowAnimation(AssessmentRowView row)
    {
        row.State.BeginAnimation(OpacityProperty, null);
        if (row.Mark.RenderTransform is RotateTransform rotation) rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        row.Mark.RenderTransform = Transform.Identity;
    }

    private void UpdateAssessmentNavigation()
    {
        Previous.Visibility = Visibility.Hidden; Previous.IsEnabled = false;
        CancelOperation.Visibility = _assessing ? Visibility.Visible : Visibility.Collapsed;
        CancelOperation.IsEnabled = _assessmentCancellation?.IsCancellationRequested == false;
        Skip.Visibility = !_assessing && _assessmentResults.Any(r => r.NeedsAttention) ? Visibility.Visible : Visibility.Collapsed;
        Skip.IsEnabled = !_selectingTask; Skip.SetResourceReference(ContentProperty, "SetupDefer");
        Next.IsEnabled = !_assessing && !_selectingTask;
        Next.Visibility = _assessing ? Visibility.Hidden : Visibility.Visible;
        foreach (var action in _actions) action.IsEnabled = !_assessing && !_selectingTask;
        var tasks = SetupTaskPlanner.Plan(_assessmentResults, _deferredTasks);
        Next.SetResourceReference(ContentProperty, _assessmentResults.All(r => !r.NeedsAttention) ? "SetupCheckDone" :
            tasks.Count > 0 ? "SetupContinue" : "SetupCheckDoneLater");
        BusyBar.Visibility = DownloadProgress.Visibility = Visibility.Collapsed;
        Status.Visibility = string.IsNullOrWhiteSpace(Status.Text) ? Visibility.Collapsed : Visibility.Visible;
        PromptPanel.Visibility = PromptPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Body.Visibility = Visibility.Visible;
        Body.IsEnabled = !_selectingTask;
    }

    private async Task ContinueAssessmentAsync()
    {
        if (_assessing || _refreshingTask || _closed || _selectingTask) return;
        _selectingTask = true; UpdateNavigation();
        try { await ContinueAssessmentCoreAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) Failure(error); }
        finally { _selectingTask = false; ReleaseSetupOwnership(); if (!_closed) UpdateNavigation(); }
    }
    private async Task ContinueAssessmentCoreAsync()
    {
        var tasks = SetupTaskPlanner.Plan(_assessmentResults, _deferredTasks);
        if (tasks.Count == 0 || _assessmentResults.All(r => !r.NeedsAttention))
        {
            var before = _state.Disposition;
            _state.Disposition = _assessmentResults.All(r => !r.NeedsAttention) ? SetupDisposition.Completed : SetupDisposition.InProgress;
            if (Save()) { _allowClose = true; Close(); } else _state.Disposition = before;
            return;
        }
        var task = tasks[0];
        if (task.Step == SetupStep.Validation) { await CheckSettingsAsync(); return; }
        await _discoveryTask;
        if (_closed) return;
        // Read-only results can recover an existing target without resetting its history.
        var missingIds = _assessmentResults.Select(r => r.DeviceId).Where(id => id is not null &&
            !_state.Devices.Any(d => SetupAssessment.SameDevice(d.Id, id))).Distinct().ToArray();
        var bindings = missingIds.Length > 0 ? await CheckRuntime.ReadBindingsAsync(_lifetime.Token) : null;
        if (_closed) return;
        var checkpoint = System.Text.Json.JsonSerializer.Serialize(_state);
        foreach (var id in missingIds)
            if (!_state.Devices.Any(d => SetupAssessment.SameDevice(d.Id, id!)))
            {
                var profile = bindings!.FindByIdentity(_state.Usage == SetupUsage.WirelessOnly ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired, id!);
                _state.Devices.Add(new() { Id = id!, Name = profile?.DisplayName ?? id! });
            }
        foreach (var result in _assessmentResults.Where(r => r.State == SetupState.Completed))
        {
            if (result.DeviceId is { } id && _state.Devices.FirstOrDefault(d => SetupAssessment.SameDevice(d.Id, id)) is { } device)
                device.Outcomes[result.Step] = SetupOutcome.Verified;
        }
        var deviceIndex = _state.Devices.FindIndex(d => task.DeviceId is not null && SetupAssessment.SameDevice(d.Id, task.DeviceId));
        _state.DeviceIndex = Math.Max(0, deviceIndex);
        _state.Step = task.Step;
        if (_state.CurrentDevice is null && _state.Step is SetupStep.Environment or SetupStep.Bluetooth or SetupStep.Profile or SetupStep.ReDetection)
            _state.Step = SetupStep.Connection;
        _state.PreserveKnownDevices = true; _state.Disposition = SetupDisposition.InProgress;
        if (!Save()) { _state = System.Text.Json.JsonSerializer.Deserialize<FirstRunSetupState>(checkpoint)!; return; }
        _activeTask = task;
        _assessmentVisible = false; _smartSetup = true;
        StepIndicator.Visibility = Visibility.Visible; Enter();
    }
    private string? CurrentTaskDeviceId() => _activeTask?.DeviceId;
    private bool ContinueSetupSubflow() => _state.Step is SetupStep.Usage or SetupStep.Display or
        SetupStep.WirelessDisplay or SetupStep.WiredDisplay or SetupStep.WiredFrameRate or SetupStep.WiredDecoder;

    private Task ReconfigureAllAsync()
    {
        var previous = _state;
        _state = new() { PreferencesApplied = true, Disposition = SetupDisposition.InProgress };
        if (!Save()) { _state = previous; return Task.CompletedTask; }
        _smartSetup = false; _fullSetup = true; _assessmentVisible = false; _deferredTasks.Clear();
        StepIndicator.Visibility = Visibility.Visible; Enter();
        return Task.CompletedTask;
    }
    private void StopAssessmentAnimations()
    {
        foreach (var row in _checkRows.Values) StopAssessmentRowAnimation(row);
        if (PageSymbol.RenderTransform is RotateTransform rotation) rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        PageSymbol.BeginAnimation(OpacityProperty, null);
        if (PageSymbol.RenderTransform is ScaleTransform scale)
        { scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); }
    }
    private void AnimateAssessmentResult()
    {
        if (!SystemParameters.ClientAreaAnimation || _previewOnly) return;
        AnimateCheckmark(PageSymbol);
    }
    private static void AnimateCheckmark(FrameworkElement element)
    {
        element.RenderTransformOrigin = new(.5, .5);
        var scale = new ScaleTransform(1, 1); element.RenderTransform = scale;
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(.85, TimeSpan.Zero));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, TimeSpan.FromMilliseconds(260)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation); scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
