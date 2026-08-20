using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using Microsoft.Win32;

namespace IPhoneMirror.App.Windows;

public partial class MediaOutputSettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly bool _previewOnly;
    private bool _savePromptOpen;
    private (uint Width, uint Height)? _lastDefaultSize;

    internal MediaOutputSettingsWindow(MainViewModel viewModel, bool previewOnly = false)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _previewOnly = previewOnly;
        DataContext = viewModel;
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
            FeedbackText.Text = LocalizationService.Get("DeveloperReadOnlyPreview");
            UpdateStartButtons();
            return;
        }
        // Re-probe on each opening so a user can install FFmpeg or update PATH
        // without restarting the entire application.
        await RunAsync(() => _viewModel.EnsureMediaOutputCapabilitiesAsync(force: true));
        SelectFirstSupportedProtocol();
        UpdateStartButtons();
        if (!_viewModel.IsMediaOutputRunning &&
            _viewModel.PendingRecordingPath is not null)
            await PromptToSaveRecordingAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Loaded -= OnLoaded;
        Closing -= OnClosing;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CanStartMediaOutput) or
            nameof(MainViewModel.CanRecordMediaOutput) or
            nameof(MainViewModel.CanStreamRtmp) or
            nameof(MainViewModel.CanStreamSrt) or
            nameof(MainViewModel.CanStreamWhip) or
            nameof(MainViewModel.CanUseVirtualCamera) or
            nameof(MainViewModel.CanInstallVirtualCamera) or
            nameof(MainViewModel.CanUninstallVirtualCamera) or
            nameof(MainViewModel.CanStopMediaOutput))
            UpdateStartButtons();
        if (e.PropertyName == nameof(MainViewModel.SourceVideoHeight) &&
            !_viewModel.IsMediaOutputRunning)
            ApplyPreviewOutputDefaults(force: false);
        if (e.PropertyName == nameof(MainViewModel.IsMediaOutputRunning) &&
            !_viewModel.IsMediaOutputRunning &&
            _viewModel.PendingRecordingPath is not null)
            _ = Dispatcher.BeginInvoke(async () =>
                await PromptToSaveRecordingAsync());
    }

    private void ApplyPreviewOutputDefaults(bool force)
    {
        if (!IsLoaded && !force) return;
        var size = _viewModel.SuggestedMediaOutputSize();
        var previous = _lastDefaultSize;
        var updateRecording = force || previous is null ||
            (RecordWidthBox.Text == previous.Value.Width.ToString() &&
             RecordHeightBox.Text == previous.Value.Height.ToString());
        var updateStreaming = force || previous is null ||
            (StreamWidthBox.Text == previous.Value.Width.ToString() &&
             StreamHeightBox.Text == previous.Value.Height.ToString());
        var previousResolution = previous is null ? null :
            $"{previous.Value.Width}x{previous.Value.Height}";
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
        SelectVirtualCameraResolution(size.Width, size.Height,
            updateVirtualCamera);
        _lastDefaultSize = size;
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
        if (_previewOnly)
        {
            StartRecordingButton.IsEnabled = false;
            StartStreamingButton.IsEnabled = false;
            return;
        }
        StartRecordingButton.IsEnabled = _viewModel.CanStartMediaOutput &&
            _viewModel.CanRecordMediaOutput;
        StartStreamingButton.IsEnabled = _viewModel.CanStartMediaOutput &&
            SelectedProtocolSupported();
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
        if (!TryReadOutputSettings(RecordWidthBox, RecordHeightBox, RecordFpsBox,
                RecordBitrateBox, out var width, out var height, out var fps, out var bitrate))
            return;
        var result = await _viewModel.StartRecordingAsync(
            width, height, fps, bitrate);
        FeedbackText.Text = result.Message;
    }

    private async void OnStartStreamingClick(object sender, RoutedEventArgs e)
    {
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
            width, height, fps, bitrate);
        FeedbackText.Text = result.Message;
    }

    private async void OnStartVirtualCameraClick(object sender, RoutedEventArgs e)
    {
        if (VirtualCameraResolutionBox.SelectedItem is not ComboBoxItem
                { Tag: string resolution } ||
            VirtualCameraFpsBox.SelectedItem is not ComboBoxItem
                { Tag: string frameRateText } ||
            !TryParseResolution(resolution, out var width, out var height) ||
            !int.TryParse(frameRateText, out var frameRate))
        {
            FeedbackText.Text = LocalizationService.Get("MediaOutputInvalidSettings");
            return;
        }
        var result = await _viewModel.StartVirtualCameraAsync(
            width, height, frameRate);
        FeedbackText.Text = result.Message;
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
        FeedbackText.Text = result.Message;
    }

    private async void OnUninstallVirtualCameraClick(object sender, RoutedEventArgs e)
    {
        var result = await _viewModel.UninstallVirtualCameraAsync();
        FeedbackText.Text = result.Message;
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => _viewModel.StopMediaOutputAsync());
        await PromptToSaveRecordingAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task PromptToSaveRecordingAsync()
    {
        if (_savePromptOpen || _viewModel.IsMediaOutputRunning) return;
        var pending = _viewModel.PendingRecordingPath;
        if (pending is null) return;
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
                FeedbackText.Text = LocalizationService.Get("RecordingPendingSave");
                return;
            }

            try
            {
                try { File.Move(pending, dialog.FileName, overwrite: true); }
                catch (IOException)
                {
                    await using var source = new FileStream(pending, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var destination = new FileStream(dialog.FileName,
                        FileMode.Create, FileAccess.Write, FileShare.None,
                        1024 * 1024, FileOptions.Asynchronous);
                    await source.CopyToAsync(destination);
                    await destination.FlushAsync();
                    File.Delete(pending);
                }
                _viewModel.MarkPendingRecordingSaved(pending);
                FeedbackText.Text = LocalizationService.Format(
                    "RecordingSavedFormat", dialog.FileName);
            }
            catch (Exception error)
            {
                DiagnosticLogger.Exception("recording", "save_failed", error,
                    ("file", Path.GetFileName(dialog.FileName)));
                FeedbackText.Text = LocalizationService.Format(
                    "RecordingSaveFailedFormat", error.Message);
            }
        }
        finally { _savePromptOpen = false; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("media_output", "settings_action_failed", error);
            FeedbackText.Text = error.Message;
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
            width >= 160 && height >= 160 && fps is >= 10 and <= 60 &&
            bitrate is >= 500 and <= 50000)
            return true;
        FeedbackText.Text = LocalizationService.Get("MediaOutputInvalidSettings");
        return false;
    }
}
