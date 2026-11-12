using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunCompleteWindowResizeTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        try
        {
            vm.HideInterfaceDescriptions = false;
            SetKeyboardField(vm, "_driverState", "驱动叠加：已启用保守 USB 模式");
            KeyboardCall(vm, "OnPropertyChanged", nameof(MainViewModel.DriverState));
            InteractionAssert(window.MinWidth <= 480 && window.MinWidth > 0 && window.ResizeMode == ResizeMode.CanResize,
                "Complete mode still prevents useful horizontal resizing.");
            var hwnd = new WindowInteropHelper(window).Handle;
            var dpi = RepairGetDpiForWindow(hwnd) / 96d;
            var memory = Marshal.AllocHGlobal(40);
            try
            {
                Marshal.Copy(new byte[40], 0, memory, 40);
                RepairSendMessage(hwnd, 0x0024, 0, memory);
                var nativeMinimum = Marshal.ReadInt32(memory, 24);
                InteractionAssert(nativeMinimum > 0 && nativeMinimum / dpi < 510,
                    $"The native tracking limit still blocks narrowing: {nativeMinimum / dpi:F1} DIP.");
            }
            finally { Marshal.FreeHGlobal(memory); }
            var left = (FrameworkElement)window.FindName("LeftPanelHost");
            var right = (FrameworkElement)window.FindName("ControlPanel");
            var center = (FrameworkElement)window.FindName("CenterPanel");
            void Layout() { DrainDispatcher(); window.UpdateLayout(); }
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            var header = (Grid)window.FindName("HeaderPanel");
            var heading = (FrameworkElement)window.FindName("WorkspaceHeading");
            var subtitle = (FrameworkElement)window.FindName("WorkspaceSubtitle");
            double? referenceHeaderHeight = null;
            double? referenceContentTop = null;
            foreach (var (showLeft, showRight) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                window.Width = 1440; Layout();
                KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, showLeft ? "Devices" : "None"));
                KeyboardCall(window, "SetSettingsPanelVisible", showRight);
                FinishWorkspaceReveal(window); Layout();
                foreach (var width in showLeft || showRight ? new[] { 1100d, 960d, 800d, 960d, 1280d } : new[] { 1100d, 800d, 640d, 480d, 640d, 960d, 1280d })
                {
                    // Exercise the actual HWND resize, retaining production minimums.
                    InteractionAssert(PerfSetWindowPos(hwnd, 0, 0, 0, (int)Math.Round(width * dpi),
                        (int)Math.Round(window.ActualHeight * dpi), 0x0016), "Native horizontal resize failed.");
                    Layout(); AdvanceDispatcher(TimeSpan.FromMilliseconds(30)); Layout();
                    InteractionAssert(Math.Abs(window.ActualWidth - width) < 2 && window.MinWidth <= 800 && center.ActualWidth >= 155,
                        $"Native width bounced back or squeezed the preview: request={width}, actual={window.ActualWidth}, preview={center.ActualWidth}.");
                    InteractionAssert(left.IsVisible == showLeft && right.IsVisible == showRight,
                        "Horizontal resizing unexpectedly hid an open workspace card.");
                    if (showLeft && showRight && width <= 960)
                        InteractionAssert(left.ActualWidth >= 190 && right.ActualWidth >= 230,
                            $"The source card consumed the settings viewport: {left.ActualWidth:F1}/{right.ActualWidth:F1}.");
                    var actions = (FrameworkElement)window.FindName("WindowActions");
                    var capture = (FrameworkElement)window.FindName("CaptureActionButton");
                    var driver = (FrameworkElement)window.FindName("DetectionStatus");
                    InteractionAssert(Grid.GetRow(capture) == 0 && Grid.GetColumn(capture) == 3 && Grid.GetRow(driver) == 0 &&
                        Grid.GetColumn(driver) == 1 && Grid.GetColumnSpan(heading) == 1 && header.RowDefinitions.Count == 1 &&
                        Math.Abs(Bounds(capture).Top + capture.ActualHeight / 2 - Bounds(actions).Top - actions.ActualHeight / 2) < 1,
                        "Narrowing reparented the capture action or driver status into a second header row.");
                    referenceHeaderHeight ??= header.ActualHeight;
                    referenceContentTop ??= Bounds((FrameworkElement)window.FindName("MainContentGrid")).Top;
                    InteractionAssert(Math.Abs(header.ActualHeight - referenceHeaderHeight.Value) < 1 && subtitle.IsVisible &&
                        Math.Abs(Bounds((FrameworkElement)window.FindName("MainContentGrid")).Top - referenceContentTop.Value) < 1,
                        "Crossing the narrow breakpoint moved the workspace down or hid the subtitle.");
                    var overlap = Rect.Intersect(Bounds(actions), Bounds(capture));
                    InteractionAssert(Bounds(actions).Right <= window.ActualWidth + 1 && Bounds(capture).Left >= 0 &&
                        (overlap.IsEmpty || overlap.Width < 1 || overlap.Height < 1),
                        "Window controls and capture action overlap after narrowing.");
                    overlap = Rect.Intersect(Bounds(driver), Bounds(capture));
                    InteractionAssert(overlap.IsEmpty || overlap.Width < 1 || overlap.Height < 1,
                        "Driver status overlaps the capture action in the compact header.");
                    var groups = ((Panel)window.FindName("StatisticsItems")).Children.OfType<FrameworkElement>().Select(Bounds).ToArray();
                    InteractionAssert(groups.Max(b => b.Top) - groups.Min(b => b.Top) < 1,
                        "Narrow complete mode wrapped the status bar.");
                    if (width == 800)
                        SaveWindowRender(window, Path.Combine(output, $"narrow-left-{showLeft}-right-{showRight}.png"));
                    if (width == 480)
                        SaveWindowRender(window, Path.Combine(output, "minimum-480.png"));
                }
            }
            // Reapplying the mode, full screen and a lightweight roundtrip must
            // retain the relaxed complete-mode limit and prior manual width.
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "None"));
            KeyboardCall(window, "SetSettingsPanelVisible", false); FinishWorkspaceReveal(window);
            window.Width = 900; Layout();
            KeyboardCall(window, "ApplyApplicationDisplayMode"); Layout();
            InteractionAssert(window.ActualWidth < 910 && window.MinWidth <= 800, "Applying complete mode restored the old 1280 DIP minimum.");
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight; Layout();
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete; Layout();
            InteractionAssert(Math.Abs(window.ActualWidth - 900) < 2 && window.MinWidth <= 800,
                "A display-mode roundtrip lost the user's narrow complete-mode width.");
            KeyboardCall(window, "ToggleFullScreen"); Layout();
            KeyboardCall(window, "ToggleFullScreen"); Layout();
            InteractionAssert(Math.Abs(window.ActualWidth - 900) < 2 && window.MinWidth <= 800,
                "Full-screen exit restored the old complete-mode minimum.");
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP", "zh-TW", "zh-HK" })
            foreach (var scale in new[] { 1d, 1.5 })
            {
                LocalizationService.SetLanguage(language);
                IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, scale, false);
                window.Width = 800; Layout();
                KeyboardCall(window, "SetSettingsPanelVisible", true); FinishWorkspaceReveal(window); Layout();
                foreach (var category in new[] { "Application", "Projection", "Control" })
                {
                    ((RadioButton)window.FindName(category + "SettingsTab")).IsChecked = true; Layout();
                    var scroll = (ScrollViewer)window.FindName(category + "SettingsScrollViewer");
                    scroll.ScrollToTop(); Layout();
                    var tab = (FrameworkElement)window.FindName(category + "SettingsTab");
                    InteractionAssert(tab.IsVisible && scroll.ActualWidth > 240 && scroll.ScrollableWidth < 1 && Bounds(tab).Right <= Bounds(right).Right + 1,
                        $"Settings lose usable width at 800 DIP: {language}/{scale}/{category}.");
                }
                var actions = (FrameworkElement)window.FindName("WindowActions");
                var capture = (FrameworkElement)window.FindName("CaptureActionButton");
                InteractionAssert(Math.Abs(Bounds(capture).Top + capture.ActualHeight / 2 - Bounds(actions).Top - actions.ActualHeight / 2) < 1 &&
                    Bounds(capture).Left >= Bounds(heading).Right - 1 && Bounds(capture).Right <= Bounds(actions).Left + 1 &&
                    subtitle.IsVisible && header.ActualHeight < 100 * scale,
                    $"Header controls moved, overlapped or wrapped at {language}/{scale}.");
                var completeHeaderHeight = header.ActualHeight;
                var completeContentTop = Bounds((FrameworkElement)window.FindName("MainContentGrid")).Top;
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight; Layout();
                InteractionAssert(!subtitle.IsVisible && header.RowDefinitions.Count == 1 &&
                    Math.Abs(Bounds(capture).Top + capture.ActualHeight / 2 - Bounds(actions).Top - actions.ActualHeight / 2) < 1,
                    "Lightweight mode did not hide the subtitle or moved header actions into different rows.");
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete; Layout();
                InteractionAssert(subtitle.IsVisible && Math.Abs(header.ActualHeight - completeHeaderHeight) < 1 &&
                    Math.Abs(Bounds((FrameworkElement)window.FindName("MainContentGrid")).Top - completeContentTop) < 1,
                    "Complete mode failed to restore its description preference and header geometry.");
                SaveWindowRender(window, Path.Combine(output, $"stable-header-{language}-{scale:0.0}.png"));
                KeyboardCall(window, "SetSettingsPanelVisible", false); FinishWorkspaceReveal(window);
            }
            Console.WriteLine("PASS complete resize: real HWND shrink to 480 DIP without sidebars, four sidebar combinations at 800 DIP, balanced narrow cards, usable preview, single-row bars, stable header/content positions, window controls, mode/fullscreen restore, five languages at 100/150% text.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }
}
