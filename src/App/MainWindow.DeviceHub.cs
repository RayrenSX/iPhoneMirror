using System.Windows;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private bool _isDeviceHubPanelVisible;
    private DeviceHubPanel? _deviceHubPanel;
    internal MainViewModel DeviceHubViewModel => _viewModel;
    private bool IsRightWorkspacePanelVisible => _isSettingsPanelVisible || _isDeviceHubPanelVisible;

    private void OnNavigateDeviceHubClick(object sender, RoutedEventArgs e)
    {
        if (!_workspaceControlsReady || _isSynchronizingWorkspacePanelControls) return;
        SetDeviceHubPanelVisible(!_isDeviceHubPanelVisible);
    }

    private void SetDeviceHubPanelVisible(bool visible)
    {
        if (_isDeviceHubPanelVisible == visible) return;
        _isDeviceHubPanelVisible = visible;
        if (visible)
        {
            _isSettingsPanelVisible = false;
            _workspacePrefersRightPanel = true;
            if (_deviceHubPanel is null)
            {
                _deviceHubPanel = new DeviceHubPanel(_viewModel);
                _deviceHubPanel.CloseRequested += (_, _) => SetDeviceHubPanelVisible(false);
                DeviceHubPanelHost.Content = _deviceHubPanel;
            }
        }
        SynchronizeRightPanelContent();
        ApplyWorkspacePanelState(animate: IsLoaded && !_isFullScreen);
    }

    private void SynchronizeRightPanelContent()
    {
        UpdateRightPanelPages(keepClosingPage: true);
        _deviceHubPanel?.SetDevice(_viewModel.SelectedDevice, _isDeviceHubPanelVisible && !_isFullScreen);
    }

    private void UpdateRightPanelPages(bool keepClosingPage = false)
    {
        // The shared sidebar clips the outgoing page until its close animation ends.
        if (keepClosingPage && !IsRightWorkspacePanelVisible) return;
        ControlPanelContent.Visibility = _isSettingsPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        DeviceHubPanelHost.Visibility = _isDeviceHubPanelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDeviceHubContext(string? property)
    {
        if (property is null or nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.IsUsbControlEnabled) or
            nameof(MainViewModel.IsWirelessControlEnabled) or nameof(MainViewModel.UsbControlIsInputEnabled) or
            nameof(MainViewModel.CurrentSessionHandle))
            _deviceHubPanel?.SetDevice(_viewModel.SelectedDevice, _isDeviceHubPanelVisible && !_isFullScreen);
    }
}
