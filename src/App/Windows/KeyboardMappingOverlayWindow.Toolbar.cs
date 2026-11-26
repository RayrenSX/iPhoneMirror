using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.SharedUI.Services;

namespace IPhoneMirror.App.Windows;

internal sealed partial class KeyboardMappingOverlayWindow
{
    private PickingToolbarWindow? _pickingToolbar;
    private Border _toolbar = null!;
    private TextBlock _hint = null!;
    private Button _retry = null!;
    private bool _toolbarOverVideo;
    private Rect _toolbarScreenBounds;

    private void InitializePickingToolbar()
    {
        _hint = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 230, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "PreviewTextBrush");
        _hint.SetResourceReference(TextBlock.FontSizeProperty, "CaptionFontSize");
        Button ActionButton(string text)
        {
            var button = new Button { Padding = new Thickness(8, 4, 8, 4), MinHeight = 28, MinWidth = 0,
                Margin = new Thickness(4, 0, 0, 0), Focusable = false, FocusVisualStyle = null, Cursor = Cursors.Arrow };
            button.SetResourceReference(StyleProperty, "CompactButton");
            button.SetResourceReference(ContentControl.ContentProperty, text);
            return button;
        }
        var cancel = ActionButton("MappingPickCancel");
        cancel.Click += (_, _) => Complete(null);
        _retry = ActionButton("MappingRepick");
        _retry.Click += (_, _) => { _start = null; _dragging = false; ReleaseMouseCapture(); RefreshDrawing(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_hint); row.Children.Add(_retry); row.Children.Add(cancel);
        _toolbar = new Border { Child = row, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1) };
        _toolbar.SetResourceReference(Border.BackgroundProperty, "PreviewPanelAltBrush");
        _toolbar.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
    }

    private void UpdatePickingToolbar()
    {
        if (_pick is not { } pick || _closed || _pixelWidth <= 0 || _pixelHeight <= 0) return;
        _hint.Text = LocalizationService.Get(_dragging && pick.Entry.IsSwipe ? "WizardRecording" :
            pick.Entry.IsSwipe ? "MappingPickDragCompact" : "MappingPickClickCompact");
        _hint.ToolTip = LocalizationService.Get(pick.Entry.IsSwipe ? "MappingPickDrag" : "MappingPickClick");
        _retry.Visibility = pick.Entry.IsSwipe ? Visibility.Visible : Visibility.Collapsed;
        var origin = new NativePoint();
        if (!ClientToScreen(Surface.Window, ref origin)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = new Size(Math.Ceiling(_toolbar.DesiredSize.Width * dpi.DpiScaleX), Math.Ceiling(_toolbar.DesiredSize.Height * dpi.DpiScaleY));
        var preview = new Rect(origin.X, origin.Y, _pixelWidth, _pixelHeight);
        var picture = PreviewCoordinateMapper.ContentRect(_pixelWidth, _pixelHeight, Surface.Width, Surface.Height);
        if (picture.IsEmpty) return;
        picture.Offset(origin.X, origin.Y);
        var monitor = new ToolbarMonitorInfo { Size = (uint)Marshal.SizeOf<ToolbarMonitorInfo>() };
        var area = GetMonitorInfoW(MonitorFromWindow(Surface.Window, 2), ref monitor)
            ? new Rect(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top)
            : preview;
        var bounds = PreviewCoordinateMapper.PlaceOverlayToolbar(preview, picture, area, size, 8 * dpi.DpiScaleX);
        _toolbarOverVideo = bounds.IntersectsWith(picture);
        var toolbar = _pickingToolbar ??= CreatePickingToolbar();
        toolbar.SetClickThrough(_toolbarOverVideo);
        if (_toolbarScreenBounds != bounds || !toolbar.IsVisible)
        {
            _toolbarScreenBounds = bounds;
            var handle = new WindowInteropHelper(toolbar).EnsureHandle();
            if (!StillPicking()) return;
            SetWindowPos(handle, 0, (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
                (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 0x0014);
        }
        if (!StillPicking()) return;
        if (!toolbar.IsVisible) toolbar.Show();
        if (!StillPicking()) return;
        if (_toolbarOverVideo && IsMouseOver) UpdatePickingToolbarOcclusion(Mouse.GetPosition(this));
        else { toolbar.Opacity = 1; toolbar.IsHitTestVisible = true; }

        bool StillPicking() => !_closed && ReferenceEquals(_pick, pick) && ReferenceEquals(_pickingToolbar, toolbar);
    }

    private PickingToolbarWindow CreatePickingToolbar()
    {
        var toolbar = new PickingToolbarWindow(this, _toolbar);
        toolbar.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pickingToolbar, toolbar)) _pickingToolbar = null;
            toolbar.Content = null;
        };
        return toolbar;
    }

    private void UpdatePickingToolbarOcclusion(Point pointer)
    {
        if (!_toolbarOverVideo || _pickingToolbar is null) return;
        var exclusion = _toolbarScreenBounds; exclusion.Inflate(24, 24);
        var hidden = exclusion.Contains(PointToScreen(pointer));
        _pickingToolbar.Opacity = hidden ? 0 : 1;
        _pickingToolbar.IsHitTestVisible = !hidden;
    }

    private sealed class PickingToolbarWindow : Window
    {
        private bool _clickThrough;
        internal PickingToolbarWindow(Window owner, UIElement content)
        {
            WindowWorkAreaController.UseOwnerBounds(this);
            Style = new Style(typeof(Window));
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Owner = owner; Content = content;
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                SetClickThrough(_clickThrough);
                HwndSource.FromHwnd(handle)?.AddHook(NoActivate);
            };
        }
        internal void SetClickThrough(bool enabled)
        {
            _clickThrough = enabled;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == 0) return;
            var style = GetWindowLongPtrW(handle, -20).ToInt64() | 0x08000000;
            SetWindowLongPtrW(handle, -20, (nint)(enabled ? style | 0x20 : style & ~0x20));
        }
        private nint NoActivate(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE: keep Esc and picking focus on the overlay.
            if (message == 0x0084 && _clickThrough) { handled = true; return -1; }
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ToolbarMonitorInfo { internal uint Size; internal NativeRect Monitor, Work; internal uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref ToolbarMonitorInfo info);
}
