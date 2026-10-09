using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Windows;

public partial class FirstRunSetupWindow
{
    private CancellationTokenSource? _discovery;
    private Task _discoveryTask = Task.CompletedTask;
    private bool _activityAnimating;
    internal Func<CancellationToken, Task<SetupDriverProgress>> DiscoverDevices { get; set; } = token =>
        FirstRunDriverClient.RunAsync("", false, new Progress<SetupDriverProgress>(), token, enumerate: true);
    internal Func<bool> ReceiverComponentAvailable { get; set; } = () =>
        new WirelessReceiverService().IsBackendAvailable(WirelessReceiverBackend.UxPlay);
    internal Func<IProgress<UpdateDownloadProgress>, Action, CancellationToken, Task> InstallReceiverComponent { get; set; } = UxPlayComponent.InstallAsync;
    internal Func<CancellationToken, Task<bool>> ApplyWirelessPreferences { get; set; }
    internal Func<DeviceBindingManager> ReadBindings { get; set; } = () => new();
    internal Func<string, bool, IProgress<SetupDriverProgress>, CancellationToken, Task<SetupDriverProgress>> PrepareDevice { get; set; } =
        (target, install, progress, token) => FirstRunDriverClient.RunAsync(target, install, progress, token);

    private void AnimatePage(int direction)
    {
        // Animate live text at its actual size; never stretch a screenshot.
        foreach (var element in new FrameworkElement[] { PageSymbol, Heading, Description, Body })
        {
            element.BeginAnimation(OpacityProperty, null);
            element.RenderTransform = Transform.Identity;
        }
        if (_previewOnly || !SystemParameters.ClientAreaAnimation) return;
        var welcome = !_assessmentVisible && _state.Step == SetupStep.Welcome;
        var elements = new FrameworkElement[] { PageSymbol, Heading, Description, Body };
        for (var i = 0; i < elements.Length; i++)
        {
            var element = elements[i];
            var delay = TimeSpan.FromMilliseconds(welcome ? 70 + i * 85 : i * 20);
            var duration = TimeSpan.FromMilliseconds(welcome ? 480 : 260);
            var transform = new TranslateTransform(); element.RenderTransform = transform;
            var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.Zero));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, delay));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, delay + duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            element.BeginAnimation(OpacityProperty, fade);
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(welcome ? 10 : direction * 6, 0, duration)
            { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }

    private void AnimateActivity()
    {
        if (_activityAnimating == _busy) return;
        _activityAnimating = _busy;
        for (var i = 0; i < BusyBar.Children.Count; i++)
        {
            var dot = (UIElement)BusyBar.Children[i];
            dot.BeginAnimation(OpacityProperty, null);
            if (_busy && !_previewOnly && SystemParameters.ClientAreaAnimation)
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(.25, .85, TimeSpan.FromMilliseconds(650))
                { BeginTime = TimeSpan.FromMilliseconds(i * 140), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    private void UsageChoices()
    {
        var row = CardRow();
        var mirror = ChoiceCard("purpose", "SetupMirrorOnly", "SetupMirrorOnlyHint", _state.Usage != SetupUsage.MirrorAndControl);
        mirror.Tag = Symbol("monitor");
        mirror.Checked += (_, _) => { _state.SelectUsage(SetupUsage.WirelessOnly); Save(); DrawProgress(); };
        var control = ChoiceCard("purpose", "SetupUsageMirrorAndControl", "SetupUsageHintMirrorAndControl", _state.Usage == SetupUsage.MirrorAndControl);
        control.Tag = Symbol("game");
        control.Checked += (_, _) => { _state.SelectUsage(SetupUsage.MirrorAndControl); Save(); DrawProgress(); };
        row.Children.Add(mirror); row.Children.Add(control); Body.Children.Add(row);
    }

    private void MirrorConnectionChoices()
    {
        var row = CardRow();
        foreach (var usage in new[] { SetupUsage.WirelessOnly, SetupUsage.WiredOnly })
        {
            var card = ChoiceCard("connection", "SetupUsage" + usage, "SetupUsageHint" + usage, _state.Usage == usage);
            card.Tag = Symbol(usage == SetupUsage.WirelessOnly ? "wireless" : "usb");
            card.Checked += (_, _) => { _state.SelectUsage(usage); Save(); DrawProgress(); };
            row.Children.Add(card);
        }
        Body.Children.Add(row);
    }

    private void ControlIntroduction()
    {
        var row = CardRow();
        foreach (var mode in new[] { "Usb", "Wireless", "Bluetooth" })
        {
            var panel = new StackPanel();
            var icon = new System.Windows.Shapes.Path { Data = Symbol(mode == "Usb" ? "usb" : mode == "Wireless" ? "wireless" : "bluetooth"),
                Width = 26, Height = 26, Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 16) };
            icon.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "TextBrush"); panel.Children.Add(icon);
            var title = Text("SetupControl" + mode, 16); title.FontWeight = FontWeights.SemiBold; panel.Children.Add(title);
            var enabled = new CheckBox { Margin = new(0, 0, 0, 8), IsChecked = mode switch {
                "Usb" => _state.UsbControlEnabled ?? true, "Wireless" => _state.WirelessEnabled ?? true, _ => _state.BluetoothEnabled ?? true } };
            enabled.SetResourceReference(ContentControl.ContentProperty, "SetupCheckUseFeature");
            void StoreChoice(object sender, RoutedEventArgs args)
            {
                if (mode == "Usb") _state.UsbControlEnabled = enabled.IsChecked == true;
                else if (mode == "Wireless") _state.WirelessEnabled = enabled.IsChecked == true;
                else _state.BluetoothEnabled = enabled.IsChecked == true;
                Save();
            }
            enabled.Checked += StoreChoice; enabled.Unchecked += StoreChoice; panel.Children.Add(enabled);
            var detail = Text("SetupControlHint" + mode, 13); detail.LineHeight = 20;
            detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); panel.Children.Add(detail);
            var border = new Border { Child = panel, Padding = new(18), MinHeight = 270, CornerRadius = new(14), BorderThickness = new(1), Margin = new(0, 0, 12, 12) };
            border.SetResourceReference(Border.BackgroundProperty, "CardBrush"); border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
            row.Children.Add(border);
        }
        Body.Children.Add(row);
        var note = Text("SetupControlRecommend", 13); note.TextAlignment = TextAlignment.Center; Body.Children.Add(note);
    }

    private void StopDiscovery() => _discovery?.Cancel();
    private void StartDiscovery()
    {
        _verified = false;
        Status.Text = L("SetupScanning"); Status.Visibility = Visibility.Visible;
        Details.Text = ""; DetailsExpander.Visibility = Visibility.Collapsed; Help.Visibility = Visibility.Collapsed;
        var prior = _discoveryTask;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _discovery = cancellation;
        UpdateNavigation();
        _discoveryTask = DiscoverAsync(prior, cancellation);
    }
    private async Task DiscoverAsync(Task prior, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await prior;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Complete the current read before releasing the helper for installation.
                    var scan = await DiscoverDevices(token);
                    if (_closed || token.IsCancellationRequested && (!_acceptFinalDiscovery || _advanceCancelled || _skipRequested)) break;
                    if (!scan.Success || scan.Devices is null) throw new IOException(L("SetupDriverFailed"));
                    ApplyDiscovery(scan.Devices);
                    Details.Text = ""; DetailsExpander.Visibility = Visibility.Collapsed;
                    Status.Text = L("SetupScanning"); Status.Visibility = Visibility.Visible;
                    Help.Visibility = Visibility.Collapsed;
                }
                catch (Exception error) when (error is not OperationCanceledException ||
                    !token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    if (_closed || token.IsCancellationRequested && (!_acceptFinalDiscovery || _advanceCancelled || _skipRequested)) break;
                    _verified = false;
                    Details.Text = "";
                    Failure(error is OperationCanceledException ? new TimeoutException(L("SetupTimeout"), error) : error);
                    UpdateNavigation();
                }
                if (_acceptFinalDiscovery) break;
                await Task.Delay(1500, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_discovery, cancellation)) _discovery = null;
            cancellation.Dispose();
            ReleaseSetupOwnership();
            if (!_closed) UpdateNavigation();
        }
    }
    private void ApplyDiscovery(IReadOnlyList<SetupUsbDevice> devices)
    {
        var before = string.Join("|", _state.Devices.Select(d => d.Id + d.Name));
        _state.ReconcileConnectedDevices(devices);
        var changed = before != string.Join("|", _state.Devices.Select(d => d.Id + d.Name));
        _verified = devices.Count > 0;
        if (changed) { Body.Children.Clear(); _actions.Clear(); DeviceInventory(); Save(); }
        UpdateDeviceCount(); UpdateNavigation();
    }
    private void UpdateDeviceCount()
    {
        DeviceLabel.Text = LocalizationService.Format("SetupConnectedCount", _state.Devices.Count);
        DeviceLabel.Visibility = Visibility.Visible;
    }

    private async Task EnsureReceiverComponentAsync(CancellationToken token)
    {
        if (_main.SelectedWirelessReceiverBackend.Backend != WirelessReceiverBackend.UxPlay ||
            await Task.Run(ReceiverComponentAvailable, token)) return;
        var installing = false;
        Status.Text = L("PreparingDownload");
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            if (_closed || token.IsCancellationRequested || !_busy || installing) return;
            if (value.Phase == UpdateDownloadPhase.Download)
            {
                DownloadProgress.Visibility = value.Percentage.HasValue ? Visibility.Visible : Visibility.Collapsed;
                DownloadProgress.Value = value.Percentage ?? 0;
                Status.Text = (value.Percentage is { } percentage ? LocalizationService.Format("DownloadProgressFormat", percentage) : L("PreparingDownload")) +
                    $"\n{value.BytesReceived / 1_000_000.0:F1} / {value.TotalBytes.GetValueOrDefault() / 1_000_000.0:F1} MB · {value.BytesPerSecond / 1_000_000.0:F1} MB/s";
            }
            else
            {
                DownloadProgress.Visibility = Visibility.Collapsed;
                Status.Text = L(value.Phase == UpdateDownloadPhase.Verification ? "VerifyingDownload" : "UxPlayDownloadFindingMirror");
            }
        });
        await InstallReceiverComponent(progress, () =>
        {
            installing = true;
            if (_closed || token.IsCancellationRequested) return;
            DownloadProgress.Visibility = Visibility.Collapsed; Status.Text = L("UxPlayDownloadInstalling");
        }, token);
        token.ThrowIfCancellationRequested();
        if (!await Task.Run(ReceiverComponentAvailable, token))
            throw new IOException(L("UxPlayDownloadFailed"));
    }

    private bool HasSavedBinding()
    {
        if (!_smartSetup && _state.Outcomes.GetValueOrDefault(_state.Step) != SetupOutcome.Verified) return false;
        if (_smartSetup && _activeTask?.Checks.Any(c => c.NeedsAttention) == true) return false;
        var saved = ReadBindings();
        if (_state.Usage == SetupUsage.WirelessOnly)
            return _state.Step == SetupStep.Wireless && _state.Devices.Count > 0 && _state.Devices.All(d =>
                saved.FindByIdentity(DeviceIdentityType.AirPlay, d.Id) is not null);
        var profile = _state.CurrentDevice is { } device ? saved.FindByIdentity(DeviceIdentityType.Wired, device.Id) : null;
        return _state.Step == SetupStep.Wireless ? profile?.AirPlayIdentity is not null : profile?.BluetoothIdentity is not null;
    }

    private Task ValidateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_state.HasSavedDevices) return Task.CompletedTask;
        var saved = ReadBindings();
        foreach (var device in _state.Devices)
        {
            token.ThrowIfCancellationRequested();
            if (_state.Usage != SetupUsage.WirelessOnly && device.Outcomes.GetValueOrDefault(SetupStep.Profile) != SetupOutcome.Verified) continue;
            var profile = saved.FindByIdentity(_state.Usage == SetupUsage.WirelessOnly ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired, device.Id);
            if (profile is null)
                throw new IOException(L("SetupSaveFailed"));
            if (device.Outcomes.GetValueOrDefault(SetupStep.Wireless) == SetupOutcome.Verified && profile.AirPlayIdentity is null ||
                device.Outcomes.GetValueOrDefault(SetupStep.Bluetooth) == SetupOutcome.Verified && profile.BluetoothIdentity is null)
                throw new IOException(L("SetupSaveFailed"));
        }
        return Task.CompletedTask;
    }
}
