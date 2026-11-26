using System.IO;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;
using Microsoft.Win32;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private bool _previewFileDropOpen;
    private ScreenTextFeedbackWindow? _screenTextFeedback;

    private void ShowPreviewFeedback(nint window, string key, string? source,
        Func<bool>? stillRelevant, params object[] values)
    {
        var message = values.Length == 0 ? LocalizationService.Get(key) : LocalizationService.Format(key, values);
        _viewModel.AddUiLog(message);
        _screenTextFeedback?.Close();
        if (window == 0) return;
        var mediaCast = DeviceViewModel.IsMediaCastUdid(source);
        var mediaGeneration = _mediaCastEvents.CurrentGeneration;
        var session = source is null || mediaCast ? 0UL : _viewModel.GetDeviceSessionHandle(source);
        bool Current() => !_shutdownStarted && !_bossKeyHidden && (stillRelevant?.Invoke() ?? true) &&
            (source is null || DeviceViewModel.UdidEquals(source, GetScreenTextSource(window)) &&
                (mediaCast ? _mediaCastEvents.CurrentGeneration == mediaGeneration
                    : _viewModel.GetDeviceSessionHandle(source) == session));
        FrameworkElement? preview = window == _windowSource?.Handle
            ? mediaCast ? MediaCastVideoHost : PreviewPanel
            : null;
        double MainPreviewAspect() => preview == MediaCastVideoHost
            ? MediaCastMediaElement.NaturalVideoHeight > 0
                ? (double)MediaCastMediaElement.NaturalVideoWidth / MediaCastMediaElement.NaturalVideoHeight : 0
            : _viewModel.SourceVideoHeight > 0
                ? (double)_viewModel.SourceVideoWidth / _viewModel.SourceVideoHeight : 0;
        var feedback = new ScreenTextFeedbackWindow(window, preview, message, Current, preview is null ? null : MainPreviewAspect);
        feedback.Closed += (_, _) =>
        {
            if (ReferenceEquals(_screenTextFeedback, feedback)) _screenTextFeedback = null;
        };
        _screenTextFeedback = feedback;
        feedback.ShowForPreview();
    }

    private string? GetScreenTextSource(nint window) =>
        _secondaryMirrors.DeviceForWindow(window) ??
        (_mediaCastActive && window != 0 && window == _mediaCastPreviewWindow?.Handle ? DeviceViewModel.MediaCastUdid : null) ??
        (window == _windowSource?.Handle && _viewModel.CanUseVisualPreviewTools
            ? _viewModel.SelectedDevice?.Udid : null);

    private DeviceViewModel? MainFileDropDevice => !_bossKeyHidden && _viewModel.CanUseVisualPreviewTools &&
        _viewModel.SelectedDevice is { IsMediaCast: false } device ? device : null;

    private void OnPickPreviewFilesClick(object sender, RoutedEventArgs e)
    {
        if (MainFileDropDevice is { } device) PickPreviewFiles(device, _windowSource?.Handle ?? 0);
    }

    private void PickPreviewFiles(DeviceViewModel device, nint window) =>
        _ = QueuePreviewFileDrop(device, window, null);

    private void OnMainPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)) return;
        e.Effects = MainFileDropDevice is not null && !_previewFileDropOpen &&
            e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnMainPreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] paths) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (MainFileDropDevice is not { } device || !e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return;
        if (QueuePreviewFileDrop(device, _windowSource?.Handle ?? 0, paths)) e.Effects = DragDropEffects.Copy;
    }

    private void OnMainNativePreviewFilesDropped(string[] paths)
    {
        if (MainFileDropDevice is { } device) _ = QueuePreviewFileDrop(device, _windowSource?.Handle ?? 0, paths);
    }

    private bool QueuePreviewFileDrop(DeviceViewModel device, nint window, string[]? paths)
    {
        if (_previewFileDropOpen || _bossKeyHidden || paths is { Length: 0 } || window == 0) return false;
        var session = _viewModel.GetDeviceSessionHandle(device.Udid);
        if (session == 0) return false;
        bool Current() => !_mappingClosing && !_bossKeyHidden &&
            _viewModel.GetDeviceSessionHandle(device.Udid) == session &&
            (window == _windowSource?.Handle ? ReferenceEquals(MainFileDropDevice, device) :
                DeviceViewModel.UdidEquals(_secondaryMirrors.DeviceForWindow(window), device.Udid));
        bool ControlReady()
        {
            if (_viewModel.IsDeviceHubReady(device)) return true;
            DiagnosticLogger.Error("shared_files", "control_required", ("device", AppLog.Device(device.Udid)));
            ShowPreviewFeedback(window, "PreviewFilesControlRequired", device.Udid, Current);
            return false;
        }
        if (!Current() || !ControlReady()) return false;

        _previewFileDropOpen = true;
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (!Current() || !ControlReady()) return;
                if (paths is null)
                {
                    var picker = new OpenFileDialog
                    {
                        Title = LocalizationService.Get("PreviewFilesPick"), Multiselect = true, CheckFileExists = true,
                    };
                    var accepted = window == _windowSource?.Handle ? picker.ShowDialog(this) : picker.ShowDialog();
                    if (accepted != true || !Current()) return;
                    paths = picker.FileNames;
                }
                var candidates = paths;
                var files = await Task.Run(() => candidates.Where(path => Path.IsPathFullyQualified(path) && File.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                if (!Current() || !ControlReady()) return;
                var packages = PreviewIpaActionWindow.IpaFiles(files);
                if (packages.Length > 0)
                {
                    var choice = new PreviewIpaActionWindow(device.DisplayName, files);
                    if (window == _windowSource?.Handle) choice.Owner = this;
                    else new WindowInteropHelper(choice).Owner = window;
                    choice.ShowDialog();
                    if (choice.SelectedAction is null || !Current() || !ControlReady()) return;
                    if (choice.SelectedAction == PreviewIpaAction.Install)
                    {
                        var installer = _viewModel.CapturePreviewIpaInstaller(device, Current);
                        var installDialog = new PreviewIpaInstallWindow(device.DisplayName, installer.Udid,
                            packages, Current, installer.Install);
                        if (window == _windowSource?.Handle) installDialog.Owner = this;
                        else new WindowInteropHelper(installDialog).Owner = window;
                        installDialog.ShowDialog();
                        return;
                    }
                }
                var dialog = new PreviewFileTransferWindow(_viewModel, device, files, paths.Length - files.Length, Current);
                if (window == _windowSource?.Handle) dialog.Owner = this;
                else new WindowInteropHelper(dialog).Owner = window;
                dialog.ShowDialog();
            }
            catch (Exception error)
            {
                _viewModel.AddUiLog(LocalizationService.Format("PreviewFilesFailed", 0, paths?.Length ?? 0, error.Message));
            }
            finally { _previewFileDropOpen = false; }
        });
        return true;
    }
}
