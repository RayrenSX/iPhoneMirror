using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunLightweightPreviewBoundsTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Lightweight);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        var preview = (FrameworkElement)window.FindName("PreviewPanel");
        var group = (FrameworkElement)window.FindName("MainContentGrid");
        var root = (FrameworkElement)window.FindName("RootLayout");
        var left = (FrameworkElement)window.FindName("LeftPanelHost");
        var right = (FrameworkElement)window.FindName("ControlPanel");
        void Layout() { DrainDispatcher(); window.UpdateLayout(); }
        void Check(string state)
        {
            Layout();
            var expectedGroupWidth = Math.Min(root.ActualWidth, group.MaxWidth);
            InteractionAssert(Math.Abs(group.ActualWidth - expectedGroupWidth) <= 2,
                $"{state}: maximized group shrank to its text instead of its width limit: {group.ActualWidth:F1}/{expectedGroupWidth:F1}.");
            var available = group.ActualWidth - (left.IsVisible ? left.ActualWidth + 18 : 0) -
                (right.IsVisible ? right.ActualWidth + 18 : 0);
            var expectedPreview = Math.Min(available, preview.MaxWidth);
            InteractionAssert(preview.ActualWidth > 0 && Math.Abs(preview.ActualWidth - expectedPreview) <= 2,
                $"{state}: preview collapsed to its message width: {preview.ActualWidth:F1}/{expectedPreview:F1}.");
            var x = group.TranslatePoint(new Point(), root).X;
            InteractionAssert(Math.Abs(x * 2 + group.ActualWidth - root.ActualWidth) <= 2,
                "The expanded card group stopped centering.");
            var bounds = preview.TransformToAncestor(group).TransformBounds(new Rect(preview.RenderSize));
            InteractionAssert(bounds.Left >= 0 && bounds.Right <= group.ActualWidth + 1 && preview.MinWidth == 0,
                "The preview width limit caused overlap or a hard child minimum.");
        }
        try
        {
            foreach (var readingScale in new[] { 1d, 1.5 })
            {
                IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, readingScale, false);
                KeyboardCall(window, "MaximizeWindow", (Rect?)null); Layout();
                foreach (var (showLeft, showRight) in new[] { (true, true), (false, true), (true, false), (false, false) })
                {
                    KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, showLeft ? "Devices" : "None"));
                    KeyboardCall(window, "SetSettingsPanelVisible", showRight); FinishWorkspaceReveal(window); Layout();
                    foreach (var state in new[] { "waiting", "stopped", "video", "stopped-after-video" })
                    {
                        var video = state == "video";
                        SetKeyboardField(vm, "_selectedDevice", state == "waiting" ? null : DeviceViewModel.FromNative(
                            new NativeDeviceInfo { Udid = "preview-width-fixture", Name = "Ray’s Phone", State = ConnectionState.Ready }));
                        SetKeyboardField(vm, "_sourceVideoWidth", state == "waiting" ? 0u : 1082u);
                        SetKeyboardField(vm, "_sourceVideoHeight", state == "waiting" ? 0u : 2340u);
                        SetKeyboardField(vm, "_isCapturing", video);
                        KeyboardCall(window, "ApplyApplicationDisplayMode");
                        Check(state);
                        if (!video && (showLeft || showRight))
                            InteractionAssert(Math.Abs(preview.ActualWidth - 340) <= 2,
                                $"{state}: idle preview no longer keeps its 340 DIP portrait width.");
                        SaveWindowRender(window, Path.Combine(output, $"{readingScale:0.0}-left-{showLeft}-right-{showRight}-{state}.png"));
                    }
                }
                KeyboardCall(window, "ToggleFullScreen"); Layout();
                InteractionAssert(preview.ActualWidth >= root.ActualWidth - 2,
                    "Fullscreen retained the maximized group's requested width.");
                KeyboardCall(window, "ToggleFullScreen"); Layout(); Check("fullscreen-restored");
                KeyboardCall(window, "RestoreWindowFromMaximized"); Layout();
                InteractionAssert(double.IsNaN(group.Width), "Normal mode retained the maximized group's fixed request.");
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete; Layout();
                InteractionAssert(double.IsNaN(group.Width) && double.IsPositiveInfinity(group.MaxWidth),
                    "Complete mode retained the lightweight group width.");
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight; Layout();
                Console.WriteLine($"PASS {readingScale:P0} text: four sidebar combinations, waiting/stopped/video/stopped-after-video, centered requested width, preview containment, fullscreen/maximize/mode restore.");
            }
            return 0;
        }
        finally { SetKeyboardField(vm, "_isCapturing", false); CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }
}
