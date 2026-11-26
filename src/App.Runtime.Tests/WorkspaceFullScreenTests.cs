using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.SharedUI.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWorkspaceFullScreenTests()
    {
        // Match the production app manifest. The test host otherwise virtualizes
        // the high-DPI secondary screen to 96 DPI and misses monitor-fit races.
        var previousDpiContext = WorkspaceSetDpiAwareness((nint)(-4));
        InteractionAssert(previousDpiContext != 0, "Cannot enable production per-monitor DPI awareness.");
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        WindowWorkAreaController.EnableForApplication();
        var displays = new List<WorkspaceDisplayInfo>();
        WorkspaceMonitorCallback collect = (nint monitor, nint dc, ref WorkspaceNativeRect rect, nint data) =>
        {
            var info = new WorkspaceDisplayInfo { Size = (uint)Marshal.SizeOf<WorkspaceDisplayInfo>() };
            if (WorkspaceGetMonitorInfo(monitor, ref info)) displays.Add(info);
            return true;
        };
        InteractionAssert(WorkspaceEnumMonitors(0, 0, collect, 0) && displays.Count > 0,
            "No desktop displays are available for the fullscreen regression.");
        try
        {
            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            foreach (var display in displays.OrderBy(d => d.Monitor.Left))
            foreach (var video in new[] { false, true })
            {
                SetApplicationDisplayMode(app, mode);
                var window = CreateWorkspaceTestWindow(app, includeNativePreview: video);
                var handle = new WindowInteropHelper(window).Handle;
                var vm = (MainViewModel)KeyboardField(window, "_viewModel");
                try
                {
                    // Move a normal window first, allowing WPF's monitor/DPI events
                    // to finish before the user requests edge-to-edge maximize.
                    var area = display.WorkArea;
                    _ = WorkspaceSetWindowPos(handle, 0, area.Left + 50, area.Top + 50,
                        Math.Min(1200, area.Right - area.Left - 100),
                        Math.Min(800, area.Bottom - area.Top - 100), 0x14);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                    if (video)
                    {
                        SetKeyboardField(vm, "_selectedDevice", DeviceViewModel.FromNative(new NativeDeviceInfo
                            { Udid = "fullscreen-fixture", Name = "Fullscreen phone", State = ConnectionState.Ready }));
                        SetKeyboardField(vm, "_sourceVideoWidth", 1082u);
                        SetKeyboardField(vm, "_sourceVideoHeight", 2340u);
                        SetKeyboardField(vm, "_isCapturing", true);
                        KeyboardCall(window, "ApplyApplicationDisplayMode");
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                    }
                    KeyboardCall(window, "MaximizeWindow", (object?)null);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                    CheckBounds("maximize");
                    KeyboardCall(window, "OnNavigateDeviceHubClick", window, new RoutedEventArgs());
                    FinishWorkspaceReveal(window);
                    CheckBounds("first device management open");
                    KeyboardCall(window, "SetDeviceHubPanelVisible", false);
                    FinishWorkspaceReveal(window);
                    CheckBounds("first device management close");
                    var panelType = KeyboardField(window, "_leftWorkspacePanel").GetType();
                    foreach (var panel in new[] { "Mirroring", "Devices" })
                    {
                        KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, panel));
                        FinishWorkspaceReveal(window);
                        CheckBounds(panel);
                    }
                    KeyboardCall(window, "SetSettingsPanelVisible", true);
                    FinishWorkspaceReveal(window);
                    CheckBounds("settings");
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        KeyboardCall(window, "SetDeviceHubPanelVisible", true);
                        FinishWorkspaceReveal(window);
                        CheckBounds($"device management open {attempt}");
                        InteractionAssert(((FrameworkElement)window.FindName("DeviceHubPanelHost")).IsVisible,
                            "The maximized device management panel did not open.");
                        KeyboardCall(window, "SetDeviceHubPanelVisible", false);
                        FinishWorkspaceReveal(window);
                        CheckBounds($"device management close {attempt}");
                    }
                    // Monitor fitting is queued at Loaded priority. Opening a
                    // lazily-created page must not let an outstanding fit resize
                    // the user's newly maximized window back to normal bounds.
                    if (displays.FirstOrDefault(d => d.Monitor != display.Monitor) is { Size: > 0 } other)
                    {
                        KeyboardCall(window, "RestoreWindowFromMaximized");
                        _ = WorkspaceSetWindowPos(handle, 0, other.WorkArea.Left + 50, other.WorkArea.Top + 50,
                            Math.Min(1200, other.WorkArea.Right - other.WorkArea.Left - 100),
                            Math.Min(800, other.WorkArea.Bottom - other.WorkArea.Top - 100), 0x14);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                        _ = WorkspaceSetWindowPos(handle, 0, area.Left + 50, area.Top + 50,
                            Math.Min(1200, area.Right - area.Left - 100),
                            Math.Min(800, area.Bottom - area.Top - 100), 0x14);
                        KeyboardCall(window, "MaximizeWindow", (object?)null);
                        KeyboardCall(window, "SetDeviceHubPanelVisible", true);
                        FinishWorkspaceReveal(window);
                        CheckBounds("device management after pending monitor fit");
                    }
                    KeyboardCall(window, "ToggleFullScreen");
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    InteractionAssert((bool)KeyboardField(window, "_isFullScreen") &&
                        WorkspaceGetWindowRect(handle, out var fullScreenBounds) && fullScreenBounds == display.Monitor,
                        "Preview fullscreen changed the destination monitor bounds.");
                    KeyboardCall(window, "ToggleFullScreen");
                    FinishWorkspaceReveal(window);
                    CheckBounds("preview fullscreen roundtrip");
                    KeyboardCall(window, "RestoreWindowFromMaximized");
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                    InteractionAssert(!(bool)KeyboardField(window, "_isWindowMaximized") &&
                        window.WindowState == WindowState.Normal &&
                        WorkspaceGetWindowRect(handle, out var normalBounds) && normalBounds != display.Monitor,
                        "Leaving maximized mode did not restore an ordinary window.");
                    Console.WriteLine($"PASS {mode}, video={video}: navigation keeps fullscreen bounds on display {display.Monitor}, DPI={WorkspaceGetDpi(handle)}.");

                    void CheckBounds(string step)
                    {
                        InteractionAssert(WorkspaceGetWindowRect(handle, out var actual), "Cannot read the workspace HWND bounds.");
                        InteractionAssert(actual == display.Monitor && (bool)KeyboardField(window, "_isWindowMaximized"),
                            $"{mode}/video={video}/{step}: fullscreen bounds changed: expected={display.Monitor}, actual={actual}, " +
                            $"maximized={KeyboardField(window, "_isWindowMaximized")}, WPF={window.Left},{window.Top},{window.Width},{window.Height}.");
                    }
                }
                finally
                {
                    SetKeyboardField(vm, "_isCapturing", false);
                    if (KeyboardField(window, "_deviceHubPanel") is IDisposable hub) hub.Dispose();
                    CloseWorkspaceTestWindow(window);
                }
            }
            return 0;
        }
        finally { app.Shutdown(); _ = WorkspaceSetDpiAwareness(previousDpiContext); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct WorkspaceNativeRect(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkspaceDisplayInfo
    {
        internal uint Size;
        internal WorkspaceNativeRect Monitor, WorkArea;
        internal uint Flags;
    }

    private delegate bool WorkspaceMonitorCallback(nint monitor, nint dc, ref WorkspaceNativeRect bounds, nint data);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WorkspaceEnumMonitors(nint dc, nint clip, WorkspaceMonitorCallback callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WorkspaceGetMonitorInfo(nint monitor, ref WorkspaceDisplayInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WorkspaceGetWindowRect(nint handle, out WorkspaceNativeRect bounds);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WorkspaceSetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint WorkspaceGetDpi(nint handle);
    [DllImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")]
    private static extern nint WorkspaceSetDpiAwareness(nint context);
}
