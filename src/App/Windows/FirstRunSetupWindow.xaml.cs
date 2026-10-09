using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public partial class FirstRunSetupWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly MainViewModel _main;
    private readonly FirstRunSetupStore _store;
    private FirstRunSetupState _state;
    private CancellationTokenSource? _operation;
    private bool _busy, _verified, _allowClose, _closed, _installNeeded;
    private bool _advancing, _acceptFinalDiscovery;
    private bool _skipRequested;
    private bool _advanceCancelled;
    private TaskCompletionSource<string?>? _choice;
    private readonly List<Button> _actions = [];
    private StackPanel? _installationPanel;
    private readonly bool _previewOnly;
    private readonly CancellationTokenSource _lifetime = new();
    internal bool StartRequested { get; private set; }
    internal FirstRunSetupState State => _state;

    internal FirstRunSetupWindow(MainViewModel main, FirstRunSetupStore? store = null, bool rerun = false, bool previewOnly = false, bool checkOnOpen = true)
    {
        _previewOnly = previewOnly;
        _main = main; _store = store ?? new(); _state = _store.Load();
        CheckRuntime = new() { WiredIdentitiesAsync = async token => (await _main.SetupScanAsync(false, token)).Select(d => d.Udid).ToArray() };
        _main.SetupActive = true;
        ApplyWirelessPreferences = _main.SetupApplyWirelessPreferencesAsync;
        if (!checkOnOpen && rerun && _state.Disposition == SetupDisposition.Completed)
            _state = new() { PreferencesApplied = true };
        else if (!checkOnOpen && _state.Disposition == SetupDisposition.InProgress)
        {
            if (_state.WirelessBackendDraft is { } backend && _main.WirelessReceiverBackends.FirstOrDefault(v => v.Backend == backend) is { } backendOption)
                _main.SelectedWirelessReceiverBackend = backendOption;
            if (_main.WirelessDisplayProfiles.FirstOrDefault(v => v.Id == _state.WirelessProfileDraft) is { } profileOption)
                _main.SelectedWirelessDisplayProfile = profileOption;
        }
        InitializeComponent();
        Loaded += (_, _) => { if (checkOnOpen && !_previewOnly) _ = CheckSettingsAsync(); else Enter(); };
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; StopDiscovery(); _lifetime.Cancel(); _operation?.Cancel(); _choice?.TrySetCanceled(); StopAssessmentAnimations(); ReleaseSetupOwnership(); };
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
        {
            if (_assessmentVisible) { if (_assessing) RefreshAssessmentLabels(); else RenderAssessment(); return; }
            var verified = _verified; var status = Status.Text; var details = Details.Text;
            var help = HelpText.Text; var helpVisibility = Help.Visibility;
            // Keep the same controls, focus and animation clocks during language changes.
            RefreshLabels();
            _verified = verified; Status.Text = LocalizationService.RefreshText(status);
            Details.Text = LocalizationService.RefreshText(details); HelpText.Text = LocalizationService.RefreshText(help);
            Help.Visibility = helpVisibility; UpdateNavigation();
        });
    }
    private static string L(string key) => LocalizationService.Get(key);
    private static TextBlock Text(string key, double size = 14)
    {
        var text = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 12) };
        text.SetResourceReference(TextBlock.TextProperty, key); return text;
    }
    private Button Action(string key, Func<Task> action, Panel? parent = null)
    {
        var button = new Button { Margin = new(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
        button.SetResourceReference(ContentProperty, key);
        button.Click += async (_, _) =>
        { try { await action(); } catch (Exception error) { Failure(error); } };
        (parent ?? Body).Children.Add(button); _actions.Add(button); return button;
    }
    private void Enter(int direction = 1)
    {
        StopDiscovery();
        _verified = false; _installNeeded = false;
        Render(); Scroller.ScrollToTop();
        AnimatePage(direction);
        if (!_previewOnly && _state.Step is (SetupStep.Connection or SetupStep.Devices)) StartDiscovery();
        if (!_previewOnly && _state.Step is (SetupStep.Environment or SetupStep.ReDetection or SetupStep.Profile or SetupStep.Validation))
            _ = RunAsync(false);
    }
    private void RefreshLabels()
    {
        if (_assessmentVisible) { RefreshAssessmentLabels(); return; }
        Heading.Text = L(_state.Step == SetupStep.Completed && _state.HasSkippedSteps ? "SetupTitlePartial" : "SetupTitle" + _state.Step);
        Description.Text = L(_state.Step == SetupStep.Completed && _state.HasSkippedSteps ? "SetupHintPartial" : "SetupHint" + _state.Step);
        PageSymbol.Data = Symbol(_state.Step switch
        {
            SetupStep.Welcome or SetupStep.Usage or SetupStep.Display => "spark", SetupStep.MirrorConnection => "monitor",
            SetupStep.Preferences => "language", SetupStep.ApplicationMode => "monitor", SetupStep.Appearance => "sun",
            SetupStep.ControlIntroduction or SetupStep.WiredControl => "game",
            SetupStep.WirelessIntroduction or SetupStep.Wireless or SetupStep.WirelessControl or SetupStep.WirelessBackend => "wireless",
            SetupStep.BluetoothIntroduction or SetupStep.Bluetooth => "bluetooth", SetupStep.Environment or SetupStep.WiredDecoder => "settings",
            SetupStep.Profile => "link", SetupStep.DeviceComplete or SetupStep.Completed => "check",
            SetupStep.WiredDisplay or SetupStep.WirelessDisplay => "image", SetupStep.WiredFrameRate => "bolt",
            SetupStep.Validation => "search", _ => "phone"
        });
        DeviceLabel.Text = _state.Usage != SetupUsage.WirelessOnly && _state.CurrentDevice is { } device && _state.DeviceSteps.Contains(_state.Step)
            ? LocalizationService.Format("SetupDeviceProgress", _state.DeviceIndex + 1, _state.Devices.Count, device.Name) : "";
        if (_state.IsWiredDisplayStep && _state.Devices.ElementAtOrDefault(_state.DisplayDeviceIndex) is { } displayDevice)
            DeviceLabel.Text = LocalizationService.Format("SetupDeviceProgress", _state.DisplayDeviceIndex + 1, _state.Devices.Count, displayDevice.Name);
        if (_smartSetup && _activeTask?.DeviceId is { } taskDevice && _state.Devices.FirstOrDefault(d => SetupAssessment.SameDevice(d.Id, taskDevice)) is { } targetDevice)
            DeviceLabel.Text = targetDevice.Name;
        DeviceLabel.Visibility = string.IsNullOrEmpty(DeviceLabel.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (_state.Step is SetupStep.Connection or SetupStep.Devices) UpdateDeviceCount();
        DrawProgress();
    }
    private void Render()
    {
        if (_assessmentVisible) { RenderAssessment(); return; }
        Page.MaxWidth = 680; Scroller.VerticalContentAlignment = VerticalAlignment.Top;
        PageSymbol.Width = PageSymbol.Height = 30; PageSymbol.Margin = new(0, 0, 0, 10);
        PageSymbol.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MutedTextBrush");
        Description.Margin = new(0, 10, 0, 20); Description.MinHeight = 0; PromptPanel.Visibility = Visibility.Visible;
        RefreshLabels(); Body.Children.Clear(); PromptPanel.Children.Clear(); _actions.Clear(); _installationPanel = null;
        Status.Text = Details.Text = ""; Help.Visibility = Visibility.Collapsed;
        switch (_state.Step)
        {
            case SetupStep.Welcome:
                var welcome = Text("SetupWelcomeNote", 16);
                welcome.TextAlignment = TextAlignment.Center; welcome.MaxWidth = 440;
                welcome.LineHeight = 28; welcome.HorizontalAlignment = HorizontalAlignment.Center;
                welcome.Margin = new(0, 20, 0, 0); Body.Children.Add(welcome); break;
            case SetupStep.Preferences: Preferences(); break;
            case SetupStep.ApplicationMode: ApplicationModeChoices(); break;
            case SetupStep.Appearance: AppearanceChoices(); break;
            case SetupStep.Usage:
                UsageChoices(); break;
            case SetupStep.MirrorConnection: MirrorConnectionChoices(); break;
            case SetupStep.ControlIntroduction:
            case SetupStep.WirelessIntroduction:
            case SetupStep.BluetoothIntroduction:
                ControlIntroduction(); break;
            case SetupStep.Connection:
            case SetupStep.Devices:
                DeviceInventory();
                _verified = _previewOnly && _state.Devices.Count > 0;
                break;
            case SetupStep.Environment:
                _installationPanel = new StackPanel(); Body.Children.Add(_installationPanel);
                _installationPanel.Children.Add(Text("SetupInstallConsent"));
                Action("SetupInstall", () => RunAsync(true), _installationPanel);
                Action("SetupRetry", () => RunAsync(false)); break;
            case SetupStep.WiredControl:
                Body.Children.Add(Text("SetupCheckBridgeRepair"));
                Action("SetupRetry", () => AdvancePendingTasksAsync());
                break;
            case SetupStep.ControlReadiness:
                Body.Children.Add(Text("SetupCheckControlHint"));
                Action("SetupRetry", () => AdvancePendingTasksAsync());
                break;
            case SetupStep.Wireless:
            case SetupStep.Bluetooth:
                if (_state.Optional) Body.Children.Add(Text("SetupSkipHint" + _state.Step));
                _verified = HasSavedBinding();
                if (_verified) Status.Text = L("SetupBindingSaved");
                Action(_verified ? "SetupConfigureAgain" : "SetupConfigure", () => RunAsync(false)); break;
            case SetupStep.Display: DisplaySettings(); break;
            case SetupStep.WirelessBackend:
            case SetupStep.WirelessDisplay:
            case SetupStep.WiredDisplay:
            case SetupStep.WiredFrameRate:
            case SetupStep.WiredDecoder:
                DisplayChoices(); break;
            case SetupStep.DeviceComplete:
            case SetupStep.Completed:
                Summary(); _verified = _state.Step == SetupStep.DeviceComplete
                    ? _state.CurrentDevice is { } current && _state.DeviceComplete(current)
                    : true;
                if (_state.Step == SetupStep.Completed && _state.HasSavedDevices) Action("SetupLaterMain", () => FinishAsync(false));
                break;
        }
        if (_state.Step is SetupStep.ReDetection or SetupStep.Profile or SetupStep.Validation)
            Action("SetupRetry", () => RunAsync(false));
        if (_smartSetup && _activeTask?.Checks.FirstOrDefault(c => c.NeedsAttention) is { } check)
        {
            Status.Text = L("SetupState" + check.State);
            var reason = check.DetailKey is { } key ? L(key) : check.Detail;
            if (!string.IsNullOrWhiteSpace(reason)) Status.Text += " · " + reason;
        }
        Page.Margin = _state.Step == SetupStep.Welcome ? new Thickness(16, 64, 16, 12) : new Thickness(16, 6, 16, 12);
        Heading.FontSize = _state.Step == SetupStep.Welcome ? 32 : 28;
        UpdateNavigation();
    }
    private void DrawProgress()
    {
        var stages = Enum.GetValues<SetupCategory>();
        int active = Array.IndexOf(stages, _state.Category);
        StepIndicator.Children.Clear(); StepIndicator.ColumnDefinitions.Clear();
        for (var i = 0; i < stages.Length; i++)
        {
            if (i > 0)
            {
                StepIndicator.ColumnDefinitions.Add(new() { Width = new GridLength(12) });
                var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 17, 0, 0) };
                line.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
                Grid.SetColumn(line, i * 2 - 1); StepIndicator.Children.Add(line);
            }
            StepIndicator.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var panel = new StackPanel();
            var mark = new TextBlock { Text = i == active ? "●" : i < active ? "✓" : "○", FontSize = 18, TextAlignment = TextAlignment.Center };
            mark.SetResourceReference(TextBlock.ForegroundProperty, i == active ? "AccentBrush" : "MutedTextBrush"); panel.Children.Add(mark);
            panel.Children.Add(new TextBlock { Text = L("SetupStage" + stages[i]), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                FontWeight = i == active ? FontWeights.SemiBold : FontWeights.Normal, Margin = new(0, 4, 0, 0) });
            Grid.SetColumn(panel, i * 2); StepIndicator.Children.Add(panel);
        }
    }
    private void UpdateNavigation()
    {
        if (_assessmentVisible) { UpdateAssessmentNavigation(); return; }
        Next.Visibility = Visibility.Visible;
        Previous.IsEnabled = !_busy && !_advancing && _state.Step != SetupStep.Welcome;
        Previous.Visibility = _state.Step == SetupStep.Welcome ? Visibility.Hidden : Visibility.Visible;
        Skip.Visibility = _state.CanSkip ? Visibility.Visible : Visibility.Collapsed;
        Skip.IsEnabled = !_skipRequested;
        CancelOperation.Visibility = _busy || _discovery is not null ? Visibility.Visible : Visibility.Collapsed;
        CancelOperation.IsEnabled = !_skipRequested && (_busy ? _operation?.IsCancellationRequested == false : _discovery?.IsCancellationRequested == false);
        Skip.SetResourceReference(ContentProperty, _state.Step == SetupStep.Welcome ? "SetupDefer" : "SetupSkip");
        Next.SetResourceReference(ContentProperty, _state.Step == SetupStep.Welcome ? "SetupBegin" : _state.Step == SetupStep.Completed ? _state.HasSavedDevices ? "SetupStartMirror" : "SetupLaterMain" : "WizardNext");
        Next.IsEnabled = !_busy && !_advancing && (_verified || _state.IsChoiceStep);
        BusyBar.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        AnimateActivity();
        // Once a task starts, use this space for its inline device prompt.
        Body.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
        Body.IsEnabled = !_refreshingTask && !_selectingTask;
        DetailsExpander.Visibility = string.IsNullOrWhiteSpace(Details.Text) ? Visibility.Collapsed : Visibility.Visible;
        Status.Visibility = string.IsNullOrWhiteSpace(Status.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (_installationPanel is not null) _installationPanel.Visibility = _installNeeded ? Visibility.Visible : Visibility.Collapsed;
        foreach (var action in _actions) action.IsEnabled = !_busy && !_advancing &&
            (_state.Step is not (SetupStep.Connection or SetupStep.Devices) || _discovery is null || _discovery.IsCancellationRequested);
        if (_refreshingTask || _selectingTask)
        {
            Previous.IsEnabled = Next.IsEnabled = Skip.IsEnabled = false;
            foreach (var action in _actions) action.IsEnabled = false;
            if (_refreshingTask) { CancelOperation.Visibility = Visibility.Visible; CancelOperation.IsEnabled = _assessmentCancellation?.IsCancellationRequested == false; }
        }
    }
    private bool Save()
    {
        _state.WirelessBackendDraft = _main.SelectedWirelessReceiverBackend.Backend;
        _state.WirelessProfileDraft = _main.SelectedWirelessDisplayProfile.Id;
        try { _store.Save(_state); return true; }
        catch (Exception error) { Status.Text = L("SetupSaveFailed"); Status.Visibility = Visibility.Visible; Details.Text = error.Message; DetailsExpander.Visibility = Visibility.Visible; return false; }
    }
    private async void NextClick(object sender, RoutedEventArgs e)
    {
        if (!Next.IsEnabled) return;
        try { await AdvanceAsync(); }
        catch (Exception error) { if (!_closed) Failure(error); }
    }
    private async Task AdvanceAsync()
    {
        if (_assessmentVisible) { await ContinueAssessmentAsync(); return; }
        if (_advancing || _busy || _refreshingTask || _selectingTask || _closed) return;
        _advancing = true; UpdateNavigation();
        try
        {
            if (_state.Step == SetupStep.Completed) { await FinishAsync(true); return; }
            if (_state.Step is SetupStep.Connection or SetupStep.Devices)
            {
                // Accept the in-flight scan before committing the device queue.
                _acceptFinalDiscovery = true;
                await _discoveryTask;
                _acceptFinalDiscovery = false;
                if (_closed) return;
                if (_skipRequested) { _skipRequested = false; SkipCurrentStep(); return; }
                if (_advanceCancelled || !_verified) return;
            }
            if (_state.Step == SetupStep.WirelessBackend)
            {
                var originalStep = _state.Step;
                await RunOperationAsync(EnsureReceiverComponentAsync);
                if (_closed || !_verified || _state.Step != originalStep) return;
                if (!_main.SetupSaveWirelessPreferences()) throw new IOException(L("SetupSaveFailed"));
            }
            if (_state.IsChoiceStep && Application.Current is App app && !app.SaveUpdateSettings())
                throw new IOException(L("SetupSaveFailed"));
            _state.Disposition = SetupDisposition.InProgress;
            if (_state.Step == SetupStep.MirrorConnection) _state.GlobalOutcomes[SetupStep.Usage] = SetupOutcome.Verified;
            if (_state.Step == SetupStep.ControlIntroduction)
            {
                _state.WirelessEnabled ??= true; _state.BluetoothEnabled ??= true; _state.UsbControlEnabled ??= true;
            }
            if (_state.IsChoiceStep || _state.Step is SetupStep.Connection or SetupStep.Devices) _state.Record(SetupOutcome.Verified);
            Navigate();
        }
        catch (Exception error) { if (!_closed) Failure(error); }
        finally
        {
            _acceptFinalDiscovery = false; _advancing = false;
            if (!_closed)
            {
                if (!_assessmentVisible && !_refreshingTask && !_advanceCancelled && _state.Step is (SetupStep.Connection or SetupStep.Devices) && _discovery is null) StartDiscovery();
                UpdateNavigation();
            }
            _advanceCancelled = false;
        }
    }
    private void Navigate(bool backwards = false)
    {
        if (_smartSetup && backwards) { ShowAssessmentSummary(); return; }
        if (_smartSetup && !backwards && !ContinueSetupSubflow())
        {
            if (Save()) _ = AdvancePendingTasksAsync();
            return;
        }
        var checkpoint = System.Text.Json.JsonSerializer.Serialize(_state);
        if (backwards) _state.Previous(); else _state.Next();
        if (Save()) Enter(backwards ? -1 : 1);
        else _state = System.Text.Json.JsonSerializer.Deserialize<FirstRunSetupState>(checkpoint)!;
    }
    private void PreviousClick(object sender, RoutedEventArgs e)
    { if (_busy || _advancing || _refreshingTask || _selectingTask) return; Navigate(backwards: true); }
    private void SkipClick(object sender, RoutedEventArgs e)
    {
        if (_refreshingTask || _selectingTask) return;
        if (_assessmentVisible)
        {
            if (!_assessing)
            {
                var previous = _state.Disposition;
                if (previous == SetupDisposition.New) _state.Disposition = SetupDisposition.Deferred;
                if (Save()) { _allowClose = true; Close(); } else _state.Disposition = previous;
            }
            return;
        }
        if (!_state.CanSkip || _skipRequested || _closed) return;
        if (_busy)
        {
            _skipRequested = true;
            _operation?.Cancel(); Status.Text = L("SetupCancelling"); UpdateNavigation(); return;
        }
        if (_advancing)
        {
            _skipRequested = true; StopDiscovery();
            Status.Text = L("SetupCancelling"); UpdateNavigation(); return;
        }
        SkipCurrentStep();
    }
    private void CancelOperationClick(object sender, RoutedEventArgs e)
    {
        if (_assessmentVisible || _refreshingTask) { _assessmentCancellation?.Cancel(); return; }
        if (_busy) _operation?.Cancel();
        else { _advanceCancelled = _advancing; _verified = false; StopDiscovery(); }
        Status.Text = L(_busy ? "SetupCancelling" : "SetupCancelled");
        Status.Visibility = Visibility.Visible; UpdateNavigation();
    }
    private void SkipCurrentStep()
    {
        if (_smartSetup)
        {
            var snapshot = System.Text.Json.JsonSerializer.Serialize(_state);
            var taskKey = _activeTask?.Key ?? new SetupTask(_state.Step, CurrentTaskDeviceId(), []).Key;
            _state.Record(SetupOutcome.Skipped);
            if (Save()) { _deferredTasks.Add(taskKey); _ = AdvancePendingTasksAsync(skip: true); }
            else _state = System.Text.Json.JsonSerializer.Deserialize<FirstRunSetupState>(snapshot)!;
            return;
        }
        if (_state.Step == SetupStep.Welcome)
        {
            var previousDisposition = _state.Disposition;
            _state.Disposition = SetupDisposition.Deferred;
            if (Save()) { _allowClose = true; Close(); }
            else _state.Disposition = previousDisposition;
            return;
        }
        StopDiscovery();
        var checkpoint = System.Text.Json.JsonSerializer.Serialize(_state);
        _state.Skip();
        if (Save()) Enter();
        else
        {
            _state = System.Text.Json.JsonSerializer.Deserialize<FirstRunSetupState>(checkpoint)!;
            UpdateNavigation();
        }
    }
    private Task RunAsync(bool install) => RunOperationAsync(async token =>
    {
        if (install) _installNeeded = false;
        switch (_state.Step)
        {
            case SetupStep.Environment:
                var result = await PrepareDevice(Target, install,
                    new Progress<SetupDriverProgress>(p =>
                    { if (_closed || token.IsCancellationRequested || !_busy || _state.Step != SetupStep.Environment) return; Status.Text = L("SetupDriver" + p.Stage); Details.Text += Status.Text + "\n"; if (p.Detail is not null) Details.Text += p.Detail + "\n"; DetailsExpander.Visibility = Visibility.Visible; }), token);
                if (!result.Success && result.Stage == "Result" && !install)
                { _installNeeded = true; throw new InvalidOperationException(L("SetupInstallConsent")); }
                if (!result.Success && result.Stage != "Replug") throw new InvalidOperationException(L("SetupDriver" + result.Stage));
                _installNeeded = false;
                if (result.Stage == "Replug") Status.Text = L("SetupDriverReplug");
                break;
            case SetupStep.ReDetection:
                await PollAsync(async () => (await _main.SetupScanAsync(false, token)).Any(d =>
                    !string.IsNullOrWhiteSpace(d.Udid) && IPhoneFilterDriverService.NormalizeSerial(d.Udid) == IPhoneFilterDriverService.NormalizeSerial(Target)), token);
                var identified = _main.Devices.First(d => !d.IsWireless &&
                    IPhoneFilterDriverService.NormalizeSerial(d.Udid) == IPhoneFilterDriverService.NormalizeSerial(Target));
                _state.CurrentDevice!.Id = identified.Udid; _state.CurrentDevice.Name = identified.DisplayName;
                RefreshLabels(); break;
            case SetupStep.Profile:
                var device = (await _main.SetupScanAsync(false, token)).SingleOrDefault(d => DeviceViewModel.UdidEquals(d.Udid, Target))
                    ?? throw new InvalidOperationException(L("SetupDeviceLost"));
                var bindings = DeviceBindingManager.Shared;
                bindings.RefreshFromDisk();
                var profile = bindings.FindByIdentity(DeviceIdentityType.Wired, Target);
                if (profile is null)
                {
                    var created = bindings.CreateProfileFromIdentity(device.DisplayName, DeviceIdentityType.Wired, Target,
                        new(device.ProductType, device.ModelDisplay, SystemVersion: device.OsVersion));
                    if (!created.Success) throw new InvalidOperationException(created.Error);
                }
                if (new DeviceBindingManager().FindByIdentity(DeviceIdentityType.Wired, Target) is null) throw new IOException(L("SetupSaveFailed"));
                _main.SetupSelect(Target); break;
            case SetupStep.Wireless: await ConfigureWirelessAsync(token); break;
            case SetupStep.Bluetooth: await ConfigureBluetoothAsync(token); break;
            case SetupStep.Validation:
                if (!_main.SetupSaveVideoPreferences(_state.Devices.Select(d => d.Id))) throw new IOException(L("SetupSaveFailed"));
                if (_state.UsesWirelessMirroring) _state.WirelessRestarted |= await ApplyWirelessPreferences(token);
                if (_state.GlobalOutcomes.GetValueOrDefault(SetupStep.Display) != SetupOutcome.Skipped)
                    _state.GlobalOutcomes[SetupStep.Display] = SetupOutcome.Verified;
                await ValidateAsync(token); break;
        }
        token.ThrowIfCancellationRequested(); _state.Record(SetupOutcome.Verified);
        if (!Save()) { _state.Outcomes.Remove(_state.Step); throw new IOException(L("SetupSaveFailed")); }
    });
    private string Target => _state.CurrentDevice?.Id ?? throw new InvalidOperationException(L("SetupDeviceLost"));
    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy || _closed) return;
        _busy = true; _verified = false; Help.Visibility = Visibility.Collapsed; Details.Text = "";
        _state.Outcomes.Remove(_state.Step);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = cancellation;
        cancellation.CancelAfter(TimeSpan.FromMinutes(12));
        Status.Text = L("SetupWorking"); UpdateNavigation();
        try
        {
            // Keep ownership until the operation and its cleanup have actually ended.
            await operation(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_closed) { _verified = true; if (!Status.Text.Contains(L("SetupDriverReplug"), StringComparison.Ordinal)) Status.Text = L("SetupVerified"); }
        }
        catch (OperationCanceledException) { if (!_closed) Status.Text = L("SetupCancelled"); }
        catch (Exception error) { cancellation.Cancel(); if (!_closed) Failure(error); }
        finally
        {
            _busy = false; _operation = null;
            if (_closed) ReleaseSetupOwnership();
            else
            {
                PromptPanel.Children.Clear(); DownloadProgress.Visibility = Visibility.Collapsed;
                if (_skipRequested) { _skipRequested = false; SkipCurrentStep(); }
                UpdateNavigation();
            }
        }
    }
    private void ReleaseSetupOwnership()
    {
        if (_closed && !_busy && !_assessing && !_refreshingTask && !_selectingTask && _discovery is null) _main.SetupActive = false;
    }
    private async Task PollAsync(Func<Task<bool>> probe, CancellationToken token)
    {
        var timer = Stopwatch.StartNew(); var attempt = 0;
        while (timer.Elapsed < TimeSpan.FromSeconds(75))
        {
            token.ThrowIfCancellationRequested();
            Status.Text = LocalizationService.Format("SetupChecking", ++attempt, (int)(75 - timer.Elapsed.TotalSeconds));
            var pending = probe();
            bool found;
            try { found = await pending.WaitAsync(TimeSpan.FromSeconds(25), token); }
            catch
            {
                // Native probes cannot always be interrupted. Keep ownership
                // until they settle, so a late result cannot touch the next page.
                try { await pending; } catch { }
                throw;
            }
            token.ThrowIfCancellationRequested();
            if (found) return;
            await Task.Delay(1200, token);
        }
        throw new TimeoutException(L("SetupTimeout"));
    }
    private async Task ConfigureWirelessAsync(CancellationToken token)
    {
        await EnsureReceiverComponentAsync(token);
        if (!await _main.SetupReceiverAsync(token)) throw new InvalidOperationException(L("SetupAirPlayFailed"));
        IReadOnlyList<DeviceViewModel> devices = [];
        await PollAsync(async () => { devices = await _main.SetupScanAsync(true, token); return devices.Any(d => d.Ready); }, token);
        if (_state.Usage == SetupUsage.WirelessOnly)
        {
            foreach (var wireless in devices.Where(d => d.Ready))
            {
                token.ThrowIfCancellationRequested();
                var manager = DeviceBindingManager.Shared;
                manager.RefreshFromDisk();
                if (manager.FindByIdentity(DeviceIdentityType.AirPlay, wireless.Udid) is null)
                {
                    var created = manager.CreateProfileFromIdentity(wireless.DisplayName, DeviceIdentityType.AirPlay, wireless.Udid,
                        new(wireless.ProductType, wireless.ModelDisplay));
                    if (!created.Success) throw new InvalidOperationException(created.Error);
                }
                if (new DeviceBindingManager().FindByIdentity(DeviceIdentityType.AirPlay, wireless.Udid) is null) throw new IOException(L("SetupSaveFailed"));
                if (!_state.Devices.Any(d => d.Id == wireless.Udid)) _state.Devices.Add(new() { Id = wireless.Udid, Name = wireless.DisplayName });
                Details.Text += wireless.DisplayName + " · " + L("SetupVerified") + "\n";
            }
            return;
        }
        var id = await ChooseAsync(devices.Where(d => d.Ready).Select(d => (d.Udid, d.DisplayName)).ToArray(), token);
        devices = await _main.SetupScanAsync(true, token);
        var device = devices.FirstOrDefault(d => DeviceViewModel.UdidEquals(d.Udid, id) && d.Ready)
            ?? throw new InvalidOperationException(L("SetupDeviceLost"));
        var bindings = DeviceBindingManager.Shared;
        bindings.RefreshFromDisk();
        var profile = bindings.FindByIdentity(DeviceIdentityType.Wired, Target) ?? throw new InvalidOperationException(L("SetupDeviceLost"));
        var bound = bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, id, device.DisplayName,
            new(device.ProductType, device.ModelDisplay), userConfirmed: true);
        if (!bound.Success) throw new InvalidOperationException(bound.Error);
        if (new DeviceBindingManager().FindByIdentity(DeviceIdentityType.AirPlay, id) is null) throw new IOException(L("SetupSaveFailed"));
    }
    private async Task<string> ChooseAsync((string Id, string Name)[] choices, CancellationToken token)
    {
        _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PromptPanel.Children.Clear(); PromptPanel.Children.Add(Text("SetupConfirmIdentity"));
        var options = new List<UIElement>();
        foreach (var item in choices)
        {
            var card = ValueCard("identity", item.Name, null, false);
            card.Tag = Symbol("phone");
            card.Checked += (_, _) => _choice?.TrySetResult(item.Id); options.Add(card);
        }
        PromptPanel.Children.Add(Paginate(options, 3));
        try { return await _choice.Task.WaitAsync(TimeSpan.FromMinutes(2), token) ?? throw new OperationCanceledException(); }
        finally { _choice = null; PromptPanel.Children.Clear(); }
    }
    private async Task ConfigureBluetoothAsync(CancellationToken token)
    {
        var id = Target; _main.SetupSelect(id);
        try
        {
            // Own startup until completion so cancellation cannot leave advertising behind.
            var started = await _main.StartBluetoothPeripheralForConfigurationAsync(id);
            token.ThrowIfCancellationRequested();
            if (!started)
                throw new InvalidOperationException(L("SetupBluetoothFailed"));
            var savedId = DeviceBindingManager.Shared.FindByIdentity(DeviceIdentityType.Wired, id)?.BluetoothIdentity?.StableId;
            IReadOnlyList<BluetoothClientInfo> clients = [];
            await PollAsync(async () => { clients = await _main.GetReverseBluetoothClientsAsync(); return clients.Any(c => c.CanBind || c.Id == savedId); }, token);
            var chosen = clients.Any(c => c.Id == savedId) ? savedId! :
                await ChooseAsync(clients.Where(c => c.CanBind).Select(c => (c.Id, c.DisplayName)).ToArray(), token);
            clients = await _main.GetReverseBluetoothClientsAsync();
            token.ThrowIfCancellationRequested();
            var client = clients.FirstOrDefault(c => c.Id == chosen && (c.CanBind || c.Id == savedId));
            if (client is null)
                throw new InvalidOperationException(L("SetupBluetoothFailed"));
            var manager = DeviceBindingManager.Shared;
            manager.RefreshFromDisk();
            var profile = manager.FindByIdentity(DeviceIdentityType.Wired, id) ?? throw new IOException(L("SetupSaveFailed"));
            var result = manager.Bind(profile.Id, DeviceIdentityType.Bluetooth, chosen, client.DisplayName, null, userConfirmed: true);
            if (!result.Success) throw new IOException(result.Error);
            if (new DeviceBindingManager().FindByIdentity(DeviceIdentityType.Bluetooth, chosen) is null) throw new IOException(L("SetupSaveFailed"));
        }
        finally { await _main.StopBluetoothPeripheralConfigurationAsync(); }
    }
    private void Failure(Exception error)
    {
        Status.Text = _installNeeded ? L("SetupInstallConsent") : L("SetupFailed");
        Status.Visibility = Visibility.Visible;
        Details.Text += error.Message + "\n";
        DetailsExpander.Visibility = Visibility.Visible;
        HelpText.Text = L(_state.Step switch
        {
            SetupStep.Environment => "SetupHelpDriver", SetupStep.Wireless or SetupStep.WirelessBackend => "SetupHelpWireless",
            SetupStep.Bluetooth => "SetupHelpBluetooth", SetupStep.WiredControl or SetupStep.WirelessControl => "SetupHelpControl",
            _ => "SetupHelpDevice"
        });
        if (_state.Step == SetupStep.WirelessBackend)
            HelpText.Text = L(error switch
            {
                System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "UxPlayDownloadNotFound",
                InvalidDataException => "UxPlayDownloadIntegrityFailed",
                IOException or UnauthorizedAccessException => "UxPlayDownloadStorageFailed",
                _ => "SetupHelpWireless"
            });
        Help.Visibility = Visibility.Visible;
        DiagnosticLogger.Exception("setup", "step_failed", error);
    }
    private Task FinishAsync(bool start)
    {
        if (_fullSetup)
        {
            _fullSetup = false;
            return CheckSettingsAsync();
        }
        var previousDisposition = _state.Disposition;
        _state.Disposition = SetupDisposition.Completed;
        if (Save())
        {
            var savedIds = _state.Devices.Where(d => _state.Usage == SetupUsage.WirelessOnly ? _state.HasSavedDevices :
                d.Outcomes.GetValueOrDefault(SetupStep.Profile) == SetupOutcome.Verified).Select(d => d.Id).ToArray();
            var candidate = _main.Devices.FirstOrDefault(d => savedIds.Any(id => DeviceViewModel.UdidEquals(d.Udid, id)));
            if (candidate is not null) _main.SetupSelect(candidate.Udid);
            StartRequested = start && candidate is not null; _allowClose = true; Close();
        }
        else _state.Disposition = previousDisposition;
        return Task.CompletedTask;
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (Application.Current is App { IsSystemSessionEnding: true })
        { _state.Disposition = SetupDisposition.InProgress; Save(); _operation?.Cancel(); return; }
        e.Cancel = true; ExitPanel.Visibility = Visibility.Visible;
    }
    private void ContinueClick(object sender, RoutedEventArgs e) => ExitPanel.Visibility = Visibility.Collapsed;
    private void ExitClick(object sender, RoutedEventArgs e)
    {
        _state.Disposition = SetupDisposition.InProgress;
        if (!Save()) { ExitPanel.Visibility = Visibility.Collapsed; return; }
        _operation?.Cancel(); 
        _allowClose = true; Close();
    }
}
