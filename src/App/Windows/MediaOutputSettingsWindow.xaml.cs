using IPhoneMirror.UI.Controls;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using Microsoft.Win32;

namespace IPhoneMirror.App.Windows;

public partial class MediaOutputSettingsWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly MainViewModel _viewModel;
    private readonly bool _previewOnly;
    private bool _savePromptOpen;
    private bool _discardPromptOpen;
    private string? _lastPromptedRecordingPath;
    private (uint Width, uint Height)? _lastDefaultSize;
    private (uint Width, uint Height)? _lastDefaultCameraSize;
    private readonly CancellationTokenSource _microphoneCancellation = new();
    private bool _microphoneRefreshRunning;

    internal MediaOutputSettingsWindow(MainViewModel viewModel, bool previewOnly = false)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _previewOnly = previewOnly;
        MicrophoneBox.ItemsSource = new[] { new MicrophoneDevice(LocalizationService.Get("MicrophoneOff"), "") };
        MicrophoneBox.SelectedIndex = 0;
        DataContext = viewModel;
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
        {
            FeedbackText.Text = LocalizationService.RefreshText(FeedbackText.Text);
            if (MicrophoneBox.ItemsSource is IEnumerable<MicrophoneDevice> microphones)
            {
                var selected = MicrophoneBox.SelectedValue as string;
                MicrophoneBox.ItemsSource = microphones.Select(device => device.Id.Length == 0
                    ? device with { Name = LocalizationService.Get("MicrophoneOff") } : device).ToArray();
                MicrophoneBox.SelectedValue = selected;
            }
            if (VirtualCameraCurrentResolutionItem.Content is string caption)
                VirtualCameraCurrentResolutionItem.Content = LocalizationService.RefreshText(caption);
            UpdateStartButtons();
        });
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyPreviewOutputDefaults(force: true);
        RecordFpsBox.Text = StreamFpsBox.Text = "30";
        RecordBitrateBox.Text = StreamBitrateBox.Text = "6000";
        VirtualCameraFpsBox.SelectedIndex = 0;
        if (_previewOnly)
        {
            SetFeedback(LocalizationService.Get("DeveloperReadOnlyPreview"));
            UpdateStartButtons();
            return;
        }
        // Re-probe on each opening so a user can install FFmpeg or update PATH
        // without restarting the entire application.
        await RunAsync(() => _viewModel.EnsureMediaOutputCapabilitiesAsync(force: true));
        if (_microphoneCancellation.IsCancellationRequested) return;
        SelectFirstSupportedProtocol();
        UpdateStartButtons();
        await RefreshMicrophonesAsync();
        if (_microphoneCancellation.IsCancellationRequested) return;
        if (!_viewModel.IsRecordingMediaOutput &&
            _viewModel.PendingRecordingPath is not null)
            await PromptToSaveRecordingAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Loaded -= OnLoaded;
        Closing -= OnClosing;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _microphoneCancellation.Cancel();
        _microphoneCancellation.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.MediaOutputStatus) or nameof(MainViewModel.MediaOutputTone) &&
            _viewModel.MediaOutputTone == "Failed")
            SetFeedback(_viewModel.MediaOutputStatus, StatusTone.Error);
        if (e.PropertyName is nameof(MainViewModel.CanStartMediaOutput) or
            nameof(MainViewModel.CanRecordMediaOutput) or
            nameof(MainViewModel.CanStartRecording) or
            nameof(MainViewModel.CanStartStreaming) or
            nameof(MainViewModel.CanStreamRtmp) or
            nameof(MainViewModel.CanStreamSrt) or
            nameof(MainViewModel.CanStreamWhip) or
            nameof(MainViewModel.CanUseVirtualCamera) or
            nameof(MainViewModel.CanInstallVirtualCamera) or
            nameof(MainViewModel.CanUninstallVirtualCamera) or
            nameof(MainViewModel.CanStopMediaOutput) or
            nameof(MainViewModel.IsRecordingMediaOutput) or
            nameof(MainViewModel.IsStreamingMediaOutput) or
            nameof(MainViewModel.IsVirtualCameraOutputRunning) or
            nameof(MainViewModel.CanDiscardRecording))
            UpdateStartButtons();
        if (e.PropertyName == nameof(MainViewModel.SourceVideoHeight) &&
            !_viewModel.IsMediaOutputRunning)
            ApplyPreviewOutputDefaults(force: false);
        if (e.PropertyName == nameof(MainViewModel.IsRecordingMediaOutput) &&
            !_viewModel.IsRecordingMediaOutput &&
            _viewModel.PendingRecordingPath is not null)
            _ = Dispatcher.BeginInvoke(async () =>
                await PromptToSaveRecordingAsync());
    }

    private void ApplyPreviewOutputDefaults(bool force)
    {
        if (!IsLoaded && !force) return;
        var size = _viewModel.SuggestedMediaOutputSize();
        var cameraSize = _viewModel.SuggestedVirtualCameraSize();
        var previous = _lastDefaultSize;
        var updateRecording = force || previous is null ||
            (RecordWidthBox.Text == previous.Value.Width.ToString() &&
             RecordHeightBox.Text == previous.Value.Height.ToString());
        var updateStreaming = force || previous is null ||
            (StreamWidthBox.Text == previous.Value.Width.ToString() &&
             StreamHeightBox.Text == previous.Value.Height.ToString());
        var previousResolution = _lastDefaultCameraSize is { } previousCamera
            ? $"{previousCamera.Width}x{previousCamera.Height}" : null;
        var selectedResolution =
            (VirtualCameraResolutionBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var updateVirtualCamera = force || previous is null ||
            string.Equals(selectedResolution, previousResolution,
                StringComparison.OrdinalIgnoreCase);

        if (updateRecording)
        {
            RecordWidthBox.Text = size.Width.ToString();
            RecordHeightBox.Text = size.Height.ToString();
        }
        if (updateStreaming)
        {
            StreamWidthBox.Text = size.Width.ToString();
            StreamHeightBox.Text = size.Height.ToString();
        }
        SelectVirtualCameraResolution(cameraSize.Width, cameraSize.Height,
            updateVirtualCamera);
        _lastDefaultSize = size;
        _lastDefaultCameraSize = cameraSize;
    }

    private void SelectVirtualCameraResolution(uint width, uint height,
        bool updateSelection)
    {
        var value = $"{width}x{height}";
        var preset = VirtualCameraResolutionBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => !ReferenceEquals(
                    item, VirtualCameraCurrentResolutionItem) &&
                string.Equals(item.Tag as string, value,
                    StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
        {
            VirtualCameraCurrentResolutionItem.Visibility = Visibility.Collapsed;
            if (updateSelection) VirtualCameraResolutionBox.SelectedItem = preset;
            return;
        }

        VirtualCameraCurrentResolutionItem.Tag = value;
        VirtualCameraCurrentResolutionItem.Content = LocalizationService.Format(
            "CurrentPreviewResolutionFormat", width, height);
        VirtualCameraCurrentResolutionItem.Visibility = Visibility.Visible;
        if (updateSelection)
            VirtualCameraResolutionBox.SelectedItem =
                VirtualCameraCurrentResolutionItem;
    }

    private void OnStreamProtocolChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateStartButtons();

    private void SelectFirstSupportedProtocol()
    {
        if (SelectedProtocolSupported()) return;
        StreamProtocolBox.SelectedItem = new[]
        {
            RtmpProtocolItem,
            SrtProtocolItem,
            WhipProtocolItem,
        }.FirstOrDefault(item => item.IsEnabled);
    }

    private void UpdateStartButtons()
    {
        if (!IsLoaded) return;
        var pendingRecording = _viewModel.CanDiscardRecording;
        StartRecordingButton.SetResourceReference(ContentControl.ContentProperty,
            _viewModel.IsRecordingMediaOutput ? "StopRecordingOnly" : pendingRecording ? "RecordSaveTitle" : "StartRecording");
        StartStreamingButton.SetResourceReference(ContentControl.ContentProperty,
            _viewModel.IsStreamingMediaOutput ? "StopStreamingOnly" : "StartStreaming");
        StartVirtualCameraButton.SetResourceReference(ContentControl.ContentProperty,
            _viewModel.IsVirtualCameraOutputRunning ? "StopVirtualCamera" : "StartVirtualCamera");
        StartRecordingButton.IsEnabled = !_previewOnly && (_viewModel.IsRecordingMediaOutput
            ? _viewModel.CanStopRecording : pendingRecording || _viewModel.CanStartRecording && _viewModel.CanRecordMediaOutput);
        StartStreamingButton.IsEnabled = !_previewOnly && (_viewModel.IsStreamingMediaOutput
            ? _viewModel.CanStopStreaming : _viewModel.CanStartStreaming && SelectedProtocolSupported());
        StartVirtualCameraButton.IsEnabled = !_previewOnly && (_viewModel.IsVirtualCameraOutputRunning
            ? _viewModel.CanStopVirtualCamera : _viewModel.CanUseVirtualCamera);
        StopAllOutputsButton.IsEnabled = !_previewOnly && _viewModel.CanStopMediaOutput;
        DiscardRecordingButton.Visibility = pendingRecording ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool SelectedProtocolSupported() => StreamProtocolBox.SelectedIndex switch
    {
        0 => _viewModel.CanStreamRtmp,
        1 => _viewModel.CanStreamSrt,
        2 => _viewModel.CanStreamWhip,
        _ => false,
    };

    private async void OnStartRecordingClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || _viewModel.IsMediaOutputTransitioning) return;
        if (_viewModel.IsRecordingMediaOutput)
        {
            await RunAsync(() => _viewModel.StopRecordingAsync());
            await PromptToSaveRecordingAsync();
            return;
        }
        if (_viewModel.PendingRecordingPath is not null)
        {
            await PromptToSaveRecordingAsync(force: true);
            return;
        }
        if (!TryReadOutputSettings(RecordWidthBox, RecordHeightBox, RecordFpsBox,
                RecordBitrateBox, out var width, out var height, out var fps, out var bitrate))
            return;
        var result = await _viewModel.StartRecordingAsync(
            width, height, fps, bitrate);
        SetFeedback(result.Message, result.Success ? StatusTone.Success : StatusTone.Error);
    }

    private async void OnStartStreamingClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || _viewModel.IsMediaOutputTransitioning) return;
        if (_viewModel.IsStreamingMediaOutput)
        {
            await RunAsync(() => _viewModel.StopStreamingAsync());
            return;
        }
        if (!TryReadOutputSettings(StreamWidthBox, StreamHeightBox, StreamFpsBox,
                StreamBitrateBox, out var width, out var height, out var fps, out var bitrate))
            return;
        var kind = StreamProtocolBox.SelectedIndex switch
        {
            1 => MediaOutputKind.Srt,
            2 => MediaOutputKind.Whip,
            _ => MediaOutputKind.Rtmp,
        };
        var result = await _viewModel.StartStreamingAsync(kind,
            StreamDestinationBox.Text.Trim(), StreamAuthorizationBox.Text.Trim(),
            width, height, fps, bitrate, MicrophoneBox.SelectedValue as string);
        SetFeedback(result.Message, result.Success ? StatusTone.Success : StatusTone.Error);
    }

    private async void OnRefreshMicrophonesClick(object sender, RoutedEventArgs e) =>
        await RefreshMicrophonesAsync();

    private async Task RefreshMicrophonesAsync()
    {
        if (_previewOnly || _microphoneRefreshRunning || _microphoneCancellation.IsCancellationRequested) return;
        _microphoneRefreshRunning = true;
        RefreshMicrophonesButton.IsEnabled = false;
        var selected = MicrophoneBox.SelectedValue as string ?? "";
        try
        {
            var devices = await _viewModel.GetMicrophonesAsync(_microphoneCancellation.Token);
            if (_microphoneCancellation.IsCancellationRequested) return;
            MicrophoneBox.ItemsSource = new[] { new MicrophoneDevice(LocalizationService.Get("MicrophoneOff"), "") }
                .Concat(devices).ToArray();
            MicrophoneBox.SelectedValue = devices.Any(device => device.Id == selected) ? selected : "";
            if (devices.Count == 0) SetFeedback(LocalizationService.Get("MicrophoneNotFound"), StatusTone.Warning);
        }
        catch (OperationCanceledException) when (_microphoneCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("media_output", "microphone_refresh_failed", error);
            SetFeedback(LocalizationService.ErrorMessage(error, "MicrophoneRefreshFailed"), StatusTone.Error);
        }
        finally
        {
            _microphoneRefreshRunning = false;
            if (!_microphoneCancellation.IsCancellationRequested)
                RefreshMicrophonesButton.IsEnabled = true;
        }
    }

    private async void OnStartVirtualCameraClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || _viewModel.IsMediaOutputTransitioning) return;
        if (_viewModel.IsVirtualCameraOutputRunning)
        {
            await RunAsync(() => _viewModel.StopVirtualCameraAsync());
            return;
        }
        if (VirtualCameraResolutionBox.SelectedItem is not ComboBoxItem
                { Tag: string resolution } ||
            VirtualCameraFpsBox.SelectedItem is not ComboBoxItem
                { Tag: string frameRateText } ||
            !TryParseResolution(resolution, out var width, out var height) ||
            !int.TryParse(frameRateText, out var frameRate))
        {
            SetFeedback(LocalizationService.Get("MediaOutputInvalidSettings"), StatusTone.Warning);
            return;
        }
        var result = await _viewModel.StartVirtualCameraAsync(
            width, height, frameRate);
        SetFeedback(result.Message, result.Success ? StatusTone.Success : StatusTone.Error);
    }

    private static bool TryParseResolution(string value,
        out uint width, out uint height)
    {
        width = height = 0;
        var parts = value.Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && uint.TryParse(parts[0], out width) &&
            uint.TryParse(parts[1], out height);
    }

    private async void OnInstallVirtualCameraClick(object sender, RoutedEventArgs e)
    {
        var result = await _viewModel.InstallVirtualCameraAsync();
        SetFeedback(result.Message, result.Success ? StatusTone.Success : StatusTone.Error);
    }

    private async void OnUninstallVirtualCameraClick(object sender, RoutedEventArgs e)
    {
        var result = await _viewModel.UninstallVirtualCameraAsync();
        SetFeedback(result.Message, result.Success ? StatusTone.Success : StatusTone.Error);
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PendingRecordingPath is null || _savePromptOpen || _discardPromptOpen) return;
        _discardPromptOpen = true;
        try
        {
            if (!AppPromptWindow.ConfirmDestructive(LocalizationService.Get("DiscardRecording"),
                    LocalizationService.Get("DiscardRecordingConfirmation"),
                    LocalizationService.Get("DiscardRecording"), this))
                return;
            var discarded = _viewModel.DiscardPendingRecording();
            SetFeedback(LocalizationService.Get(discarded ? "RecordingDiscarded" : "RecordingDiscardFailed"),
                discarded ? StatusTone.Success : StatusTone.Error);
        }
        finally { _discardPromptOpen = false; }
    }

    private async void OnStopAllClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || !_viewModel.CanStopMediaOutput) return;
        await RunAsync(_viewModel.StopMediaOutputAsync);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task PromptToSaveRecordingAsync(bool force = false)
    {
        if (_savePromptOpen || _discardPromptOpen || !IsLoaded || _viewModel.IsRecordingMediaOutput) return;
        var pending = _viewModel.PendingRecordingPath;
        if (pending is null) return;
        if (!force && string.Equals(pending, _lastPromptedRecordingPath,
                StringComparison.OrdinalIgnoreCase)) return;
        _lastPromptedRecordingPath = pending;
        _savePromptOpen = true;
        try
        {
            var suggestedName = Path.GetFileName(pending);
            var suffix = suggestedName.LastIndexOf('_');
            if (suffix > 0)
                suggestedName = suggestedName[..suffix] + ".mp4";
            var dialog = new SaveFileDialog
            {
                Title = LocalizationService.Get("RecordSaveTitle"),
                Filter = LocalizationService.Get("RecordMp4Filter"),
                DefaultExt = ".mp4",
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyVideos),
                FileName = suggestedName,
            };
            if (dialog.ShowDialog(this) != true)
            {
                SetFeedback(LocalizationService.Get("RecordingPendingSave"), StatusTone.Warning);
                return;
            }

            try
            {
                try { File.Move(pending, dialog.FileName, overwrite: true); }
                catch (IOException)
                {
                    await PendingRecordingStore.CopyAndDeleteAsync(pending, dialog.FileName);
                }
                _viewModel.MarkPendingRecordingSaved(pending);
                SetFeedback(LocalizationService.Format(
                    "RecordingSavedFormat", dialog.FileName), StatusTone.Success);
            }
            catch (Exception error)
            {
                DiagnosticLogger.Exception("recording", "save_failed", error,
                    ("file", Path.GetFileName(dialog.FileName)));
                SetFeedback(LocalizationService.Get("RecordingSaveRetry"), StatusTone.Error);
            }
        }
        finally { _savePromptOpen = false; }
    }

    private void SetFeedback(string message, StatusTone tone = StatusTone.Neutral)
    {
        FeedbackText.Text = message;
        StatusAppearance.SetTone(FeedbackText, tone);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("media_output", "settings_action_failed", error);
            SetFeedback(LocalizationService.ErrorMessage(error, "MediaOutputOperationFailed"), StatusTone.Error);
        }
    }

    private bool TryReadOutputSettings(TextBox widthBox, TextBox heightBox,
        TextBox fpsBox, TextBox bitrateBox, out uint width, out uint height,
        out int fps, out int bitrate)
    {
        width = height = 0;
        fps = bitrate = 0;
        if (uint.TryParse(widthBox.Text, out width) &&
            uint.TryParse(heightBox.Text, out height) &&
            int.TryParse(fpsBox.Text, out fps) &&
            int.TryParse(bitrateBox.Text, out bitrate) &&
            width is >= 160 and <= 3840 && height is >= 160 and <= 2160 &&
            (width & 1U) == 0 && (height & 1U) == 0 && fps is >= 10 and <= 60 &&
            bitrate is >= 500 and <= 50000)
            return true;
        SetFeedback(LocalizationService.Get("MediaOutputInvalidSettings"), StatusTone.Warning);
        return false;
    }
}
