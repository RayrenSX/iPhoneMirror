using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class FirstRunSetupWindow
{
    private bool _toolActive;
    private CancellationTokenSource? _toolCancellation;
    internal Func<CancellationToken, Task<DriverManagerLaunchResult>> OpenDriverToolAsync { get; set; } =
        token => new DriverManagerLauncher().LaunchAndWaitAsync(token);
    private string? _bindingToolIdentity;
    internal Func<string?, CancellationToken, Task<string?>>? OpenBindingToolAsync { get; set; }
    private static bool IsToolStep(SetupStep step) => step is SetupStep.Environment or
        SetupStep.Profile or SetupStep.Wireless or SetupStep.Bluetooth;

    private void RenderToolHandoff(bool driver)
    {
        if (!driver)
        {
            _verified = HasSavedBinding();
            if (_verified) Status.Text = L("SetupBindingSaved");
        }
        Body.Children.Add(CreateToolCard(driver, () => RunAsync(false)));
        var hint = Text("SetupToolsReturnHint", 13);
        hint.TextAlignment = TextAlignment.Center;
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Body.Children.Add(hint);
    }

    private Button CreateToolCard(bool driver, Func<Task> open)
    {
        var content = new StackPanel();
        var icon = new System.Windows.Shapes.Path { Data = Symbol(driver ? "settings" : "link"),
            Width = 28, Height = 28, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 16) };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        content.Children.Add(icon);
        var title = Text(driver ? "DriverManagerTitle" : "DeviceBindingTitle", 18);
        title.FontWeight = FontWeights.SemiBold; title.Margin = new(0, 0, 0, 8);
        content.Children.Add(title);
        var detail = Text(driver ? "SetupToolsDriverHint" : "SetupToolsBindingHint", 13);
        detail.SetResourceReference(TextBlock.LineHeightProperty, "ReleaseNotesLineHeight"); detail.Margin = new(0, 0, 0, 18);
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        content.Children.Add(detail);
        var action = Text("SetupToolsOpen", 13); action.Margin = new(0);
        action.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        content.Children.Add(action);
        var button = Action(driver ? "DriverManagerTitle" : "DeviceBindingTitle", open);
        Body.Children.Remove(button);
        button.Content = content; button.Margin = new(0, 0, 12, 0);
        button.SetResourceReference(StyleProperty, "SetupToolCard");
        System.Windows.Automation.AutomationProperties.SetName(button, L(driver ? "DriverManagerTitle" : "DeviceBindingTitle"));
        return button;
    }

    private async Task OpenStandaloneToolAsync(bool driver, CancellationToken token)
    {
        StopDiscovery();
        await _discoveryTask;
        token.ThrowIfCancellationRequested();
        _bindingToolIdentity = null;
        _toolActive = true;
        Status.Text = L(driver ? "SetupToolsDriverWaiting" : "SetupToolsBindingWaiting");
        UpdateNavigation();
        try
        {
            if (driver)
            {
                // Waiting for an external tool needs no progress animation.
                StopAssessmentAnimations();
                DiagnosticLogger.Info("setup", "driver_handoff_started",
                    ("layered_window", AllowsTransparency));
                var result = await OpenDriverToolAsync(token);
                if (!result.Success) throw new IOException(result.Message);
            }
            else if (OpenBindingToolAsync is { } open) _bindingToolIdentity = await open(_state.CurrentDevice?.Id, token);
            else
            {
                token.ThrowIfCancellationRequested();
                var window = new DeviceBindingWindow(this, _main.Devices, _main, previewOnly: _previewOnly);
                window.SelectIdentity(_state.CurrentDevice?.Id);
                window.ShowDialog();
                _bindingToolIdentity = window.AirPlayIdentity;
                await window.CleanupTask;
            }
            token.ThrowIfCancellationRequested();
            if (!_closed) Status.Text = L("SetupToolsChecking");
        }
        finally
        {
            _toolActive = false;
            if (driver)
                DiagnosticLogger.Info("setup", "driver_handoff_finished",
                    ("cancelled", token.IsCancellationRequested), ("closed", _closed));
            if (!_closed)
            {
                AnimateActivity();
                Activate();
            }
        }
    }

    private async Task VerifyStandaloneToolAsync(bool driver, CancellationToken token)
    {
        if (driver)
        {
            var result = _state.CurrentDevice is { } target
                ? await PrepareDevice(target.Id, false, new Progress<SetupDriverProgress>(), token)
                : await CheckRuntime.DriverAsync(token);
            if (!result.Success || result.Stage == "Replug" || result.Environment is { } environment &&
                (!environment.AppleReady || !environment.UsbInstalled || !environment.UsbFilesMatch))
                throw new InvalidOperationException(L(result.Stage == "Replug" ? "SetupDriverReplug" : "SetupToolsIncomplete"));
            return;
        }
        var saved = await CheckRuntime.ReadBindingsAsync(token);
        token.ThrowIfCancellationRequested();
        if (saved.LoadError is not null) throw new IOException(L("SetupCheckBindingsUnreadable"), saved.LoadError);
        if (_state.Usage == SetupUsage.WirelessOnly)
        {
            // Adopt only the profile explicitly selected in the binder, including an existing profile.
            var profile = string.IsNullOrWhiteSpace(_bindingToolIdentity) ? null :
                saved.FindByIdentity(DeviceIdentityType.AirPlay, _bindingToolIdentity);
            if (profile?.AirPlayIdentity is not { } identity)
                throw new InvalidOperationException(L("SetupToolsIncomplete"));
            var existing = _state.Devices.FirstOrDefault(d => SetupAssessment.SameDevice(d.Id, identity.StableId));
            _state.Devices = [existing ?? new() { Id = identity.StableId, Name = profile.DisplayName }];
            _state.DeviceIndex = 0;
        }
        else
        {
            var profile = saved.FindByIdentity(DeviceIdentityType.Wired, Target);
            if (profile is null || _state.Step == SetupStep.Wireless && profile.AirPlayIdentity is null ||
                _state.Step == SetupStep.Bluetooth && profile.BluetoothIdentity is null)
                throw new InvalidOperationException(L("SetupToolsIncomplete"));
            if (_state.Step == SetupStep.Profile) RecordOptionalBindings(profile);
        }
    }

    private void RecordOptionalBindings(DeviceBindingProfile profile)
    {
        if (_state.Usage != SetupUsage.MirrorAndControl || _state.CurrentDevice is not { } device) return;
        // The binder handles all identities in one visit. Optional identities can be added later.
        device.Outcomes[SetupStep.Wireless] = profile.AirPlayIdentity is not null ? SetupOutcome.Verified : SetupOutcome.Skipped;
        device.Outcomes[SetupStep.Bluetooth] = profile.BluetoothIdentity is not null ? SetupOutcome.Verified : SetupOutcome.Skipped;
    }

    private async Task OpenToolFromSummaryAsync(bool driver)
    {
        if (_assessing || _selectingTask || _closed) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _toolCancellation = cancellation;
        _selectingTask = true; UpdateNavigation();
        try { await OpenStandaloneToolAsync(driver, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_closed) Status.Text = L("SetupCancelled"); return; }
        catch (Exception error) { if (!_closed) Failure(error); return; }
        finally { _toolCancellation = null; _selectingTask = false; ReleaseSetupOwnership(); if (!_closed) UpdateNavigation(); }
        if (!_closed) await CheckSettingsAsync();
    }
}
