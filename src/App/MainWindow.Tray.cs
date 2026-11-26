using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private TrayIconService? _trayIcon;
    private TrayPanelWindow? _trayPanel;
    private ProjectionSettingsWindow? _traySettingsWindow;
    private bool _startupServicesStarted;
    private bool _exitRequested;
    internal bool IsTrayMode => _viewModel.IsTrayApplicationMode || (Application.Current as App)?.AutomationBackground == true;

    internal void ExitApplication()
    {
        _exitRequested = true;
        try { Close(); }
        finally { _exitRequested = false; }
    }

    private bool TryCloseMainWindowToTray()
    {
        if (_exitRequested || !_viewModel.CloseMainWindowToTray ||
            (Application.Current as App)?.IsSystemSessionEnding == true) return false;
        if (_viewModel.CanRestoreMainWindowFromTray) return true;
        if (IsTrayMode) return false;
        _viewModel.EnterTrayOnMainWindowClose();
        return true;
    }

    internal void StartInTray()
    {
        ShowInTaskbar = false;
        // Create the controller HWND for hotkeys without showing the workspace.
        new WindowInteropHelper(this).EnsureHandle();
        ApplyTrayMode();
        StartDeviceServices();
    }

    private void StartDeviceServices()
    {
        _ = StartAutomationOnStartupAsync();
        if (_startupServicesStarted) return;
        _startupServicesStarted = true;
        _refreshTimer.Start();
        _mediaCastTimer.Start();
        _ = InitializeDeviceStartupAsync();
    }

    private void ApplyTrayMode()
    {
        if (_shutdownStarted) return;
        if (!IsTrayMode)
        {
            if (_trayIcon is null) return;
            DisposeTray();
            EnsurePreviewInteractionRecovery();
            _compactPreviewActive = false;
            _secondaryMirrors.PreviewClosed -= OnCompactPreviewClosed;
            ShowInTaskbar = true;
            ShowActivated = true;
            WindowState = WindowState.Normal;
            Show();
            Activate();
            QueueMainPreviewHostSync();
            return;
        }
        if (_trayIcon is not null) return;
        try
        {
            _trayIcon = new TrayIconService(ShowTrayPanel);
            _trayPanel = new TrayPanelWindow(_viewModel, StartTrayProjectionAsync,
                ShowTraySettings, ExitApplication);
            _trayPanel.PreviewInteractionRestoreRequested += RestorePreviewInteraction;
            UpdatePreviewInteractionRecovery();
        }
        catch (Exception error)
        {
            DisposeTray();
            EnsurePreviewInteractionRecovery();
            if ((Application.Current as App)?.AutomationBackground == true) return;
            if (_viewModel.CanRestoreMainWindowFromTray) _viewModel.RestoreMainWindowFromTray();
            else _viewModel.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete;
            ShowInTaskbar = true;
            Show();
            AppPromptWindow.Inform(LocalizationService.Get("ApplicationModeTray"),
                LocalizationService.Format("TrayUnavailable", error.Message), this);
            return;
        }
        _projectionSettingsWindow?.Close();
        if (_isFullScreen) ToggleFullScreen();
        MainPreviewHost.SetPresentationVisible(false);
        MainPreviewHost.Deactivate();
        Hide();
        ShowInTaskbar = false;
        // Reuse an existing capture when moving it out of the main workspace.
        _ = MoveActiveProjectionToTrayAsync();
    }

    private async Task MoveActiveProjectionToTrayAsync()
    {
        if ((Application.Current as App)?.AutomationBackground == true) return;
        try
        {
            if (_mediaCastActive) ShowMediaCastPreviewWindow();
            foreach (var device in _viewModel.Devices.Where(device => !device.IsMediaCast).ToArray())
            {
                if (!IsTrayMode || _shutdownStarted) return;
                if (_viewModel.GetDeviceSessionHandle(device.Udid) != 0 && !_secondaryMirrors.IsOpen(device))
                    await StartTrayProjectionAsync(device);
            }
        }
        catch (Exception error)
        {
            _trayPanel?.ReportError(error.Message);
            _viewModel.AddUiLog(error.Message);
            ShowTrayPanel();
        }
    }

    private void ShowTrayPanel()
    {
        if (_shutdownStarted || !IsTrayMode || _trayPanel is null || _trayIcon is null) return;
        if (_trayPanel.IsVisible && _trayPanel.IsActive) { _trayPanel.Hide(); return; }
        _trayIcon.ShowPanel(_trayPanel);
    }

    private async Task StartTrayProjectionAsync(DeviceViewModel device)
    {
        if (_shutdownStarted) return;
        if (device.IsMediaCast)
        {
            if (_mediaCastActive) ShowMediaCastPreviewWindow();
            return;
        }
        var result = await _secondaryMirrors.ShowAsync(device);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        if (_shutdownStarted) return;
        QueueMainPreviewHostSync();
        _secondaryMirrors.Activate(device.Udid);
    }

    private void ShowTraySettings()
    {
        if (_traySettingsWindow is not null) { _traySettingsWindow.Activate(); return; }
        var window = new ProjectionSettingsWindow(_viewModel,
            () => _viewModel.RefreshAsync(forceDeviceEnumeration: true),
            ToggleActiveFullScreenAsync, OpenPreviewWindowAsync, CaptureScreenshotAsync,
            OnMediaOutputSettingsRequested)
        { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _secondaryMirrors.ConfigurePhoneFrameSettings(window);
        _traySettingsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_traySettingsWindow, window)) _traySettingsWindow = null;
        };
        window.Show();
    }

    private void DisposeTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayPanel?.ClosePermanently();
        _trayPanel = null;
        _traySettingsWindow?.Close();
        _traySettingsWindow = null;
    }

    private void QueueTrayProjection(string udid, ulong sessionHandle)
    {
        if ((Application.Current as App)?.AutomationBackground == true) return;
        // Wait for session creation/recovery to release its gate before attaching
        // a renderer, and ignore callbacks invalidated by a stop or mode change.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_shutdownStarted || !IsTrayMode ||
                _viewModel.GetDeviceSessionHandle(udid) != sessionHandle) return;
            var device = _viewModel.Devices.FirstOrDefault(item => DeviceViewModel.UdidEquals(item.Udid, udid));
            if (device is null || _secondaryMirrors.IsOpen(device)) return;
            try { await StartTrayProjectionAsync(device); }
            catch (Exception error)
            {
                _viewModel.AddUiLog(error.Message);
                _trayPanel?.ReportError(error.Message);
            }
        }));
    }
}
