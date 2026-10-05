using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

// Like ProtectedContentOverlayWindow, use an owned transparent HWND above
// native D3D airspace. Normal markers never activate or intercept the mouse.
internal sealed class KeyboardMappingOverlayWindow : Window
{
    private readonly MappingVisual _visual;
    private readonly Border _toolbar;
    private readonly TextBlock _hint;
    private HwndSource? _source;
    private MappingPositionPick? _pick;
    private (double X, double Y)? _start, _cursor;
    private long _downTime;
    private bool _dragging;
    private bool _closed;
    private int _pixelWidth, _pixelHeight;
    private IReadOnlyList<KeyboardMappingEntry> _entries = [];
    internal MappingPreviewSurface Surface { get; private set; }
    internal bool IsPicking => _pick is not null;

    internal KeyboardMappingOverlayWindow(MappingPreviewSurface surface)
    {
        Surface = surface;
        Title = LocalizationService.Get("MappingPicking");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _visual = new MappingVisual(this) { IsHitTestVisible = false };
        var root = new Grid();
        root.Children.Add(_visual);
        _hint = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "PreviewTextBrush");
        var cancel = new Button { Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        cancel.SetResourceReference(ContentControl.ContentProperty, "Cancel");
        cancel.Click += (_, _) => Complete(null);
        var retry = new Button { Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        retry.SetResourceReference(ContentControl.ContentProperty, "MappingRepick");
        retry.Click += (_, _) => { _start = null; _dragging = false; ReleaseMouseCapture(); RefreshDrawing(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(retry); buttons.Children.Add(cancel);
        var panel = new StackPanel(); panel.Children.Add(_hint); panel.Children.Add(buttons);
        _toolbar = new Border { Child = panel, Padding = new Thickness(8), Margin = new Thickness(8),
            CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 420, Visibility = Visibility.Collapsed };
        _toolbar.SetResourceReference(Border.BackgroundProperty, "PreviewPanelAltBrush");
        root.Children.Add(_toolbar);
        Content = root;
        new WindowInteropHelper(this).Owner = surface.Owner;
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(Hook);
            ApplyInputMode();
        };
        Closed += (_, _) => { _closed = true; _source?.RemoveHook(Hook); _source = null; Complete(null); };
        Deactivated += (_, _) => { if (IsPicking) Complete(null); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsPicking) { e.Handled = true; Complete(null); }
        };
        MouseMove += (_, e) =>
        {
            if (!IsPicking) return;
            _cursor = Normalize(e.GetPosition(this));
            RefreshDrawing();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!IsPicking || e.Handled || IsToolbarSource(e.OriginalSource)) return;
            _start = Normalize(e.GetPosition(this));
            if (_start is null) return;
            _cursor = _start; _dragging = true;
            _downTime = Stopwatch.GetTimestamp();
            CaptureMouse(); e.Handled = true; RefreshDrawing();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!IsPicking || !_dragging) return;
            _dragging = false; ReleaseMouseCapture(); e.Handled = true;
            var end = Normalize(e.GetPosition(this));
            if (_start is not { } start || end is null) { _start = null; RefreshDrawing(); return; }
            var entry = _pick!.Entry;
            var endpoint = ConstrainEnd(entry.Action, start, end.Value);
            if (entry.IsSwipe && Math.Abs(endpoint.X - start.X) + Math.Abs(endpoint.Y - start.Y) < .005)
            { _start = null; RefreshDrawing(); return; }
            var deviceStart = Surface.ToDevice(start.X, start.Y);
            var deviceEnd = Surface.ToDevice(endpoint.X, endpoint.Y);
            Complete(entry with { X = deviceStart.X, Y = deviceStart.Y,
                EndX = deviceEnd.X, EndY = deviceEnd.Y, DeviceCoordinates = true,
                DurationMs = entry.IsSwipe ? (int)Math.Clamp(Stopwatch.GetElapsedTime(_downTime).TotalMilliseconds, 50, 10000)
                    : entry.DurationMs });
        };
        LostMouseCapture += (_, _) => { if (_dragging) { _dragging = false; _start = null; RefreshDrawing(); } };
    }

    private bool IsToolbarSource(object source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, _toolbar)) return true;
        return false;
    }

    internal static (double X, double Y) ConstrainEnd(MappedTouchAction action,
        (double X, double Y) start, (double X, double Y) end) => action switch
    {
        MappedTouchAction.SwipeUp => (start.X, Math.Min(start.Y, end.Y)),
        MappedTouchAction.SwipeDown => (start.X, Math.Max(start.Y, end.Y)),
        MappedTouchAction.SwipeLeft => (Math.Min(start.X, end.X), start.Y),
        MappedTouchAction.SwipeRight => (Math.Max(start.X, end.X), start.Y),
        _ => end,
    };

    internal void Update(MappingPreviewSurface surface, IReadOnlyList<KeyboardMappingEntry> entries)
    {
        var cancel = IsPicking && Surface != surface;
        Surface = surface;
        _entries = entries;
        if (cancel) Complete(null);
        if (_closed) return;
        if (!UpdateBounds()) { Hide(); if (IsPicking) Complete(null); return; }
        if (!IsVisible) Show();
        if (!IsPicking && GetCursorPos(out var cursor))
        {
            ScreenToClient(Surface.Window, ref cursor);
            _cursor = PreviewCoordinateMapper.Normalize(cursor.X, cursor.Y, _pixelWidth, _pixelHeight, Surface.Width, Surface.Height);
        }
        RefreshDrawing();
    }

    internal void BeginPick(MappingPositionPick pick)
    {
        _pick = pick; _start = _cursor = null;
        _toolbar.Visibility = Visibility.Visible;
        Cursor = Cursors.Cross;
        ApplyInputMode();
        Show(); Activate(); Focus();
        RefreshDrawing();
    }

    internal void CancelPick() => Complete(null);

    private void Complete(KeyboardMappingEntry? entry)
    {
        if (_pick is not { } pick) return;
        _pick = null; _dragging = false; _start = _cursor = null;
        ReleaseMouseCapture();
        _toolbar.Visibility = Visibility.Collapsed; Cursor = Cursors.Arrow;
        ApplyInputMode(); RefreshDrawing();
        pick.Completed(entry);
    }

    private (double X, double Y)? Normalize(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        return PreviewCoordinateMapper.Normalize(point.X * _pixelWidth / ActualWidth,
            point.Y * _pixelHeight / ActualHeight, _pixelWidth, _pixelHeight, Surface.Width, Surface.Height);
    }

    private Point Project(double x, double y)
    {
        var point = PreviewCoordinateMapper.Project(x, y, _pixelWidth, _pixelHeight, Surface.Width, Surface.Height);
        return new(point.X * ActualWidth / Math.Max(1, _pixelWidth), point.Y * ActualHeight / Math.Max(1, _pixelHeight));
    }

    private bool UpdateBounds()
    {
        if (!IsWindowVisible(Surface.Window) || IsIconic(Surface.Owner) || !GetClientRect(Surface.Window, out var rect)) return false;
        var origin = new NativePoint();
        if (!ClientToScreen(Surface.Window, ref origin)) return false;
        _pixelWidth = rect.Right - rect.Left; _pixelHeight = rect.Bottom - rect.Top;
        if (_pixelWidth <= 0 || _pixelHeight <= 0) return false;
        // Native pixels avoid mixed-monitor DIP origin errors. WPF handles the
        // overlay's per-monitor DPI after Windows positions the owned HWND.
        var handle = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(handle, 0, origin.X, origin.Y, _pixelWidth, _pixelHeight, 0x0014);
        return true;
    }

    private void ApplyInputMode()
    {
        IsHitTestVisible = IsPicking;
        // Layered HWNDs hit-test transparent pixels in the compositor. A
        // one-alpha fill makes the video area pickable without dimming it.
        Background = IsPicking ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent;
        var handle = _source?.Handle ?? 0;
        if (handle == 0) return;
        const long mask = 0x20 | 0x08000000;
        var style = GetWindowLongPtrW(handle, -20).ToInt64();
        SetWindowLongPtrW(handle, -20, (nint)(IsPicking ? style & ~mask : style | mask));
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0084 && !IsPicking) { handled = true; return -1; }
        return 0;
    }

    private void RefreshDrawing()
    {
        if (_pick is { } pick)
        {
            _hint.Text = LocalizationService.Get(_dragging && pick.Entry.IsSwipe ? "WizardRecording" :
                pick.Entry.IsSwipe ? "MappingPickDrag" : "MappingPickClick");
        }
        _visual.InvalidateVisual();
    }

    private sealed class MappingVisual(KeyboardMappingOverlayWindow overlay) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var owner = overlay;
            var accent = owner.TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
            var background = owner.TryFindResource("PreviewPanelAltBrush") as Brush ?? Brushes.DimGray;
            var foreground = owner.TryFindResource("PreviewTextBrush") as Brush ?? Brushes.White;
            var scale = Math.Clamp(Math.Min(ActualWidth, ActualHeight) / 400, .8, 1.25);
            foreach (var entry in owner._entries.Where(e => e.Enabled && e.NeedsPosition))
            {
                var point = entry.DeviceCoordinates ? owner.Surface.ToPreview(entry.X, entry.Y) : (entry.X, entry.Y);
                var start = owner.Project(point.Item1, point.Item2);
                var hovered = owner._cursor is { } cursor && (owner.Project(cursor.X, cursor.Y) - start).Length < 24 * scale;
                var selected = entry.Id == owner._pick?.Entry.Id;
                dc.PushOpacity(selected ? 1 : owner.IsPicking ? .45 : hovered ? 1 : .8);
                if (entry.IsSwipe)
                {
                    var endpoint = entry.EndPoint;
                    if (entry.DeviceCoordinates) endpoint = owner.Surface.ToPreview(endpoint.X, endpoint.Y);
                    DrawPath(dc, start, owner.Project(endpoint.X, endpoint.Y), accent, scale);
                }
                if (entry.IsContinuous)
                {
                    var radius = (entry.Action == MappedTouchAction.Joystick ? entry.Joystick.Radius : entry.RelativeDrag.Radius) * Math.Min(ActualWidth, ActualHeight);
                    dc.DrawEllipse(null, new Pen(accent, 1), start, radius, radius);
                }
                foreach (var target in entry.Action == MappedTouchAction.CycleTargets ? entry.Targets : [])
                {
                    var preview = entry.DeviceCoordinates ? owner.Surface.ToPreview(target.X, target.Y) : (target.X, target.Y);
                    var location = owner.Project(preview.Item1, preview.Item2);
                    dc.DrawEllipse(null, new Pen(accent, 1), location, 6 * scale, 6 * scale);
                }
                var label = new FormattedText(KeyboardMappingKeys.DisplayInput(entry), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12 * scale, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var width = Math.Min(ActualWidth, Math.Max(28 * scale, label.Width + 14 * scale));
                var height = 25 * scale;
                var rect = new Rect(Math.Clamp(start.X - width / 2, 0, Math.Max(0, ActualWidth - width)),
                    Math.Clamp(start.Y - height - 5 * scale, 0, Math.Max(0, ActualHeight - height)), width, height);
                dc.DrawRoundedRectangle(background, new Pen(accent, selected ? 2.5 : hovered ? 2 : 1), rect, 6 * scale, 6 * scale);
                dc.DrawText(label, new Point(rect.X + (rect.Width - label.Width) / 2, rect.Y + (rect.Height - label.Height) / 2));
                dc.DrawEllipse(accent, null, start, 2.5 * scale, 2.5 * scale);
                dc.Pop();
            }
            if (owner.IsPicking && owner._cursor is { } current)
            {
                var point = owner.Project(current.X, current.Y);
                var pen = new Pen(accent, 1.5);
                dc.DrawEllipse(null, pen, point, 8, 8);
                dc.DrawLine(pen, new Point(point.X - 17, point.Y), new Point(point.X + 17, point.Y));
                dc.DrawLine(pen, new Point(point.X, point.Y - 17), new Point(point.X, point.Y + 17));
                if (owner._start is { } start)
                {
                    var end = ConstrainEnd(owner._pick!.Entry.Action, start, current);
                    DrawPath(dc, owner.Project(start.X, start.Y), owner.Project(end.X, end.Y), accent, 1);
                }
            }
        }
        private static void DrawPath(DrawingContext dc, Point start, Point end, Brush brush, double scale)
        {
            dc.DrawLine(new Pen(brush, 1.5 * scale), start, end);
            dc.DrawEllipse(null, new Pen(brush, 1.5 * scale), end, 4 * scale, 4 * scale);
            var delta = end - start;
            if (delta.Length < 10) return;
            delta.Normalize();
            var perpendicular = new Vector(-delta.Y, delta.X);
            dc.DrawLine(new Pen(brush, 1.5 * scale), end, end - delta * 9 * scale + perpendicular * 4 * scale);
            dc.DrawLine(new Pen(brush, 1.5 * scale), end, end - delta * 9 * scale - perpendicular * 4 * scale);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
}
