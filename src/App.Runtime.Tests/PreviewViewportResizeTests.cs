using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPreviewViewportResizeTests(string output, bool live = false, bool performance = false,
        bool previewBars = false)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            SetApplicationDisplayMode(app, previewBars ? ApplicationDisplayMode.Complete : ApplicationDisplayMode.Lightweight);
            app.UpdateSettings.MirroringAutoControlMode = IPhoneMirror.App.Services.ReverseControlMode.None;
            var window = live ? new MainWindow() : CreateWorkspaceTestWindow(app, includeNativePreview: false);
            var vm = (MainViewModel)KeyboardField(window, "_viewModel");
            try
            {
                if (live)
                {
                    app.MainWindow = window;
                    window.Show();
                    bool Wait(Func<bool> predicate)
                    {
                        var watch = Stopwatch.StartNew();
                        while (!predicate() && watch.Elapsed.TotalSeconds < 40)
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                        return predicate();
                    }
                    InteractionAssert(Wait(() => vm.Devices.Any(d => !d.IsWireless && !d.IsMediaCast)), "No real USB phone discovered.");
                    var phones = vm.Devices.Where(d => !d.IsWireless && !d.IsMediaCast && vm.ResolveAppleUdid(d.Udid) is not null).ToArray();
                    InteractionAssert(phones.Length == 1, "Live test requires exactly one bound USB phone.");
                    vm.SelectedDevice = phones.Single();
                    InteractionAssert(vm.StartCommand.CanExecute(null), "Real capture cannot start.");
                    vm.StartCommand.Execute(null);
                    InteractionAssert(Wait(() => vm.IsCapturing && vm.SourceVideoWidth > 0 && vm.SourceVideoHeight > 0 && vm.CurrentSessionHandle != 0), "Real capture did not produce video dimensions.");
                    InteractionAssert(Wait(() =>
                    {
                        try { vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, "live-phone-before.png"))); return true; }
                        catch (InvalidOperationException) { return false; }
                    }), "Real video frame could not be captured after startup.");
                    Console.WriteLine($"LIVE CAPTURE: real USB session, {vm.SourceVideoWidth}x{vm.SourceVideoHeight}; source screenshot captured.");
                }
                if (previewBars)
                {
                    VerifyPreviewBars(window, app, vm, output, live);
                    return 0;
                }
                if (!live)
                {
                    SetKeyboardField(vm, "_isCapturing", true);
                    SetKeyboardField(vm, "_sourceVideoWidth", 1082u);
                    SetKeyboardField(vm, "_sourceVideoHeight", 2340u);
                }
                var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
                var preview = (FrameworkElement)window.FindName("PreviewPanel");
                var center = (FrameworkElement)window.FindName("CenterPanel");
                var column = (ColumnDefinition)window.FindName("CenterColumn");
                if (performance)
                    return MeasureLiveWindowInteraction(window, vm, output);
                foreach (var left in new[] { false, true })
                foreach (var right in new[] { false, true })
                {
                    KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, left ? "Devices" : "None"));
                    KeyboardCall(window, "SetSettingsPanelVisible", right);
                    KeyboardCall(window, "ApplyApplicationDisplayMode");
                    FinishWorkspaceReveal(window); DrainDispatcher(); window.UpdateLayout();
                    var baseWidth = Math.Max(window.MinWidth + 80, 1200);
                    // Corners still clamp to the height supplied by the drag.
                    var hwnd = new WindowInteropHelper(window).Handle;
                    var scale = RepairGetDpiForWindow(hwnd) / 96d;
                    PreviewStyleGetWindowRect(hwnd, out var nativeBounds);
                    foreach (var edge in new[] { 4, 5, 7, 8 })
                    {
                        var rectangle = new[] { nativeBounds.Left, nativeBounds.Top, nativeBounds.Right + 600, nativeBounds.Bottom };
                        var memory = Marshal.AllocHGlobal(16);
                        try
                        {
                            Marshal.Copy(rectangle, 0, memory, 4);
                            object[] arguments = [hwnd, 0x0214, (nint)edge, memory, false];
                            typeof(MainWindow).GetMethod("WindowMessageHook", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
                            Marshal.Copy(memory, rectangle, 0, 4);
                            var snugPixels = (double)KeyboardCall(window, "GetLightweightSnugWidth", preview.ActualHeight)! * scale;
                            InteractionAssert((bool)arguments[4] && Math.Abs(rectangle[2] - rectangle[0] - snugPixels) <= 2,
                                "Native sizing rectangle retained excess width before display.");
                        }
                        finally { Marshal.FreeHGlobal(memory); }
                    }
                    ExerciseProportionalEdgeDrag(window, preview, hwnd, scale, live);
                    var step = 0;
                    foreach (var (width, height) in new[] { (baseWidth, 800d), (baseWidth, 1060d), (baseWidth + 160, 1060d),
                        (baseWidth + 160, 680d), (baseWidth, 680d), (baseWidth, 820d) })
                    {
                        // Submit one window rectangle, as Windows does for a
                        // corner drag. Separate Width and Height assignments
                        // would first clamp the width against the old height.
                        InteractionAssert(PerfSetWindowPos(hwnd, 0, 0, 0,
                            (int)Math.Round(width * scale), (int)Math.Round(height * scale), 0x0016),
                            "Unable to apply the native resize rectangle.");
                        window.UpdateLayout();
                        // Check at the next render opportunity, without waiting
                        // for idle or a simulated end of the resize gesture.
                        window.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
                        window.UpdateLayout();
                        DrainDispatcher(); window.UpdateLayout();
                        var expectedLimit = Math.Round(preview.ActualHeight * vm.SourceVideoWidth / vm.SourceVideoHeight);
                        InteractionAssert(Math.Abs(preview.MaxWidth - expectedLimit) <= 1,
                            $"left={left}/right={right}/{width:F0}x{height:F0}: stale preview limit {preview.MaxWidth:F2}, expected {expectedLimit:F2} from height {preview.ActualHeight:F2}.");
                        InteractionAssert(column.Width.IsStar, "Startup's fixed center width survived a manual resize.");
                        var snug = (double)KeyboardCall(window, "GetLightweightSnugWidth", preview.ActualHeight)!;
                        var expectedWidth = Math.Min(width, snug);
                        InteractionAssert(Math.Abs(window.ActualWidth - expectedWidth) <= 2,
                            $"Excess width did not retract: {window.ActualWidth:F2}, expected {expectedWidth:F2}.");
                        if (live)
                        {
                            InteractionAssert(vm.IsCapturing && vm.CurrentSessionHandle != 0, "Capture stopped during resize.");
                            vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, $"live-phone-{left}-{right}-{step}.png")));
                        }
                        var bounds = preview.TransformToAncestor(center).TransformBounds(new Rect(preview.RenderSize));
                        InteractionAssert(bounds.Left >= -1 && bounds.Right <= center.ActualWidth + 1,
                            "Resized preview overlaps adjacent cards.");
                        SaveWindowRender(window, Path.Combine(output, $"left-{left}-right-{right}-{step++}.png"));
                    }
                    Console.WriteLine($"PASS {(live ? "LIVE" : "simulated")} left={left}/right={right}: six width/height changes refresh source-aspect limits, immediately retract surplus width and preserve preview containment.");
                }
                KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "None"));
                KeyboardCall(window, "SetSettingsPanelVisible", false);
                FinishWorkspaceReveal(window); DrainDispatcher(); window.UpdateLayout();
                void CheckCentered(string state)
                {
                    var area = (FrameworkElement)window.FindName("MainContentGrid");
                    var origin = preview.TranslatePoint(new Point(), area);
                    InteractionAssert(Math.Abs(origin.X + preview.ActualWidth / 2 - area.ActualWidth / 2) <= 2,
                        $"{state}: preview is not centered in the workspace.");
                    if (live) vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, $"live-{state}.png")));
                    SaveWindowRender(window, Path.Combine(output, $"window-{state}.png"));
                }
                CheckCentered("both-closed");
                foreach (var left in new[] { false, true })
                foreach (var right in new[] { false, true })
                {
                    KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, left ? "Devices" : "None"));
                    KeyboardCall(window, "SetSettingsPanelVisible", right);
                    FinishWorkspaceReveal(window);
                    KeyboardCall(window, "MaximizeWindow", (Rect?)null);
                    window.UpdateLayout(); DrainDispatcher(); window.UpdateLayout();
                    var group = (FrameworkElement)window.FindName("MainContentGrid");
                    var root = (FrameworkElement)window.FindName("RootLayout");
                    var x = group.TranslatePoint(new Point(), root).X;
                    InteractionAssert(Math.Abs(x * 2 + group.ActualWidth - root.ActualWidth) < 2,
                        "Maximized projection cards remain left aligned with empty space on the right.");
                    if (live) vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, $"maximized-phone-{left}-{right}.png")));
                    SaveWindowRender(window, Path.Combine(output, $"maximized-group-{left}-{right}.png"));
                    KeyboardCall(window, "RestoreWindowFromMaximized");
                    DrainDispatcher(); window.UpdateLayout();
                }
                Console.WriteLine($"PASS {(live ? "LIVE" : "simulated")}: maximized card group centered in all four sidebar combinations; restore passed.");
                foreach (var open in new[] { false, true })
                {
                    KeyboardCall(window, "SetSettingsPanelVisible", open);
                    FinishWorkspaceReveal(window);
                    KeyboardCall(window, "ToggleFullScreen");
                    window.UpdateLayout(); DrainDispatcher(); window.UpdateLayout();
                    CheckCentered($"fullscreen-sidebar-{open}");
                    InteractionAssert(preview.ActualWidth >= ((FrameworkElement)window.FindName("MainContentGrid")).ActualWidth - 2,
                        "Full screen retained the compact center-column cap.");
                    KeyboardCall(window, "ToggleFullScreen");
                    DrainDispatcher(); window.UpdateLayout();
                }
                Console.WriteLine($"PASS {(live ? "LIVE" : "simulated")}: both-closed preview centered; full-screen centered/fills with sidebars closed or open; restored successfully.");
                return 0;
            }
            finally
            {
                if (live)
                {
                    var shutdown = vm.ShutdownAsync();
                    var timeout = Stopwatch.StartNew();
                    while (!shutdown.IsCompleted && timeout.Elapsed < vm.NormalShutdownTimeout + TimeSpan.FromSeconds(15))
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                    InteractionAssert(shutdown.IsCompleted, "Real capture cleanup timed out.");
                    shutdown.GetAwaiter().GetResult();
                    Console.WriteLine($"LIVE CLEANUP: real capture stopped in {timeout.Elapsed.TotalSeconds:F2}s.");
                }
                if (!live) SetKeyboardField(vm, "_isCapturing", false);
                CloseWorkspaceTestWindow(window);
            }
        }
        finally { app.Shutdown(); }
    }

    private static void ExerciseProportionalEdgeDrag(MainWindow window, FrameworkElement preview,
        nint hwnd, double scale, bool live)
    {
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var aspect = (double)vm.SourceVideoWidth / vm.SourceVideoHeight;
        foreach (var edge in new[] { 1, 2, 3, 6 })
        {
            PreviewStyleGetWindowRect(hwnd, out var initial);
            var minHeight = (int)Math.Ceiling(window.MinHeight * scale);
            object?[] monitor = [null, null];
            var top = initial.Top;
            if ((bool)typeof(MainWindow).GetMethod("TryGetLightweightWorkArea", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, monitor)!)
            {
                var workTop = (int)KeyboardField(monitor[0]!, "Top");
                var workBottom = (int)KeyboardField(monitor[0]!, "Bottom");
                top = workTop + Math.Max(0, workBottom - workTop - minHeight) / 2;
            }
            InteractionAssert(PerfSetWindowPos(hwnd, 0, initial.Left, top, initial.Right - initial.Left,
                minHeight, 0x0014), "Could not prepare the edge-drag starting rectangle.");
            DrainDispatcher(); window.UpdateLayout();
            PreviewStyleGetWindowRect(hwnd, out initial);
            var initialPreviewHeight = preview.ActualHeight;
            var initialPreviewWidth = preview.ActualWidth;
            var memory = Marshal.AllocHGlobal(16);
            try
            {
                ResizeSendMessage(hwnd, 0x0231, 0, 0);
                int[] ResizeBy(double delta)
                {
                    var pixels = (int)Math.Round(delta * scale);
                    var rectangle = new[] { initial.Left - (edge == 1 ? pixels : 0), initial.Top - (edge == 3 ? pixels : 0),
                        initial.Right + (edge == 2 ? pixels : 0), initial.Bottom + (edge == 6 ? pixels : 0) };
                    Marshal.Copy(rectangle, 0, memory, 4);
                    InteractionAssert(ResizeSendMessage(hwnd, 0x0214, edge, memory) == 1,
                        $"The real window hook did not handle resize edge {edge}.");
                    Marshal.Copy(memory, rectangle, 0, 4);
                    InteractionAssert((edge == 1 ? rectangle[2] == initial.Right : rectangle[0] == initial.Left) &&
                        (edge != 3 || rectangle[3] == initial.Bottom) && (edge != 6 || rectangle[1] == initial.Top),
                        $"The opposite window edge moved during scaling from edge {edge}.");
                    return rectangle;
                }
                void Apply(int[] rect)
                {
                    InteractionAssert(PerfSetWindowPos(hwnd, 0, rect[0], rect[1], rect[2] - rect[0], rect[3] - rect[1], 0x0014),
                        "Could not apply the proposed proportional resize.");
                    DrainDispatcher(); window.UpdateLayout();
                }
                var pointerDelta = edge is 1 or 2 ? 45 * aspect : 45;
                var grown = ResizeBy(pointerDelta);
                Apply(grown);
                InteractionAssert(Math.Abs(preview.ActualHeight - initialPreviewHeight - 45) <= 2,
                    $"Edge {edge} did not enlarge the actual preview proportionally: {initialPreviewHeight:F1} -> {preview.ActualHeight:F1}.");
                if (Math.Abs(initialPreviewWidth - initialPreviewHeight * aspect) <= 2)
                    InteractionAssert(Math.Abs(preview.ActualWidth - initialPreviewWidth - 45 * aspect) <= 2,
                        $"Edge {edge}: preview width {initialPreviewWidth:F2} -> {preview.ActualWidth:F2} (cap {preview.MaxWidth:F2}, expected {initialPreviewWidth + 45 * aspect:F2}); height {initialPreviewHeight:F2} -> {preview.ActualHeight:F2}; window {window.ActualWidth:F2}, proposed {(grown[2] - grown[0]) / scale:F2}.");
                InteractionAssert(ResizeBy(pointerDelta).Zip(grown).All(pair => Math.Abs(pair.First - pair.Second) <= 1),
                    "A stationary pointer changed the proposed rectangle after applying the resize.");
                Apply(ResizeBy(0));
                InteractionAssert(Math.Abs(preview.ActualHeight - initialPreviewHeight) <= 2,
                    "Reversing the side drag did not return the preview to its original size.");
                InteractionAssert(!live || vm.IsCapturing, "Real projection stopped during the edge drag.");
            }
            finally
            {
                ResizeSendMessage(hwnd, 0x0232, 0, 0);
                Marshal.FreeHGlobal(memory);
            }
        }
        Console.WriteLine($"PASS {(live ? "LIVE" : "simulated")}: all four edge handles proportionally scale, retain the opposite edge and reverse without drift.");
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint ResizeSendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
