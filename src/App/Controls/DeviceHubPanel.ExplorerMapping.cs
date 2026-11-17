using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Controls;

internal sealed partial class DeviceHubPanel
{
    private bool _syncingExplorerMapping;
    private bool _explorerMappingUiPending;
    private string? _explorerMappingOpenError;

    private void InitializeExplorerMapping() =>
        _viewModel.PropertyChanged += OnExplorerMappingStateChanged;

    private bool OwnsExplorerMapping => _device is not null &&
        DeviceViewModel.UdidEquals(_device.Udid, _viewModel.ExplorerMappingDeviceUdid) &&
        _viewModel.ExplorerMappingDriveName.Length > 0 &&
        _viewModel.ExplorerMappingBundleId == SelectedDocumentBundle;

    private void OnExplorerMappingStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && !e.PropertyName.StartsWith("ExplorerMapping", StringComparison.Ordinal)) return;
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(() => OnExplorerMappingStateChanged(sender, e));
            return;
        }
        if (_disposed) return;
        _explorerMappingOpenError = null;
        SyncExplorerMapping();
    }

    private void SyncExplorerMapping()
    {
        if (!_initialized || _disposed) return;
        var own = OwnsExplorerMapping;
        var busy = _explorerMappingUiPending || _viewModel.ExplorerMappingBusy;
        var media = Option(FileRoot) != "crashes";
        var fileToolsVisible = _page == 3 && (_ready || _hasConnected || own);
        FileToolbar.Visibility = fileToolsVisible ? Visibility.Visible : Visibility.Collapsed;
        FileToolbar.IsEnabled = _ready && (!_busy || _quietOperation);
        ExplorerMappingRow.Visibility = fileToolsVisible && media
            ? Visibility.Visible : Visibility.Collapsed;
        _syncingExplorerMapping = true;
        try { ExplorerMappingToggle.IsChecked = own; }
        finally { _syncingExplorerMapping = false; }
        ExplorerMappingToggle.IsEnabled = !busy && _device is not null &&
            (own || _ready && media && !_busy);
        ExplorerMappingOpen.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        ExplorerMappingOpen.IsEnabled = own && !busy;
        var help = L("ExplorerHelp");
        if (_viewModel.ExplorerMappingLimitBytes > 0)
            help += Environment.NewLine + F("ExplorerLimitFormat", FormatBytes(_viewModel.ExplorerMappingLimitBytes));
        ExplorerMappingToggle.ToolTip = help;
        ExplorerMappingRow.ToolTip = help;
        ExplorerMappingOpen.ToolTip = L("ExplorerOpen");
        var error = _explorerMappingOpenError ??
            (_viewModel.ExplorerMappingHasError ? _viewModel.ExplorerMappingStatus : null);
        ExplorerMappingFeedback.Text = error ?? (own ? F("ExplorerMappedFormat", _viewModel.ExplorerMappingDriveName) : "");
        ExplorerMappingFeedback.Visibility = ExplorerMappingFeedback.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExplorerMappingFeedback.SetResourceReference(TextBlock.ForegroundProperty,
            error is not null ? "ErrorBrush" : "MutedTextBrush");
    }

    private async void OnExplorerMappingChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _disposed || _syncingExplorerMapping || _explorerMappingUiPending) return;
        var device = _device;
        var enable = ExplorerMappingToggle.IsChecked == true;
        var own = OwnsExplorerMapping;
        if (device is null || _viewModel.ExplorerMappingBusy ||
            enable && (!_ready || _busy || Option(FileRoot) == "crashes") || !enable && !own)
        { SyncExplorerMapping(); return; }
        _explorerMappingUiPending = true;
        _explorerMappingOpenError = null;
        SyncExplorerMapping();
        try
        {
            // The view model owns mapping lifetime. Changing tabs or hiding this
            // panel must not cancel a valid Explorer connection.
            await _viewModel.SetExplorerDocumentMappingAsync(device, enable, SelectedDocumentBundle);
        }
        catch (Exception error)
        {
            if (!_disposed && ReferenceEquals(device, _device))
                _explorerMappingOpenError = F("ExplorerFailedFormat", error.Message);
        }
        finally
        {
            _explorerMappingUiPending = false;
            if (!_disposed) SyncExplorerMapping();
        }
    }

    private void OnExplorerMappingOpenClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _viewModel.ExplorerMappingBusy || !OwnsExplorerMapping) return;
        var drive = _viewModel.ExplorerMappingDriveName;
        if (drive.Length != 2 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':') return;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = false };
            start.ArgumentList.Add(drive + Path.DirectorySeparatorChar);
            using var process = Process.Start(start);
            _explorerMappingOpenError = null;
        }
        catch (Exception error)
        { _explorerMappingOpenError = F("ExplorerOpenFailedFormat", error.Message); }
        SyncExplorerMapping();
    }
}
