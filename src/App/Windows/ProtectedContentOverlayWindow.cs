using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace IPhoneMirror.App.Windows;

internal sealed class ProtectedContentOverlayWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const nint HtTransparent = -1;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private readonly nint _owner;
    private readonly DispatcherTimer _positionTimer;
    private HwndSource? _source;
    private TextBlock _titleText = null!;
    private TextBlock _bodyText = null!;
    private TextBlock _audioText = null!;

    private ProtectedContentOverlayWindow(nint owner, string audioDisplay)
    {
        _owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(BackgroundProperty, "PreviewChromeBrush");
        Content = BuildContent(audioDisplay);
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _positionTimer.Tick += (_, _) => UpdateBounds();
        Closed += (_, _) => _positionTimer.Stop();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    internal static ProtectedContentOverlayWindow ShowFor(nint owner,
        string audioDisplay)
    {
        var overlay = new ProtectedContentOverlayWindow(owner, audioDisplay);
        var helper = new WindowInteropHelper(overlay) { Owner = owner };
        overlay.SourceInitialized += (_, _) =>
        {
            var style = GetWindowLongPtrW(helper.Handle, GwlExStyle).ToInt64();
            _ = SetWindowLongPtrW(helper.Handle, GwlExStyle,
                (nint)(style | WsExTransparent | WsExNoActivate));
            overlay.UpdateBounds();
        };
        overlay.Show();
        overlay._positionTimer.Start();
        return overlay;
    }

    internal void UpdateAudioDisplay(string audioDisplay)
    {
        _audioText.Text = audioDisplay;
    }

    private FrameworkElement BuildContent(string audioDisplay)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 480,
            Margin = new Thickness(28),
        };
        var icon = new Border
        {
            Width = 76,
            Height = 76,
            CornerRadius = new CornerRadius(38),
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(1),
        };
        icon.SetResourceReference(Border.BackgroundProperty, "PreviewPanelAltBrush");
        icon.SetResourceReference(Border.BorderBrushProperty, "PreviewBorderBrush");
        var iconText = new SymbolIcon
        {
            Symbol = SymbolRegular.Warning20,
            FontSize = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconText.SetResourceReference(SymbolIcon.ForegroundProperty, "PreviewTextBrush");
        icon.Child = iconText;
        panel.Children.Add(icon);

        _titleText = new TextBlock
        {
            Text = LocalizationService.Get("CaptureVideoProtectedTitle"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        _titleText.SetResourceReference(TextBlock.ForegroundProperty, "PreviewTextBrush");
        panel.Children.Add(_titleText);

        _bodyText = new TextBlock
        {
            Text = LocalizationService.Get(
                "CaptureVideoProtectedNoticeProtection"),
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _bodyText.SetResourceReference(TextBlock.ForegroundProperty, "PreviewMutedTextBrush");
        panel.Children.Add(_bodyText);

        var audioBadge = new Border
        {
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        audioBadge.SetResourceReference(Border.BackgroundProperty,
            "PreviewPanelAltBrush");
        audioBadge.SetResourceReference(Border.BorderBrushProperty,
            "PreviewBorderBrush");
        var audioPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var audioIcon = new SymbolIcon
        {
            Symbol = SymbolRegular.Speaker220,
            FontSize = 14,
            Margin = new Thickness(0, 0, 5, 0),
        };
        audioIcon.SetResourceReference(SymbolIcon.ForegroundProperty,
            "PreviewMutedTextBrush");
        audioPanel.Children.Add(audioIcon);
        _audioText = new TextBlock
        {
            Text = audioDisplay,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _audioText.SetResourceReference(TextBlock.ForegroundProperty,
            "PreviewMutedTextBrush");
        audioPanel.Children.Add(_audioText);
        audioBadge.Child = audioPanel;
        panel.Children.Add(audioBadge);

        var root = new Border { Child = panel };
        root.SetResourceReference(Border.BackgroundProperty, "PreviewChromeBrush");
        return root;
    }

    private void UpdateBounds()
    {
        if (_owner == 0 || !IsWindowVisible(_owner) || IsIconic(_owner) ||
            !GetWindowRect(_owner, out var rect))
        {
            Hide();
            return;
        }
        var dpi = GetDpiForWindow(_owner);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        Left = rect.Left / scale;
        Top = rect.Top / scale;
        Width = Math.Max(1, (rect.Right - rect.Left) / scale);
        Height = Math.Max(1, (rect.Bottom - rect.Top) / scale);
        if (!IsVisible) Show();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _titleText.Text = LocalizationService.Get("CaptureVideoProtectedTitle");
        _bodyText.Text = LocalizationService.Get(
            "CaptureVideoProtectedNoticeProtection");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource?)PresentationSource.FromVisual(this);
        _source?.AddHook(WindowMessageHook);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam,
        nint lParam, ref bool handled)
    {
        if (message != WmNcHitTest) return 0;
        handled = true;
        return HtTransparent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
}
