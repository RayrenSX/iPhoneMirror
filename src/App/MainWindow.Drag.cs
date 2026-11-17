using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private (Point Screen, double HorizontalRatio, double TopOffset)? _maximizedWorkspaceDrag;

    private void OnWorkspaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A captured control remains the event owner even when the pointer
        // moves over shell space (for example, while a dropdown is open).
        if (e.ChangedButton != MouseButton.Left ||
            !IsWorkspaceDragSource(e.OriginalSource as DependencyObject)) return;
        // Visual hit testing includes disabled controls, whose mouse events
        // otherwise fall through to the empty header behind them.
        var source = VisualTreeHelper.HitTest(AppShell, e.GetPosition(AppShell))?.VisualHit ??
            e.OriginalSource as DependencyObject;
        if (!IsWorkspaceDragSource(source)) return;
        e.Handled = true;
        OnHeaderMouseLeftButtonDown(sender, e);
    }

    private void BeginMaximizedWorkspaceDrag(MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Mouse.Capture(AppShell, CaptureMode.Element))
            _maximizedWorkspaceDrag = (PointToScreen(point),
                Math.Clamp(point.X / Math.Max(1, ActualWidth), 0, 1), point.Y);
    }

    private void CancelMaximizedWorkspaceDrag()
    {
        _maximizedWorkspaceDrag = null;
        if (Mouse.Captured == AppShell) AppShell.ReleaseMouseCapture();
    }

    private void OnWorkspaceMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_maximizedWorkspaceDrag is null) return;
        CancelMaximizedWorkspaceDrag();
        e.Handled = true;
    }

    private void OnWorkspaceMouseCaptureLost(object sender, MouseEventArgs e)
    {
        if (Mouse.Captured != AppShell) _maximizedWorkspaceDrag = null;
    }

    private void OnWorkspaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_maximizedWorkspaceDrag is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed || !_isWindowMaximized || _isFullScreen)
        {
            CancelMaximizedWorkspaceDrag();
            return;
        }
        var pointer = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        if (Math.Abs(pointer.X - start.Screen.X) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
            Math.Abs(pointer.Y - start.Screen.Y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY)
            return;

        CancelMaximizedWorkspaceDrag();
        RestoreMaximizedWorkspaceForDrag(pointer, start.HorizontalRatio, start.TopOffset);
        e.Handled = true;
        DragWorkspaceWindow();
    }

    private void RestoreMaximizedWorkspaceForDrag(Point pointer, double horizontalRatio, double topOffset)
    {
        RestoreWindowFromMaximized();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (GetWindowRect(hwnd, out var bounds))
        {
            var scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            var x = (int)Math.Round(pointer.X - width * horizontalRatio);
            var y = (int)Math.Round(pointer.Y - Math.Clamp(topOffset * scale, 0, Math.Max(0, height - 1)));
            var monitor = new MonitorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref monitor))
            {
                x = Math.Clamp(x, monitor.WorkArea.Left, Math.Max(monitor.WorkArea.Left, monitor.WorkArea.Right - width));
                y = Math.Clamp(y, monitor.WorkArea.Top, Math.Max(monitor.WorkArea.Top, monitor.WorkArea.Bottom - height));
            }
            _ = SetWindowPos(hwnd, 0, x, y, width, height, SwpNoZOrder | SwpNoActivate);
            Left = x / scale;
            Top = y / scale;
        }
    }

    private bool IsWorkspaceDragSource(DependencyObject? source)
    {
        if (_isFullScreen) return false;
        for (var current = source; current is not null; current = current switch
        {
            Visual or Visual3D => VisualTreeHelper.GetParent(current),
            FrameworkContentElement content => content.Parent,
            _ => LogicalTreeHelper.GetParent(current),
        })
        {
            if (current is ButtonBase or TextBoxBase or PasswordBox or Selector or
                RangeBase or Thumb or MenuItem or Hyperlink or Wpf.Ui.Controls.NavigationViewItem)
                return false;
            if (current == HeaderPanel || current == DevicePanelHeader ||
                current == MirroringPanelHeader || current == SettingsPanelHeader)
                return true;
            // Card bodies retain their selection, scrolling and preview input.
            // Only their headers and the surrounding shell gaps move the window.
            if (current == LeftPanelHost || current == CenterPanel || current == ControlPanel)
                return false;
            if (current == AppShell) return true;
        }
        return false;
    }
}
