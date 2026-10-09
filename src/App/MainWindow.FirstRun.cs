using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    internal bool ShowFirstRunSetup(bool rerun = false)
    {
        if (_viewModel.SetupActive) return false;
        if (_viewModel.HasAnyCaptureSession || _viewModel.IsUsbControlEnabled || _viewModel.IsWirelessControlEnabled || _viewModel.IsBluetoothControlEnabled)
        {
            AppPromptWindow.Inform(LocalizationService.Get("SetupTitle"), LocalizationService.Get("SetupStopSessions"));
            return false;
        }
        var wizard = new FirstRunSetupWindow(_viewModel, rerun: rerun);
        if (IsVisible) { wizard.Owner = this; wizard.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        var shutdownMode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { wizard.ShowDialog(); }
        finally { Application.Current.ShutdownMode = shutdownMode; }
        ApplyApplicationDisplayMode();
        if (_startupServicesStarted) ApplyTrayMode();
        return wizard.StartRequested;
    }
    private void OnFirstRunSetupClick(object sender, RoutedEventArgs e)
    {
        if (ShowFirstRunSetup(rerun: true)) StartAfterSetup();
    }
    internal void StartAfterSetup()
    {
        if (_viewModel.SelectedDevice is null)
            _viewModel.SelectedDevice = _viewModel.Devices.FirstOrDefault(d => !d.IsMediaCast);
        if (IsTrayMode && _viewModel.SelectedDevice is { } device)
        { _ = StartTrayProjectionAsync(device); return; }
        if (_viewModel.StartCommand.CanExecute(null)) _viewModel.StartCommand.Execute(null);
    }
}
