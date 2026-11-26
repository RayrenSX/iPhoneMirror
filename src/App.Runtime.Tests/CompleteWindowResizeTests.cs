using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Controls;
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
        // Run the wide-layout matrix on the primary work area rather than
        // whichever small secondary monitor currently owns the pointer.
        window.Left = SystemParameters.WorkArea.Left + 40;
        window.Top = SystemParameters.WorkArea.Top + 40;
        DrainDispatcher(); window.UpdateLayout();
        var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        try
        {
            vm.HideInterfaceDescriptions = false;
            SetKeyboardField(vm, "_driverState", "驱动叠加：已启用保守 USB 模式");
            KeyboardCall(vm, "OnPropertyChanged", nameof(MainViewModel.DriverState));
            InteractionAssert(window.MinWidth > 480 && window.ResizeMode == ResizeMode.CanResize,
                "Complete mode does not reserve its initial open source card.");
            var hwnd = new WindowInteropHelper(window).Handle;
            var dpi = RepairGetDpiForWindow(hwnd) / 96d;
            var memory = Marshal.AllocHGlobal(40);
            try
            {
                Marshal.Copy(new byte[40], 0, memory, 40);
                RepairSendMessage(hwnd, 0x0024, 0, memory);
                var nativeMinimum = Marshal.ReadInt32(memory, 24);
                InteractionAssert(Math.Abs(nativeMinimum / dpi - window.MinWidth) < 2,
                    $"The native tracking limit differs from the sidebar minimum: {nativeMinimum / dpi:F1}/{window.MinWidth:F1} DIP.");
            }
            finally { Marshal.FreeHGlobal(memory); }
            var left = (FrameworkElement)window.FindName("LeftPanelHost");
            var right = (FrameworkElement)window.FindName("ControlPanel");
            var center = (FrameworkElement)window.FindName("CenterPanel");
            void Layout()
            {
                var timeout = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
                    { Interval = TimeSpan.FromSeconds(5) };
                timeout.Tick += (_, _) => throw new InvalidOperationException(
                    $"Workspace layout did not settle: width={window.ActualWidth:F2}, minimum={window.MinWidth:F2}, mode={vm.SelectedApplicationDisplayMode}.");
                timeout.Start();
                try { DrainDispatcher(); window.UpdateLayout(); }
                finally { timeout.Stop(); }
            }
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            var header = (Grid)window.FindName("HeaderPanel");
            var heading = (FrameworkElement)window.FindName("WorkspaceHeading");
            var subtitle = (FrameworkElement)window.FindName("WorkspaceSubtitle");
            var workArea = IPhoneMirror.SharedUI.Services.WindowWorkAreaController.GetAvailableSize(window);
            var chromeWidth = window.ActualWidth - ((FrameworkElement)window.FindName("MainContentGrid")).ActualWidth;
            const double leftSidebarWidth = WorkspaceRevealPanel.DefaultLeftContentWidth;
            const double rightSidebarWidth = WorkspaceRevealPanel.DefaultRightContentWidth;
            var fitsBothCards = workArea.Width - chromeWidth >= leftSidebarWidth + rightSidebarWidth + 36 + 319;
            var largestRequest = workArea.Width - 24;
            double? referenceHeaderHeight = null;
            double? referenceContentTop = null;
            foreach (var (showLeft, showRight) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                window.Width = Math.Min(1440, largestRequest); Layout();
                KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, showLeft ? "Devices" : "None"));
                KeyboardCall(window, "SetSettingsPanelVisible", showRight);
                FinishWorkspaceReveal(window); Layout();
                var expectLeft = showLeft && (!showRight || fitsBothCards);
                foreach (var width in showLeft || showRight ? new[] { 1100d, 960d, 800d, 960d, 1280d } : new[] { 1100d, 800d, 640d, 480d, 640d, 960d, 1280d })
                {
                    var request = Math.Min(width, largestRequest);
                    // Exercise the actual HWND resize, retaining production minimums.
                    InteractionAssert(PerfSetWindowPos(hwnd, 0, 0, 0, (int)Math.Round(request * dpi),
                        (int)Math.Round(window.ActualHeight * dpi), 0x0016), "Native horizontal resize failed.");
                    Layout(); AdvanceDispatcher(TimeSpan.FromMilliseconds(30)); Layout();
                    InteractionAssert(Math.Abs(window.ActualWidth - Math.Max(request, window.MinWidth)) < 2 && center.ActualWidth >= 319,
                        $"Native width bounced back or squeezed the preview: request={request}, actual={window.ActualWidth}, minimum={window.MinWidth}, calculated={KeyboardCall(window, "GetCompleteWorkspaceMinimumWidth")}, preview={center.ActualWidth}, left={showLeft}, right={showRight}, constrained={KeyboardField(window, "_isWindowMaximized")}, available={workArea.Width}.");
                    InteractionAssert(left.IsVisible == expectLeft && right.IsVisible == showRight,
                        "Horizontal resizing unexpectedly hid an open workspace card.");
                    InteractionAssert((!expectLeft || Math.Abs(left.ActualWidth - leftSidebarWidth) < 1) &&
                        (!showRight || Math.Abs(right.ActualWidth - rightSidebarWidth) < 1),
                        $"Narrowing compressed the side cards: {left.ActualWidth:F1}/{right.ActualWidth:F1}.");
                    var actions = (FrameworkElement)window.FindName("WindowActions");
                    var capture = (FrameworkElement)window.FindName("CaptureActionButton");
                    var driver = (FrameworkElement)window.FindName("DetectionStatus");
                    InteractionAssert(Grid.GetRow(capture) == 0 && Grid.GetColumn(capture) == 3 && Grid.GetRow(driver) == 0 &&
                        Grid.GetColumn(driver) == 1 && Grid.GetColumnSpan(heading) == 1 && header.RowDefinitions.Count == 1 &&
                        Math.Abs(Bounds(capture).Top + capture.ActualHeight / 2 - Bounds(actions).Top - actions.ActualHeight / 2) <= 1,
                        $"Header layout changed at {window.ActualWidth:F1}: capture={Grid.GetRow(capture)}/{Grid.GetColumn(capture)} center={Bounds(capture).Top + capture.ActualHeight / 2:F2}, driver={Grid.GetRow(driver)}/{Grid.GetColumn(driver)}, headingSpan={Grid.GetColumnSpan(heading)}, rows={header.RowDefinitions.Count}, actionsCenter={Bounds(actions).Top + actions.ActualHeight / 2:F2}.");
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
            // Opening cards from the narrow idle window must grow the HWND,
            // rather than reflowing their content into the previous width.
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "None"));
            KeyboardCall(window, "SetSettingsPanelVisible", false); FinishWorkspaceReveal(window);
            window.Width = 480; Layout();
            InteractionAssert(Math.Abs(window.ActualWidth - 480) < 2 && Math.Abs(window.MinWidth - 480) < 2,
                "Closing both cards did not release the complete-mode sidebar minimum.");
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "Devices"));
            KeyboardCall(window, "SetSettingsPanelVisible", true); FinishWorkspaceReveal(window); Layout();
            InteractionAssert(window.ActualWidth >= window.MinWidth - 1 && left.IsVisible == fitsBothCards &&
                (!fitsBothCards || Math.Abs(left.ActualWidth - leftSidebarWidth) < 1) &&
                Math.Abs(right.ActualWidth - rightSidebarWidth) < 1 && center.ActualWidth >= 319,
                "Opening both cards from 480 DIP squeezed a card or the preview instead of growing the window.");
            SaveWindowRender(window, Path.Combine(output, "expanded-from-480.png"));
            // Reapplying the mode, full screen and a lightweight roundtrip must
            // retain the relaxed complete-mode limit and prior manual width.
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "None"));
            KeyboardCall(window, "SetSettingsPanelVisible", false); FinishWorkspaceReveal(window);
            var roundtripWidth = Math.Min(900, largestRequest);
            window.Width = roundtripWidth; Layout();
            KeyboardCall(window, "ApplyApplicationDisplayMode"); Layout();
            InteractionAssert(window.ActualWidth < 910 && window.MinWidth <= 800, "Applying complete mode restored the old 1280 DIP minimum.");
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight; Layout();
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete; Layout();
            InteractionAssert(Math.Abs(window.ActualWidth - roundtripWidth) < 2 && window.MinWidth <= 800,
                "A display-mode roundtrip lost the user's narrow complete-mode width.");
            KeyboardCall(window, "ToggleFullScreen"); Layout();
            KeyboardCall(window, "ToggleFullScreen"); Layout();
            InteractionAssert(Math.Abs(window.ActualWidth - roundtripWidth) < 2 && window.MinWidth <= 800,
                "Full-screen exit restored the old complete-mode minimum.");
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP", "zh-TW", "zh-HK" })
            foreach (var scale in new[] { 1d, 1.5 })
            {
                LocalizationService.SetLanguage(language);
                IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, scale, false);
                window.Width = 800; Layout();
                KeyboardCall(window, "SetSettingsPanelVisible", true); FinishWorkspaceReveal(window); Layout();
                var rightWidth = rightSidebarWidth * (1 + .5 * (scale - 1));
                var availablePreviewWidth = workArea.Width - chromeWidth - rightWidth - 18;
                InteractionAssert(Math.Abs(right.ActualWidth - rightWidth) <= 1 &&
                    center.ActualWidth >= Math.Min(320 * (1 + .35 * (scale - 1)), availablePreviewWidth) - 1,
                    $"Text scaling squeezed the settings card or preview: {language}/{scale}.");
                foreach (var category in new[] { "Application", "Projection", "Control" })
                {
                    ((RadioButton)window.FindName(category + "SettingsTab")).IsChecked = true; Layout();
                    var scroll = (ScrollViewer)window.FindName(category + "SettingsScrollViewer");
                    scroll.ScrollToTop(); Layout();
                    var tab = (FrameworkElement)window.FindName(category + "SettingsTab");
                    InteractionAssert(tab.IsVisible && scroll.ActualWidth > 240 && scroll.ScrollableWidth < 1 && Bounds(tab).Right <= Bounds(right).Right + 1,
                        $"Settings lose usable width at the complete-mode minimum: {language}/{scale}/{category}.");
                }
                var actions = (FrameworkElement)window.FindName("WindowActions");
                var capture = (FrameworkElement)window.FindName("CaptureActionButton");
                InteractionAssert(Math.Abs(Bounds(capture).Top + capture.ActualHeight / 2 - Bounds(actions).Top - actions.ActualHeight / 2) <= 1 &&
                    Bounds(capture).Left >= Bounds(heading).Right - 1 && Bounds(capture).Right <= Bounds(actions).Left + 1 &&
                    subtitle.IsVisible && header.ActualHeight < 100 * scale,
                    $"Header controls moved, overlapped or wrapped at {language}/{scale}.");
                var completeHeaderHeight = header.ActualHeight;
                var completeContentTop = Bounds((FrameworkElement)window.FindName("MainContentGrid")).Top;
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight; Layout();
                InteractionAssert(!subtitle.IsVisible && header.RowDefinitions.Count == 1 &&
                    Math.Abs(Bounds(capture).Top + capture.ActualHeight / 2 - Bounds(actions).Top - actions.ActualHeight / 2) <= 1,
                    "Lightweight mode did not hide the subtitle or moved header actions into different rows.");
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete; Layout();
                InteractionAssert(subtitle.IsVisible && Math.Abs(header.ActualHeight - completeHeaderHeight) < 1 &&
                    Math.Abs(Bounds((FrameworkElement)window.FindName("MainContentGrid")).Top - completeContentTop) < 1,
                    "Complete mode failed to restore its description preference and header geometry.");
                SaveWindowRender(window, Path.Combine(output, $"stable-header-{language}-{scale:0.0}.png"));
                KeyboardCall(window, "SetSettingsPanelVisible", false); FinishWorkspaceReveal(window);
            }
            Console.WriteLine("PASS complete resize: fixed-width side cards, 320 DIP preview reserve, dynamic native minimums, header/status layout, mode/fullscreen restore and five languages at 100/150% text.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }
}
