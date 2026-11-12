using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly HashSet<string> _deviceActionsInProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _deviceActionLifetime = new();
    internal Func<string, DeviceAction, CancellationToken, Task> ExecuteDeviceAction { get; set; } = DeviceActionService.ExecuteAsync;
    internal Func<DeviceViewModel, DeviceAction, bool>? ConfirmDeviceAction { get; set; }
    internal Action<string> CopyDeviceInfoText { get; set; } = Clipboard.SetText;

    private static DeviceViewModel? DeviceMenuTarget(ContextMenu menu) =>
        (menu.PlacementTarget as FrameworkElement)?.DataContext as DeviceViewModel;

    private void OnDeviceContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var device = DeviceMenuTarget(menu);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var localAction = item.Tag as string == "CopyInfo";
            item.IsEnabled = device is not null && !device.IsMediaCast &&
                (localAction || DeviceActionService.CanExecute(device) && !_deviceActionsInProgress.Contains(device.Udid));
            item.ToolTip = !localAction && device is not null && !DeviceActionService.CanExecute(device)
                ? LocalizationService.Get("DeviceActionUsbRequired") : null;
            ToolTipService.SetShowOnDisabled(item, true);
        }
    }

    private async void OnDeviceActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu ||
            DeviceMenuTarget(menu) is not { IsMediaCast: false } target) return;
        if (item.Tag as string == "CopyInfo")
        {
            try
            {
                CopyDeviceInfoText(LocalizationService.Format("DeviceActionInfoFormat", target.DisplayName,
                    target.ModelDisplay, target.OsDisplay, target.ConnectionType, target.Udid));
            }
            catch (Exception error) { AppPromptWindow.Inform(LocalizationService.Get("DeviceActionCopyInfo"), error.Message, this); }
            return;
        }
        if (!Enum.TryParse<DeviceAction>(item.Tag as string, out var action)) return;
        await RunDeviceActionAsync(target, action);
    }

    internal async Task RunDeviceActionAsync(DeviceViewModel target, DeviceAction action)
    {
        if (_deviceActionLifetime.IsCancellationRequested || !Enum.IsDefined(action) ||
            !DeviceActionService.CanExecute(target) || !_deviceActionsInProgress.Add(target.Udid)) return;
        var label = LocalizationService.Get("DeviceAction" + action);
        try
        {
            if (action is DeviceAction.Restart or DeviceAction.Shutdown)
            {
                var confirmed = ConfirmDeviceAction?.Invoke(target, action) ??
                    AppPromptWindow.ConfirmDestructive(label,
                        LocalizationService.Format("DeviceActionConfirm" + action, target.DisplayName), label, this);
                if (!confirmed) return;
            }
            // Refresh may replace a row while the confirmation dialog is open.
            // Always validate the same identity again; never use SelectedDevice.
            var current = _viewModel.Devices.FirstOrDefault(device => DeviceViewModel.UdidEquals(device.Udid, target.Udid));
            if (current is null || !DeviceActionService.CanExecute(current) || _deviceActionLifetime.IsCancellationRequested) return;
            await ExecuteDeviceAction(target.Udid, action, _deviceActionLifetime.Token);
            _viewModel.AddUiLog(LocalizationService.Format("DeviceActionSentFormat", target.DisplayName, label));
        }
        catch (OperationCanceledException) when (_deviceActionLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("device_action", "command_failed", error);
            if (!_deviceActionLifetime.IsCancellationRequested)
                AppPromptWindow.Inform(label, LocalizationService.Format("DeviceActionFailedFormat", label, error.Message), this);
        }
        finally { _deviceActionsInProgress.Remove(target.Udid); }
    }
}
