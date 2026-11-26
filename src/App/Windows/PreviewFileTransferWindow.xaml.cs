using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public partial class PreviewFileTransferWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private sealed record SharedApp(string Name, string BundleId);

    private readonly Func<string?, string?, CancellationToken, Task<JsonElement>> _request;
    private readonly string[] _files;
    private readonly Func<bool> _isCurrent;
    private readonly CancellationTokenSource _loading = new();
    private CancellationTokenSource? _transferCancellation;
    private bool _sending;

    internal PreviewFileTransferWindow(MainViewModel viewModel, DeviceViewModel device, string[] files,
        int skipped, Func<bool> isCurrent)
        : this(device.DisplayName, files, skipped, isCurrent, BindTarget(viewModel, device, isCurrent))
    {
    }

    private static Func<string?, string?, CancellationToken, Task<JsonElement>> BindTarget(
        MainViewModel viewModel, DeviceViewModel device, Func<bool> isCurrent)
    {
        var target = viewModel.GetPreviewSharedFilesTarget(device);
        return (bundle, file, token) => viewModel.PreviewSharedFilesAsync(device, target, bundle, file, isCurrent, token);
    }

    internal PreviewFileTransferWindow(string deviceName, string[] files, int skipped, Func<bool> isCurrent,
        Func<string?, string?, CancellationToken, Task<JsonElement>> request)
    {
        _request = request;
        _files = files;
        _isCurrent = isCurrent;
        InitializeComponent();
        TargetText.Text = LocalizationService.Format("PreviewFilesTarget", deviceName, files.Length);
        FileNames.ItemsSource = files.Take(4).Select(Path.GetFileName);
        RemainingFiles.Visibility = files.Length > 4 ? Visibility.Visible : Visibility.Collapsed;
        RemainingFiles.Text = LocalizationService.Format("PreviewFilesMore", files.Length - 4);
        SkippedText.Visibility = skipped > 0 && files.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkippedText.Text = LocalizationService.Format("PreviewFilesSkipped", skipped);
        ContentRendered += LoadDestinations;
        Closing += (_, e) =>
        {
            if (!_sending) return;
            e.Cancel = true;
            CancelTransfer();
        };
        Closed += (_, _) => { _loading.Cancel(); _loading.Dispose(); };
    }

    private async void LoadDestinations(object? sender, EventArgs e)
    {
        ContentRendered -= LoadDestinations;
        var cancellation = _loading.Token;
        try
        {
            if (_files.Length == 0)
            {
                StatusText.Text = LocalizationService.Get("PreviewFilesNoFiles");
                return;
            }
            StatusText.Text = LocalizationService.Get("PreviewFilesLoading");
            CheckTarget();
            var result = await _request(null, null, cancellation);
            cancellation.ThrowIfCancellationRequested();
            CheckTarget();
            var apps = result.GetProperty("apps").EnumerateArray().Select(app => new SharedApp(
                app.GetProperty("name").GetString()!, app.GetProperty("bundleId").GetString()!)).ToArray();
            Destination.ItemsSource = apps;
            Destination.IsEnabled = apps.Length > 0;
            if (apps.Length == 1) Destination.SelectedIndex = 0;
            StatusText.Text = LocalizationService.Get(apps.Length switch
            {
                0 => "PreviewFilesNoApps",
                1 => "PreviewFilesSharedDocuments",
                _ => "PreviewFilesChooseApp",
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested) StatusText.Text = ErrorText(error);
        }
    }

    private void CheckTarget()
    {
        if (!_isCurrent()) throw new InvalidOperationException(LocalizationService.Get("PreviewFilesConnectionUnavailable"));
    }

    private void DestinationChanged(object sender, SelectionChangedEventArgs e)
    {
        SendButton.IsEnabled = !_sending && Destination.SelectedItem is SharedApp;
        if (SendButton.IsEnabled)
            StatusText.Text = LocalizationService.Get("PreviewFilesSharedDocuments");
    }

    private async void SendClick(object sender, RoutedEventArgs e)
    {
        if (_sending || Destination.SelectedItem is not SharedApp app) return;
        _sending = true;
        using var cancellation = new CancellationTokenSource();
        _transferCancellation = cancellation;
        Destination.IsEnabled = false;
        SendButton.IsEnabled = false;
        SendButton.Visibility = Visibility.Collapsed;
        CloseButton.SetResourceReference(ContentProperty, "PreviewFilesStop");
        CloseButton.Focus();
        StatusText.Visibility = Visibility.Collapsed;
        TransferPanel.Visibility = Visibility.Visible;
        TransferTitle.Text = LocalizationService.Get("PreviewFilesSending");
        Progress.Maximum = _files.Length;
        var sent = 0;
        UpdateTransferCount(sent);
        try
        {
            foreach (var file in _files)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                CheckTarget();
                CurrentFile.Text = Path.GetFileName(file);
                await _request(app.BundleId, file, cancellation.Token);
                UpdateTransferCount(++sent);
            }
            TransferTitle.Text = LocalizationService.Get("PreviewFilesComplete");
            Progress.SetResourceReference(ForegroundProperty, "SuccessBrush");
            StatusText.Text = LocalizationService.Format("PreviewFilesSent", sent, app.Name);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            TransferTitle.Text = LocalizationService.Get("PreviewFilesStopped");
            StatusText.Text = LocalizationService.Format("PreviewFilesCancelled", sent, _files.Length);
        }
        catch (Exception error)
        {
            TransferTitle.Text = LocalizationService.Get("PreviewFilesIncomplete");
            StatusText.Text = LocalizationService.Format("PreviewFilesFailed", sent, _files.Length, ErrorText(error));
        }
        finally
        {
            _sending = false;
            _transferCancellation = null;
            CloseButton.IsEnabled = HeaderClose.IsEnabled = true;
            CloseButton.SetResourceReference(ContentProperty, "Close");
            CurrentFile.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private void UpdateTransferCount(int sent)
    {
        Progress.Value = sent;
        TransferCount.Text = LocalizationService.Format("PreviewFilesTransferCount", sent, _files.Length);
    }

    private static string ErrorText(Exception error) => error switch
    {
        AutomationException { Code: "PREVIEW_FILES_CONTROL_REQUIRED" } =>
            LocalizationService.Get("PreviewFilesControlRequired"),
        AutomationException { Code: "PREVIEW_FILES_BINDING_REQUIRED" } =>
            LocalizationService.Get("PreviewFilesBindingRequired"),
        AutomationException { Code: "CONTROL_NOT_AVAILABLE" or "apple_device_not_found" or "apple_device_connection_timeout" } =>
            LocalizationService.Get("PreviewFilesConnectionUnavailable"),
        AutomationException { Code: "FILE_IMPORT_FAILED" } =>
            LocalizationService.Get("DeviceHubPanelFileImportFailed"),
        AutomationException { Code: "apple_device_not_trusted" } =>
            LocalizationService.Get("UsbControlFailureNotTrusted"),
        AutomationException { Code: "CONTROL_LOCKED" or "MANAGEMENT_BUSY" } =>
            LocalizationService.Get("DeviceHubPanelBusyHint"),
        _ => error.Message,
    };

    private void CancelTransfer()
    {
        CloseButton.IsEnabled = HeaderClose.IsEnabled = false;
        TransferTitle.Text = LocalizationService.Get("PreviewFilesCancelling");
        _transferCancellation?.Cancel();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
