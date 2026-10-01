using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void AuditPreviewStyle(object preview, string outputPath, bool managed)
    {
        var type = preview.GetType();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Field(string name) => type.GetField(name, flags)!.GetValue(preview);
        object? Call(string method, params object[] args) => type.GetMethod(method, flags)!.Invoke(preview, args);
        var handle = (nint)type.GetProperty("Handle", flags)!.GetValue(preview)!;
        var scale = PreviewStyleGetDpi(handle) / 96.0;
        var memory = Marshal.AllocHGlobal(40);
        try
        {
            for (var i = 0; i < 10; ++i) Marshal.WriteInt32(memory, i * 4, 0);
            PreviewStyleSendMessage(handle, 0x0024, 0, memory);
            InteractionAssert(Marshal.ReadInt32(memory, 24) == Math.Ceiling(96 * scale) &&
                Marshal.ReadInt32(memory, 28) == Math.Ceiling(96 * scale), "Native minimum tracking size must honor DPI at 96 DIPs.");
            foreach (var landscape in new[] { false, true })
            {
                Call("SetSourceDimensions", landscape ? 844u : 390u, landscape ? 390u : 844u);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                foreach (var edge in Enumerable.Range(1, 8))
                {
                    PreviewStyleGetWindowRect(handle, out var outer);
                    PreviewStyleGetClientRect(handle, out var client);
                    var frameWidth = outer.Right - outer.Left - client.Right;
                    var frameHeight = outer.Bottom - outer.Top - client.Bottom;
                    var proposed = new PreviewStyleRect { Left = 100, Top = 100, Right = 120, Bottom = 120 };
                    Marshal.StructureToPtr(proposed, memory, false);
                    PreviewStyleSendMessage(handle, 0x0214, (nint)edge, memory);
                    var result = Marshal.PtrToStructure<PreviewStyleRect>(memory);
                    var width = result.Right - result.Left;
                    var height = result.Bottom - result.Top;
                    var aspect = landscape ? 844d / 390 : 390d / 844;
                    InteractionAssert(width >= Math.Floor(96 * scale) && height >= Math.Floor(96 * scale),
                        "Dragging any edge must retain the minimum size.");
                    InteractionAssert(Math.Min(width, height) <= Math.Ceiling(97 * scale) &&
                        Math.Abs((width - frameWidth) - (height - frameHeight) * aspect) < 3,
                        "Small portrait and landscape previews must preserve client aspect ratio.");
                }
            }
            Call("SetSourceDimensions", 390u, 844u);
        }
        finally { Marshal.FreeHGlobal(memory); }

        var parent = (MenuItem)Field("_windowMenuItem")!;
        var styleItem = (MenuItem)Field("_styleItem")!;
        InteractionAssert(parent.Items.Contains(styleItem), "Style belongs under the Window submenu.");
        styleItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var window = (Window)Field("_styleWindow")!;
        AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
        InteractionAssert(window.IsVisible && new WindowInteropHelper(window).Owner == handle,
            "Style settings must be owned by their preview.");
        var slider = (Slider)window.FindName("OpacitySlider");
        var hover = (CheckBox)window.FindName("OpaqueOnHoverCheckBox");
        if (managed)
        {
            foreach (var percent in new[] { 50d, 10d, 100d, 62d })
            {
                slider.Value = percent;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                InteractionAssert((double)Field("_windowOpacity")! == percent / 100,
                    $"Slider must change the current preview immediately: requested={percent}, slider={slider.Value}, stored={Field("_windowOpacity")}, feedback={((TextBlock)window.FindName("FeedbackText")).Text}.");
                if (percent < 100)
                    InteractionAssert(PreviewStyleGetLayeredAttributes(handle, out _, out var alpha, out _) &&
                        alpha == Math.Round(percent / 100 * 255), "Media HWND must receive the native alpha.");
            }
            InteractionAssert(window.Opacity == 1, "Settings must remain fully legible.");
            hover.IsChecked = true;
            // Drive the same transition used by the cursor timer without
            // moving the user's physical pointer during the regression suite.
            ((System.Windows.Threading.DispatcherTimer)Field("_hoverOpacityTimer")!).Stop();
            Call("UpdateHoverState", true);
            InteractionAssert((double)Field("_appliedOpacity")! == 1 && (double)Field("_windowOpacity")! == 0.62,
                "Hover must make the preview opaque without overwriting the saved level.");
            slider.Value = 35;
            InteractionAssert((double)Field("_appliedOpacity")! == 1,
                "Changing the baseline while hovered must retain full opacity.");
            Call("UpdateHoverState", false);
            InteractionAssert((double)Field("_appliedOpacity")! == 0.35,
                "Leaving the preview must restore the latest baseline.");
            Call("UpdateHoverState", true);
            hover.IsChecked = false;
            InteractionAssert((double)Field("_appliedOpacity")! == 0.35,
                "Disabling hover while inside must restore the baseline immediately.");
            slider.Value = 62;
            hover.IsChecked = true;
            ((System.Windows.Threading.DispatcherTimer)Field("_hoverOpacityTimer")!).Stop();
            Call("UpdateHoverState", false);
            Call("ToggleFullScreen");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            Call("ToggleFullScreen");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            InteractionAssert(PreviewStyleGetLayeredAttributes(handle, out _, out var fullscreenAlpha, out _) && fullscreenAlpha == 158,
                "Resizing and fullscreen transitions must retain media opacity.");
        }
        else
        {
            // The shell audit has inert rendering callbacks and no session.
            // A failed native apply must leave the displayed value truthful.
            slider.Value = 50;
            InteractionAssert(slider.Value == 100 && ((TextBlock)window.FindName("FeedbackText")).IsVisible,
                "An unavailable renderer must report failure and restore the slider.");
        }
        SaveWindowRender(window, outputPath);
        Call("ShowStyleSettings");
        InteractionAssert(ReferenceEquals(window, Field("_styleWindow")), "Reopening must reuse the settings window.");
        window.Close();
        if (managed) AuditManagedPreviewPixels(preview, handle);
        Call("ShowStyleSettings");
        var reopened = (Window)Field("_styleWindow")!;
        InteractionAssert(((Slider)reopened.FindName("OpacitySlider")).Value == (managed ? 62 : 100),
            "Reopening must retain this preview's opacity.");
        InteractionAssert(((CheckBox)reopened.FindName("OpaqueOnHoverCheckBox")).IsChecked == managed,
            "Reopening must retain this preview's hover preference.");
        Call("SetBossKeyHidden", true);
        InteractionAssert(Field("_styleWindow") is null, "Boss key must close the settings panel too.");
        Call("SetBossKeyHidden", false);
        Call("ShowStyleSettings");
        // The caller disposes the preview and checks that no owned panel leaks.
    }

    private static void AuditManagedPreviewPixels(object preview, nint handle)
    {
        var work = SystemParameters.WorkArea;
        var dpi = PreviewStyleGetDpi(handle) / 96.0;
        using var background = new HwndSource(new HwndSourceParameters("Preview opacity test background")
        {
            Width = 350, Height = 300, PositionX = (int)((work.Left + 30) * dpi), PositionY = (int)((work.Top + 30) * dpi),
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x88,
        }) { RootVisual = new Border { Background = System.Windows.Media.Brushes.White } };
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = preview.GetType();
        void Call(string method, params object[] args) => type.GetMethod(method, flags)!.Invoke(preview, args);
        PreviewStyleSetWindowPos(handle, (nint)(-1), (int)((work.Left + 50) * dpi),
            (int)((work.Top + 50) * dpi), 180, 200, 0x0010);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(180));
        int Sample()
        {
            PreviewStyleGetWindowRect(handle, out var r);
            var dc = PreviewStyleGetDC(0);
            try { return (int)(PreviewStyleGetPixel(dc, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2) & 255); }
            finally { PreviewStyleReleaseDC(0, dc); }
        }
        Call("UpdateHoverState", true);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
        var opaque = Sample();
        Call("UpdateHoverState", false);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
        var faded = Sample();
        InteractionAssert(opaque < 25 && faded > opaque + 45 && faded < 160,
            $"WPF media content must visibly blend after resizing: opaque={opaque}, faded={faded}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewStyleRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint PreviewStyleSendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint PreviewStyleGetDpi(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool PreviewStyleGetWindowRect(nint hwnd, out PreviewStyleRect rect);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")]
    private static extern bool PreviewStyleGetClientRect(nint hwnd, out PreviewStyleRect rect);
    [DllImport("user32.dll", EntryPoint = "GetLayeredWindowAttributes")]
    private static extern bool PreviewStyleGetLayeredAttributes(nint hwnd, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool PreviewStyleSetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern nint PreviewStyleGetDC(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int PreviewStyleReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll", EntryPoint = "GetPixel")]
    private static extern uint PreviewStyleGetPixel(nint dc, int x, int y);
}
