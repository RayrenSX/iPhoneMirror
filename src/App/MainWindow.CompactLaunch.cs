using System.Diagnostics;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private bool _compactPreviewActive;
    private readonly CancellationTokenSource _compactLaunchCancellation = new();
    private readonly SemaphoreSlim _compactLaunchGate = new(1, 1);

    private async Task InitializeDeviceStartupAsync()
    {
        var options = (Application.Current as App)?.LaunchOptions;
        if (options is not { Enabled: true } || options.AutomationBackground)
        {
            await _viewModel.RefreshAsync();
            return;
        }
        await OpenCompactRequestAsync(options);
    }

    internal async Task OpenCompactRequestAsync(CompactLaunchOptions options)
    {
        if (_compactLaunchCancellation.IsCancellationRequested) return;
        var acquired = false;
        var deadline = Stopwatch.StartNew();
        var phase = "waiting_for_request_gate";
        DiagnosticLogger.Info("startup", "compact_launch_begin",
            ("device", AppLog.Device(options.DeviceId)),
            ("working_directory_is_app_directory", string.Equals(
                System.IO.Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\'),
                AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
        try
        {
            await _compactLaunchGate.WaitAsync(_compactLaunchCancellation.Token);
            acquired = true;
            deadline.Restart();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                _compactLaunchCancellation.Token.ThrowIfCancellationRequested();
                var remaining = TimeSpan.FromSeconds(20) - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                phase = "device_discovery";
                // A wired shortcut must not spend its discovery budget starting
                // unrelated AirPlay/DLNA receivers and their child processes.
                var refresh = options.DeviceId is { } id &&
                    !Models.DeviceViewModel.IsWirelessUdid(id)
                    ? _viewModel.RefreshCompactDeviceAsync()
                    : _viewModel.RefreshAsync(forceDeviceEnumeration: true);
                await refresh
                    .WaitAsync(remaining, _compactLaunchCancellation.Token);
                _compactLaunchCancellation.Token.ThrowIfCancellationRequested();
                var devices = _viewModel.Devices.Where(device => !device.IsMediaCast).ToArray();
                var index = options.SelectDevice(devices.Select(device => device.Udid).ToArray());
                if (index >= 0)
                {
                    var device = devices[index];
                    phase = "preview_start";
                    if ((Application.Current as App)?.LaunchOptions.Enabled == true)
                        _viewModel.SelectedDevice = device;
                    var result = await _secondaryMirrors.ShowAsync(device);
                    _compactLaunchCancellation.Token.ThrowIfCancellationRequested();
                    if (!result.Success) throw new InvalidOperationException(result.Message);
                    if ((Application.Current as App)?.LaunchOptions.Enabled == true)
                    {
                        if (!_compactPreviewActive)
                            _secondaryMirrors.PreviewClosed += OnCompactPreviewClosed;
                        _compactPreviewActive = true;
                        Hide();
                    }
                    _secondaryMirrors.Activate(device.Udid);
                    DiagnosticLogger.Info("startup", "compact_launch_ready",
                        ("device", AppLog.Device(device.Udid)), ("elapsed_ms", deadline.ElapsedMilliseconds));
                    return;
                }
                if (options.DeviceId is null && devices.Length > 1)
                    throw new InvalidOperationException(LocalizationService.Get("CompactLaunchMultipleDevices"));
                await Task.Delay(500, _compactLaunchCancellation.Token);
            }
            throw new InvalidOperationException(LocalizationService.Get("CompactLaunchDeviceUnavailable"));
        }
        catch (Exception) when (_compactLaunchCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("startup", "compact_launch_failed", error,
                ("phase", phase), ("elapsed_ms", deadline.ElapsedMilliseconds));
            var message = CompactLaunchErrorMessage(error, phase);
            if (IsTrayMode)
            {
                _trayPanel?.ReportError(LocalizationService.Format("CompactLaunchFailed", message));
                ShowTrayPanel();
                return;
            }
            Show();
            WindowState = WindowState.Normal;
            Activate();
            _viewModel.AddUiLog(LocalizationService.Format("CompactLaunchFailed", message));
            IPhoneMirror.App.Windows.AppPromptWindow.Inform(LocalizationService.Get("CompactLaunchTitle"),
                LocalizationService.Format("CompactLaunchFailed", message), this);
        }
        finally { if (acquired) _compactLaunchGate.Release(); }
    }

    private static string CompactLaunchErrorMessage(Exception error, string phase) =>
        error is TimeoutException && phase == "device_discovery"
            ? LocalizationService.Get("CompactLaunchDeviceUnavailable") : error.Message;

    private void OnCompactPreviewClosed(string deviceId)
    {
        if (IsTrayMode) return;
        if (!_compactPreviewActive || _secondaryMirrors.HasAnyOpen) return;
        _compactPreviewActive = false;
        // Let the native close handler finish before the ordinary async shutdown.
        Dispatcher.BeginInvoke(ExitApplication);
    }
}
