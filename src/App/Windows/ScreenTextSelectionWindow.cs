using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

internal readonly record struct ScreenTextPreviewBounds(Rect Picture, Rect Frame);

internal sealed class ScreenTextSelectionWindow : Window
{
    private readonly nint _owner;
    private readonly Func<ScreenTextPreviewBounds> _previewBounds;
    private readonly Func<bool> _isCurrent;
    private readonly Action<string> _copyText;
    private readonly DispatcherTimer _timer;
    private readonly Border _toolbar;
    private readonly Border _statusCard;
    private readonly TextBlock _status;
    private readonly Button _copy;
    private readonly Button _selectAll;
    private readonly ScreenTextSelectionSurface _surface;
    private nint _handle;
    private Rect _lastBounds = Rect.Empty;
    private bool _closing;
    private bool _closed;
    private bool _ready;
    private int _copyVersion;

    internal ScreenTextSelectionWindow(nint owner, BitmapSource image, Func<ScreenTextPreviewBounds> previewBounds,
        Func<bool> isCurrent, Action<string> copyText, Brush? screenMask = null)
    {
        _owner = owner;
        _previewBounds = previewBounds;
        _isCurrent = isCurrent;
        _copyText = copyText;
        Title = LocalizationService.Get("CopyScreenText");
        Style = new Style(typeof(Window));
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Width = Height = 1;
        _surface = new ScreenTextSelectionSurface(image)
        {
            Focusable = true, Cursor = Cursors.Arrow, OpacityMask = screenMask,
        };
        AutomationProperties.SetAutomationId(_surface, "ScreenTextSelectionSurface");
        AutomationProperties.SetName(_surface, Title);
        AutomationProperties.SetHelpText(_surface, LocalizationService.Get("ScreenTextSelectionHelp"));
        _surface.SelectionChanged += UpdateSelection;

        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "PreviewTextBrush");
        _status.SetResourceReference(TextBlock.FontSizeProperty, "CaptionFontSize");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _statusCard = FloatingCard(_status, new Thickness(10, 4, 10, 4));
        _statusCard.MaxWidth = 238;
        _statusCard.IsHitTestVisible = false;
        SetStatus(LocalizationService.Get("ScreenTextReading"));

        _copy = ToolbarButton("ScreenTextCopySelection", "ScreenTextCopySelection",
            "M5,5 H13 V13 H5 Z M3,11 H1 V1 H11 V3", primary: true);
        _copy.IsEnabled = false;
        _copy.Click += async (_, _) => await CopySelectedAsync();
        _selectAll = ToolbarButton("ScreenTextSelectAll", "ScreenTextSelectAll",
            "M5,1 H1 V5 M9,1 H13 V5 M13,9 V13 H9 M5,13 H1 V9 M4,5 H10 M4,8 H10");
        _selectAll.IsEnabled = false;
        _selectAll.Click += (_, _) => _surface.SelectAll();
        var close = ToolbarButton("Close", "ScreenTextSelectionClose", "M2,2 L12,12 M12,2 L2,12", iconOnly: true);
        close.Click += (_, _) => Dismiss();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_selectAll);
        buttons.Children.Add(_copy);
        var separator = new Border
        {
            Width = 1, Height = 16, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };
        separator.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
        buttons.Children.Add(separator);
        buttons.Children.Add(close);
        _toolbar = FloatingCard(buttons, new Thickness(4));
        var layout = new Canvas { SnapsToDevicePixels = true };
        layout.Children.Add(_surface);
        layout.Children.Add(_statusCard);
        layout.Children.Add(_toolbar);
        Content = layout;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => { if (!_isCurrent() || !UpdatePlacement()) Dismiss(); };
        new WindowInteropHelper(this).Owner = owner;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            _ = UpdatePlacement();
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsActive) Dismiss();
        });
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
        };
    }

    internal ScreenTextSelectionSurface Surface => _surface;

    internal bool ShowForPreview()
    {
        if (!_isCurrent() || _previewBounds().Picture.IsEmpty)
        {
            Dismiss();
            return false;
        }
        Show();
        if (_closed) return false;
        if (!UpdatePlacement())
        {
            Dismiss();
            return false;
        }
        Activate();
        _surface.Focus();
        _timer.Start();
        return true;
    }

    internal void Dismiss()
    {
        if (!_closing && !_closed) Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
        if (e.Cancel) _closing = false;
    }

    internal void SetPage(ScreenTextPage page)
    {
        _ready = page.Words.Count != 0;
        _selectAll.IsEnabled = _ready;
        _surface.Cursor = _ready ? Cursors.IBeam : Cursors.Arrow;
        _surface.SetPage(page);
        if (!_ready) SetStatus(LocalizationService.Get("ScreenTextEmpty"));
    }

    internal void SetStatus(string? text)
    {
        _status.Text = text ?? "";
        _statusCard.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (!_lastBounds.IsEmpty) _ = UpdatePlacement();
    }

    private void UpdateSelection()
    {
        if (!_ready) return;
        var text = _surface.Selection;
        _copy.IsEnabled = !string.IsNullOrWhiteSpace(text);
        SetStatus(_copy.IsEnabled
            ? LocalizationService.Format("ScreenTextSelectionCount", new StringInfo(text).LengthInTextElements)
            : null);
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape || e.Key == Key.System && e.SystemKey == Key.C && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            e.Handled = true;
            Dismiss();
        }
        else if (_ready && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.A)
            {
                e.Handled = true;
                _surface.SelectAll();
            }
            else if (e.Key == Key.C)
            {
                e.Handled = true;
                if (!e.IsRepeat) await CopySelectedAsync();
            }
        }
    }

    private async Task CopySelectedAsync()
    {
        var text = _surface.Selection;
        if (string.IsNullOrWhiteSpace(text)) return;
        var version = ++_copyVersion;
        for (var attempt = 0; !_closing && !_closed && _isCurrent() && version == _copyVersion; attempt++)
        {
            try
            {
                _copyText(text);
                SetStatus(LocalizationService.Format("ScreenTextCopiedSelection", new StringInfo(text).LengthInTextElements));
                return;
            }
            catch (ExternalException error) when (error.HResult == unchecked((int)0x800401D0) && attempt < 3)
            {
                await Task.Delay(50);
            }
            catch (ExternalException)
            {
                SetStatus(LocalizationService.Get("ScreenTextClipboardFailed"));
                return;
            }
        }
    }

    private bool UpdatePlacement()
    {
        if (_handle == 0 || _closing || _closed) return false;
        var preview = _previewBounds();
        if (preview.Picture.IsEmpty) return false;
        var dpi = GetDpiForWindow(_owner);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var area = GetMonitorInfoW(MonitorFromWindow(_owner, 2), ref monitor)
            ? new Rect(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left,
                monitor.Work.Bottom - monitor.Work.Top)
            : preview.Frame;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarSize = PixelSize(_toolbar.DesiredSize, scale);
        var picture = RoundBounds(preview.Picture);
        var toolbar = RoundBounds(PreviewCoordinateMapper.PlaceOverlayToolbar(
            preview.Frame, preview.Frame, area, toolbarSize, 8 * scale));
        // Reserve the feedback slot so counts and clipboard feedback never move
        // the window while the pointer is captured during a selection.
        var statusSlot = PlaceStatusSlot(preview.Frame, toolbar, area, scale);
        var bounds = Rect.Union(Rect.Union(picture, toolbar), statusSlot);
        if (bounds != _lastBounds && !SetWindowPos(_handle, 0, (int)bounds.Left, (int)bounds.Top,
                (int)bounds.Width, (int)bounds.Height, 0x0014))
            return false;
        _lastBounds = bounds;
        var windowDpi = VisualTreeHelper.GetDpi(this);
        Place(_surface, picture, bounds, windowDpi);
        _surface.Width = picture.Width / windowDpi.DpiScaleX;
        _surface.Height = picture.Height / windowDpi.DpiScaleY;
        Place(_toolbar, toolbar, bounds, windowDpi);
        if (_statusCard.Visibility == Visibility.Visible)
        {
            _statusCard.Measure(new Size(238, double.PositiveInfinity));
            var size = PixelSize(_statusCard.DesiredSize, scale);
            var status = new Rect(statusSlot.Left + Math.Round((statusSlot.Width - size.Width) / 2),
                statusSlot.Top + Math.Round((statusSlot.Height - size.Height) / 2), size.Width, size.Height);
            Place(_statusCard, status, bounds, windowDpi);
        }
        return true;
    }

    private static Rect PlaceStatusSlot(Rect preview, Rect toolbar, Rect area, double scale)
    {
        var size = PixelSize(new Size(238, 36), scale);
        var gap = Math.Ceiling(8 * scale);
        var y = toolbar.Top + (toolbar.Height - size.Height) / 2;
        var x = Math.Clamp(toolbar.Left + (toolbar.Width - size.Width) / 2, area.Left,
            Math.Max(area.Left, area.Right - size.Width));
        Rect[] candidates =
        [
            new(toolbar.Right + gap, y, size.Width, size.Height),
            new(toolbar.Left - gap - size.Width, y, size.Width, size.Height),
            new(x, toolbar.Top - gap - size.Height, size.Width, size.Height),
            new(x, toolbar.Bottom + gap, size.Width, size.Height),
        ];
        foreach (var candidate in candidates)
            if (area.Contains(candidate) && !preview.IntersectsWith(candidate)) return RoundBounds(candidate);
        var inside = toolbar.Top >= preview.Top + preview.Height / 2
            ? preview.Top + gap : preview.Bottom - gap - size.Height;
        return RoundBounds(new Rect(x, Math.Clamp(inside,
            area.Top, Math.Max(area.Top, area.Bottom - size.Height)), size.Width, size.Height));
    }

    private static Size PixelSize(Size size, double scale) =>
        new(Math.Ceiling(size.Width * scale), Math.Ceiling(size.Height * scale));

    private static Rect RoundBounds(Rect rect) => new(
        new Point(Math.Round(rect.Left), Math.Round(rect.Top)),
        new Point(Math.Round(rect.Right), Math.Round(rect.Bottom)));

    private static void Place(FrameworkElement element, Rect rect, Rect window, DpiScale dpi)
    {
        Canvas.SetLeft(element, (rect.Left - window.Left) / dpi.DpiScaleX);
        Canvas.SetTop(element, (rect.Top - window.Top) / dpi.DpiScaleY);
    }

    private static Border FloatingCard(UIElement content, Thickness padding)
    {
        var card = new Border
        {
            Child = content, Padding = padding,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
        };
        card.SetResourceReference(Border.BackgroundProperty, "PreviewPanelAltBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "PreviewBorderBrush");
        return card;
    }

    private static Button ToolbarButton(string key, string id, string geometry, bool primary = false, bool iconOnly = false)
    {
        var name = LocalizationService.Get(key);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Path
        {
            Data = Geometry.Parse(geometry), Width = 13, Height = 13, Stretch = Stretch.Uniform,
            StrokeThickness = 1.35, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetBinding(Shape.StrokeProperty, new Binding(nameof(Button.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
        });
        content.Children.Add(icon);
        if (!iconOnly)
            content.Children.Add(new TextBlock
            {
                Text = name, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        var button = new Button
        {
            Content = content, MinWidth = 29, MinHeight = 28, Height = 28,
            Padding = new Thickness(iconOnly ? 5 : 8, 1, iconOnly ? 5 : 8, 1), ToolTip = name,
        };
        button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "GhostButton");
        button.SetResourceReference(FontSizeProperty, "CaptionFontSize");
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, name);
        return button;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { internal int Size; internal PixelRect Monitor, Work; internal uint Flags; }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after,
        int x, int y, int width, int height, uint flags);
}
