using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWindowDragRegionTests(bool nativeInput = false)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        window.Left = SystemParameters.WorkArea.Left + 40;
        window.Top = SystemParameters.WorkArea.Top + 40;
        DrainDispatcher(); window.UpdateLayout();
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        FrameworkElement Element(string name) => window.FindName(name) as FrameworkElement ??
            FindVisualDescendants<FrameworkElement>(window).First(element =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(element) == name);
        bool CanDrag(DependencyObject? source) => (bool)KeyboardCall(window, "IsWorkspaceDragSource", source)!;
        DependencyObject Hit(FrameworkElement element, Point position) =>
            VisualTreeHelper.HitTest(Element("AppShell"), element.TranslatePoint(position, Element("AppShell")))!.VisualHit;
        void CheckPoint(string name, Point position, bool expected)
        {
            var hit = Hit(Element(name), position);
            InteractionAssert(CanDrag(hit) == expected, $"Unexpected window drag behavior at {name}/{position}: {hit}.");
        }
        void CheckCenter(string name, bool expected)
        {
            var element = Element(name);
            CheckPoint(name, new Point(element.ActualWidth / 2, element.ActualHeight / 2), expected);
        }
        try
        {
            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                vm.SelectedApplicationDisplayMode = mode;
                KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "Devices"));
                KeyboardCall(window, "SetSettingsPanelVisible", true);
                FinishWorkspaceReveal(window); DrainDispatcher(); window.UpdateLayout();
                CheckCenter("WorkspaceTitleText", true);
                CheckPoint("RootLayout", new Point(200, -9), true);
                CheckPoint("RootLayout", new Point(200, Element("HeaderPanel").ActualHeight + 9), true);
                var left = Element("LeftPanelHost");
                CheckPoint("MainContentGrid", new Point(left.ActualWidth + 9, 100), true);
                var right = Element("ControlPanel");
                var rightOrigin = right.TranslatePoint(new Point(), Element("MainContentGrid"));
                CheckPoint("MainContentGrid", new Point(rightOrigin.X - 9, 100), true);
                CheckPoint("DevicePanelHeader", new Point(20, 16), true);
                CheckPoint("SettingsPanelHeader", new Point(20, 16), true);
                foreach (var name in new[] { "CaptureActionButton", "ProjectionSettingsTab", "DevicePanelToggle",
                    "CloseDevicePanelButton", "CloseSettingsPanelButton", "PreviewPanel", "DeviceListBox" })
                    CheckCenter(name, false);
                foreach (var control in FindVisualDescendants<Control>(Element("ControlPanel"))
                    .Where(control => control.IsVisible && control is ComboBox or TextBox or Slider or CheckBox))
                    InteractionAssert(!CanDrag(control), $"Dragging intercepted {control.GetType().Name}.");
                foreach (var button in FindVisualDescendants<Button>(Element("WindowActions")))
                    InteractionAssert(!CanDrag(button), "Dragging intercepted a window action.");

                KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "Mirroring"));
                FinishWorkspaceReveal(window); DrainDispatcher(); window.UpdateLayout();
                CheckPoint("MirroringPanelHeader", new Point(20, 16), true);
                CheckCenter("SidebarBluetoothControlButton", false);
                Console.WriteLine($"PASS {mode}: shell gaps and card headers drag; window buttons, navigation, settings and preview retain input.");
            }
            KeyboardCall(window, "ToggleFullScreen"); DrainDispatcher(); window.UpdateLayout();
            InteractionAssert(!CanDrag(Element("AppShell")), "Fullscreen still permits window dragging.");
            KeyboardCall(window, "ToggleFullScreen"); DrainDispatcher(); window.UpdateLayout();
            InteractionAssert(CanDrag(Element("WorkspaceTitleText")), "Leaving fullscreen did not restore window dragging.");
            Console.WriteLine("PASS fullscreen disables window dragging and restores it on exit.");
            TestMaximizedDragRestoreGeometry(window);
            if (nativeInput) TestWindowDragInput(window);
            return 0;
        }
        finally { CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }

    private static void TestMaximizedDragRestoreGeometry(MainWindow window)
    {
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var hwnd = new WindowInteropHelper(window).Handle;
        void Layout() { DrainDispatcher(); window.UpdateLayout(); }
        try
        {
            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            foreach (var (width, height) in new[] { (0u, 0u), (1082u, 2340u), (2340u, 1082u) })
            {
                SetKeyboardField(vm, "_isCapturing", false);
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete; Layout();
                window.Width = 1540; window.Height = 820; Layout();
                SetKeyboardField(vm, "_sourceVideoWidth", width);
                SetKeyboardField(vm, "_sourceVideoHeight", height);
                SetKeyboardField(vm, "_isCapturing", width != 0);
                vm.SelectedApplicationDisplayMode = mode;
                KeyboardCall(window, "ApplyApplicationDisplayMode"); Layout();
                var restore = new Size(window.ActualWidth, window.ActualHeight);
                KeyboardCall(window, "MaximizeWindow", (Rect?)null); Layout();
                var pointer = window.PointToScreen(new Point(window.ActualWidth / 2, 100));
                KeyboardCall(window, "RestoreMaximizedWorkspaceForDrag", pointer, .5, 80d); Layout();
                UnfixGetWindowRect(hwnd, out var native);
                var scale = RepairGetDpiForWindow(hwnd) / 96d;
                InteractionAssert(!(bool)KeyboardField(window, "_isWindowMaximized") &&
                    Math.Abs(window.ActualWidth - restore.Width) <= 2 && Math.Abs(window.ActualHeight - restore.Height) <= 2 &&
                    Math.Abs((native.Right - native.Left) / scale - restore.Width) <= 2 &&
                    Math.Abs((native.Bottom - native.Top) / scale - restore.Height) <= 2,
                    $"{mode}/{width}x{height}: maximize drag changed its saved dimensions: {window.ActualWidth:F1}x{window.ActualHeight:F1}/{restore.Width:F1}x{restore.Height:F1}.");
                var content = (FrameworkElement)window.FindName("MainContentGrid");
                InteractionAssert(content.ActualWidth > 0 && !WorkspaceRevealRunning(window),
                    "Restoring for a drag retained a stale workspace transition.");
                Console.WriteLine($"PASS {mode}/{width}x{height}: maximize drag restores native/managed dimensions and stops stale transitions.");
            }
        }
        finally
        {
            SetKeyboardField(vm, "_isCapturing", false);
            SetKeyboardField(vm, "_sourceVideoWidth", 0u);
            SetKeyboardField(vm, "_sourceVideoHeight", 0u);
            KeyboardCall(window, "ApplyApplicationDisplayMode"); Layout();
        }
    }

    private static void TestWindowDragInput(MainWindow window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var shell = (FrameworkElement)window.FindName("AppShell");
        var root = (FrameworkElement)window.FindName("RootLayout");
        var header = (FrameworkElement)window.FindName("HeaderPanel");
        InteractionAssert(WindowDragGetCursorPos(out var originalCursor), "The input desktop is not available for mouse tests.");
        var originalForeground = UnfixForeground();
        var originalTopmost = window.Topmost;
        void Layout() { DrainDispatcher(); window.UpdateLayout(); }
        Point Gap() => root.PointToScreen(new Point(200, header.ActualHeight + 9));
        bool RaiseCaptured(UIElement source)
        {
            var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            source.RaiseEvent(input);
            return input.Handled;
        }
        void Drag(Point start, int dx, int dy)
        {
            MappingSetCursorPos((int)start.X, (int)start.Y);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            var hit = AbsoluteLiveWindowFromPoint(new AbsoluteLivePoint { X = (int)start.X, Y = (int)start.Y });
            UnfixGetWindowRect(hwnd, out var current);
            InteractionAssert(hit == hwnd,
                $"The drag test window is occluded; no input was sent. Point={start}, HWND={hwnd}/{hit}, bounds={current.Left},{current.Top},{current.Right},{current.Bottom}, mode={vm.SelectedApplicationDisplayMode}.");
            var input = Task.Run(() =>
            {
                UnfixMouse(2, 0, 0, 0, 0);
                try
                {
                    Thread.Sleep(200);
                    MappingSetCursorPos((int)start.X + dx / 2, (int)start.Y + dy / 2);
                    Thread.Sleep(100);
                    MappingSetCursorPos((int)start.X + dx, (int)start.Y + dy);
                    Thread.Sleep(180);
                }
                finally { UnfixMouse(4, 0, 0, 0, 0); }
            });
            AdvanceDispatcher(TimeSpan.FromMilliseconds(850));
            WaitReviewTask(input); Layout();
        }
        void DoubleClick(Point point)
        {
            MappingSetCursorPos((int)point.X, (int)point.Y);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(AbsoluteLiveWindowFromPoint(new AbsoluteLivePoint { X = (int)point.X, Y = (int)point.Y }) == hwnd,
                "The double-click test window is occluded; no input was sent.");
            var input = Task.Run(() =>
            {
                for (var i = 0; i < 2; i++)
                {
                    UnfixMouse(2, 0, 0, 0, 0);
                    Thread.Sleep(60);
                    UnfixMouse(4, 0, 0, 0, 0);
                    Thread.Sleep(80);
                }
            });
            AdvanceDispatcher(TimeSpan.FromMilliseconds(650));
            WaitReviewTask(input); Layout();
        }
        try
        {
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete; Layout();
            // Windows may refuse foreground activation for a background test
            // process. Keep this inert window above other apps and verify the
            // target HWND before every injected click instead.
            window.Topmost = true;
            window.Activate(); AbsoluteLiveForeground(hwnd);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            var gap = Gap();
            MappingSetCursorPos((int)gap.X, (int)gap.Y);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            var captured = (UIElement)window.FindName("ProjectionSettingsTab");
            var capturedInput = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = captured };
            KeyboardCall(window, "OnWorkspaceMouseLeftButtonDown", shell, capturedInput);
            InteractionAssert(!capturedInput.Handled,
                "A captured control's mouse event was stolen by a shell gap beneath the pointer.");
            var title = (UIElement)window.FindName("WorkspaceTitleText");
            InteractionAssert(RaiseCaptured(title), "The shell's real routed mouse event did not reach its drag handler.");
            Console.WriteLine("PASS routed window drag input: captured controls retain their events; shell presses reach the handler.");
            var disabledButton = (FrameworkElement)window.FindName("CaptureActionButton");
            InteractionAssert(!disabledButton.IsEnabled, "The fixture requires a disabled capture button.");
            UnfixGetWindowRect(hwnd, out var buttonBefore);
            Drag(disabledButton.PointToScreen(new Point(disabledButton.ActualWidth / 2, disabledButton.ActualHeight / 2)), 40, 25);
            UnfixGetWindowRect(hwnd, out var buttonAfter);
            InteractionAssert(buttonBefore.Equals(buttonAfter), "Dragging on a disabled button moved its parent window.");
            Console.WriteLine("PASS a disabled header button cannot start a window drag.");

            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                vm.SelectedApplicationDisplayMode = mode; Layout();
                UnfixGetWindowRect(hwnd, out var before);
                Drag(Gap(), 40, 25);
                UnfixGetWindowRect(hwnd, out var after);
                InteractionAssert(Math.Abs(after.Left - before.Left - 40) <= 2 && Math.Abs(after.Top - before.Top - 25) <= 2 &&
                    after.Right - after.Left == before.Right - before.Left && after.Bottom - after.Top == before.Bottom - before.Top,
                    $"{mode}: real Windows drag bounds {before.Left},{before.Top},{before.Right},{before.Bottom} -> {after.Left},{after.Top},{after.Right},{after.Bottom}.");
                Console.WriteLine($"PASS {mode}: a real mouse drag moves the window 40x25 pixels without resizing.");
            }
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete; Layout();
            var restoreWidth = window.ActualWidth;
            var restoreHeight = window.ActualHeight;
            KeyboardCall(window, "MaximizeWindow", (Rect?)null); Layout();
            UnfixGetWindowRect(hwnd, out var maximizedBefore);
            Drag(Gap(), 1, 1);
            UnfixGetWindowRect(hwnd, out var maximizedAfter);
            InteractionAssert((bool)KeyboardField(window, "_isWindowMaximized") && maximizedBefore.Equals(maximizedAfter) &&
                KeyboardField(window, "_maximizedWorkspaceDrag") is null && Mouse.Captured != shell,
                "A click or tiny mouse movement restored the maximized window or retained capture.");
            Drag(Gap(), 40, 25);
            InteractionAssert(!(bool)KeyboardField(window, "_isWindowMaximized") &&
                Math.Abs(window.ActualWidth - restoreWidth) <= 2 && Math.Abs(window.ActualHeight - restoreHeight) <= 2,
                "Dragging the maximized window did not restore its normal size and state.");
            Console.WriteLine("PASS dragging a maximized window restores its saved size and normal window state.");
            DoubleClick(Gap());
            InteractionAssert((bool)KeyboardField(window, "_isWindowMaximized"), "Double-clicking a drag region did not maximize the window.");
            DoubleClick(Gap());
            InteractionAssert(!(bool)KeyboardField(window, "_isWindowMaximized") && Mouse.Captured != shell &&
                Math.Abs(window.ActualWidth - restoreWidth) <= 2 && Math.Abs(window.ActualHeight - restoreHeight) <= 2,
                "Double-clicking the maximized drag region did not restore the saved size or release capture.");
            Console.WriteLine("PASS maximized clicks keep their size and release capture; double-clicks maximize and restore.");
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight; Layout();
            var lightweightWidth = window.ActualWidth;
            var lightweightHeight = window.ActualHeight;
            KeyboardCall(window, "MaximizeWindow", (Rect?)null); Layout();
            Drag(Gap(), 40, 25);
            InteractionAssert(!(bool)KeyboardField(window, "_isWindowMaximized") &&
                Math.Abs(window.ActualWidth - lightweightWidth) <= 2 && Math.Abs(window.ActualHeight - lightweightHeight) <= 2,
                $"Lightweight maximize drag changed the restored size: {window.ActualWidth:F1}x{window.ActualHeight:F1}/{lightweightWidth:F1}x{lightweightHeight:F1}.");
            Console.WriteLine("PASS dragging the maximized lightweight window restores its saved size.");
        }
        finally
        {
            UnfixMouse(4, 0, 0, 0, 0);
            window.Topmost = originalTopmost;
            MappingSetCursorPos(originalCursor.X, originalCursor.Y);
            if (originalForeground != 0 && originalForeground != hwnd) AbsoluteLiveForeground(originalForeground);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool WindowDragGetCursorPos(out AbsoluteLivePoint point);
}
