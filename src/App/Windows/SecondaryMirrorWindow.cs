using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

/// <summary>
/// Minimal owner for a second USB capture session. It runs in a dedicated
/// process so every phone has isolated protocol, decoder, audio and D3D state.
/// Closing this window always joins CaptureSession and sends HPA0/HPD0.
/// </summary>
internal sealed class SecondaryMirrorWindow : Window
{
    private readonly SecondaryMirrorRequest _request;
    private readonly NativeCore _core;
    private readonly DispatcherTimer _statusTimer;
    private NativePreviewWindow? _preview;
    private bool _closing;

    internal SecondaryMirrorWindow(SecondaryMirrorRequest request)
    {
        _request = request;
        _core = new NativeCore();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += OnStatusTick;
        Title = $"iPhoneMirror — {request.Name}";
        Width = 1;
        Height = 1;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Opacity = 0;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var profile = DeviceCornerProfileResolver.Resolve(_request.ProductType, 1206, 2622);
        _ = NativeCore.SetPreviewCornerProfile(profile.IsRounded ? profile.RadiusRatio : 0, profile.CurveExponent);
        var started = _core.StartCapture(_request.Udid, playAudio: false);
        if (!started.Success)
        {
            MessageBox.Show(started.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }
        if (!NativePreviewWindow.TryCreateAndShow(1206, 2622, Title, out _preview) || _preview is null)
        {
            MessageBox.Show("无法创建独立预览窗口。", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }
        _preview.Closed += OnPreviewClosed;
        _statusTimer.Start();
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        try
        {
            var status = _core.GetCaptureStatus();
            if (status.Width != 0 && status.Height != 0) _preview?.SetSourceDimensions(status.Width, status.Height);
            if (status.State == CaptureState.Error)
            {
                MessageBox.Show(status.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void OnPreviewClosed(object? sender, EventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        _statusTimer.Stop();
        if (_preview is not null)
        {
            _preview.Closed -= OnPreviewClosed;
            _preview.Dispose();
            _preview = null;
        }
        _core.StopCapture();
        _core.Dispose();
    }
}
