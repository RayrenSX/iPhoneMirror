using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace IPhoneMirror.App.Windows;

internal sealed class ScreenTextFeedbackWindow : Window
{
    private readonly nint _owner;
    private readonly FrameworkElement? _preview;
    private readonly Func<double>? _previewAspect;
    private readonly Func<bool> _isCurrent;
    private readonly DispatcherTimer _timer;
    private readonly Border _card;
    private readonly long _started = Environment.TickCount64;
    private HwndSource? _source;

    internal ScreenTextFeedbackWindow(nint owner, FrameworkElement? preview, string message, Func<bool> isCurrent,
        Func<double>? previewAspect = null)
    {
        _owner = owner;
        _preview = preview;
        _previewAspect = previewAspect;
        _isCurrent = isCurrent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        Width = Height = 1;
        var text = new TextBlock
        {
            Text = message, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        text.SetResourceReference(TextBlock.FontSizeProperty, "BodyFontSize");
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(text, "ScreenTextFeedback");
        _card = new Border
        {
            Child = text, Padding = new Thickness(16, 8, 16, 8),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
        };
        _card.SetResourceReference(Border.BackgroundProperty, "PreviewPanelAltBrush");
        _card.SetResourceReference(Border.BorderBrushProperty, "PreviewBorderBrush");
        Content = _card;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _timer.Tick += (_, _) =>
        {
            if (Environment.TickCount64 - _started >= 5000 || !_isCurrent() || !UpdateBounds()) Close();
        };
        Closed += (_, _) => _timer.Stop();
        new WindowInteropHelper(this).Owner = owner;
        SourceInitialized += (_, _) =>
        {
            _source = (HwndSource?)PresentationSource.FromVisual(this);
            if (_source is null) return;
            var handle = _source.Handle;
            var styles = GetWindowLongPtrW(handle, -20).ToInt64();
            _ = SetWindowLongPtrW(handle, -20, (nint)(styles | 0x08000020)); // NOACTIVATE | TRANSPARENT
            _source.AddHook(HitTest);
            _ = UpdateBounds();
        };
    }

    internal void ShowForPreview()
    {
        if (!_isCurrent() || !TryReadPreviewBounds(out _))
        {
            Close();
            return;
        }
        Show();
        if (!UpdateBounds())
        {
            Close();
            return;
        }
        _timer.Start();
    }

    private bool UpdateBounds()
    {
        if (_source is null || !TryReadPreviewBounds(out var preview)) return false;
        var dpi = GetDpiForWindow(_owner);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var margin = Math.Min(preview.Width / 8, Math.Clamp(preview.Width * 0.04, 8 * scale, 20 * scale));
        _card.MaxWidth = Math.Min(420, (preview.Width - 2 * margin) / scale);
        _card.Measure(new Size(_card.MaxWidth, double.PositiveInfinity));
        var width = Math.Max(1, (int)Math.Ceiling(_card.DesiredSize.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(_card.DesiredSize.Height * scale));
        var x = (int)Math.Round(preview.Left + (preview.Width - width) / 2);
        var y = (int)Math.Round(Math.Max(preview.Top, preview.Bottom - height - margin));
        return SetWindowPos(_source.Handle, 0, x, y, width, height, 0x0014); // NOACTIVATE | NOZORDER
    }

    private bool TryReadPreviewBounds(out Rect bounds)
    {
        if (!TryReadPreviewBounds(_owner, _preview, out bounds)) return false;
        var aspect = _previewAspect?.Invoke() ?? 0;
        if (aspect > 0)
        {
            var width = Math.Min(bounds.Width, bounds.Height * aspect);
            var height = Math.Min(bounds.Height, bounds.Width / aspect);
            bounds = new Rect(bounds.X + (bounds.Width - width) / 2,
                bounds.Y + (bounds.Height - height) / 2, width, height);
        }
        return bounds.Width >= 64 && bounds.Height >= 40;
    }

    internal static bool TryReadPreviewBounds(nint owner, FrameworkElement? preview, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!IsWindowVisible(owner) || IsIconic(owner)) return false;
        if (preview is { IsVisible: true } && PresentationSource.FromVisual(preview) is not null)
        {
            bounds = new Rect(preview.PointToScreen(new Point()),
                preview.PointToScreen(new Point(preview.ActualWidth, preview.ActualHeight)));
        }
        else if (preview is null && GetClientRect(owner, out var client))
        {
            var origin = new PixelPoint();
            if (ClientToScreen(owner, ref origin))
                bounds = new Rect(origin.X, origin.Y, client.Right, client.Bottom);
        }
        return !bounds.IsEmpty && bounds.Width >= 64 && bounds.Height >= 40;
    }

    private static nint HitTest(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0084) return 0;
        handled = true;
        return -1; // HTTRANSPARENT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelPoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PixelRect { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out PixelRect rectangle);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint window, ref PixelPoint point);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
