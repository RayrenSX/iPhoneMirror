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
        if (options is not { Enabled: true })
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
        try
        {
            await _compactLaunchGate.WaitAsync(_compactLaunchCancellation.Token);
            acquired = true;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                _compactLaunchCancellation.Token.ThrowIfCancellationRequested();
                var remaining = TimeSpan.FromSeconds(20) - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await _viewModel.RefreshAsync(forceDeviceEnumeration: true)
                    .WaitAsync(remaining, _compactLaunchCancellation.Token);
                _compactLaunchCancellation.Token.ThrowIfCancellationRequested();
                var devices = _viewModel.Devices.Where(device => !device.IsMediaCast).ToArray();
                var index = options.SelectDevice(devices.Select(device => device.Udid).ToArray());
                if (index >= 0)
                {
                    var device = devices[index];
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
            if (IsTrayMode)
            {
                _trayPanel?.ReportError(LocalizationService.Format("CompactLaunchFailed", error.Message));
                ShowTrayPanel();
                return;
            }
            Show();
            WindowState = WindowState.Normal;
            Activate();
            _viewModel.AddUiLog(LocalizationService.Format("CompactLaunchFailed", error.Message));
            IPhoneMirror.App.Windows.AppPromptWindow.Inform(LocalizationService.Get("CompactLaunchTitle"),
                LocalizationService.Format("CompactLaunchFailed", error.Message), this);
        }
        finally { if (acquired) _compactLaunchGate.Release(); }
    }

    private void OnCompactPreviewClosed(string deviceId)
    {
        if (IsTrayMode) return;
        if (!_compactPreviewActive || _secondaryMirrors.HasAnyOpen) return;
        _compactPreviewActive = false;
        // Let the native close handler finish before the ordinary async shutdown.
        Dispatcher.BeginInvoke(() => Close());
    }
}
