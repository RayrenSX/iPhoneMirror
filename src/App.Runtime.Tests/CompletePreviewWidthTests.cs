using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunCompletePreviewWidthTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        var preview = (Border)window.FindName("PreviewPanel");
        var hwnd = new WindowInteropHelper(window).Handle;
        var scale = RepairGetDpiForWindow(hwnd) / 96d;
        void Layout() { DrainDispatcher(); window.UpdateLayout(); }
        double PictureWidth() => Math.Min(preview.ActualWidth - 2,
            (preview.ActualHeight - 2) * vm.SourceVideoWidth / vm.SourceVideoHeight);
        int[] Bounds()
        {
            PreviewStyleGetWindowRect(hwnd, out var rect);
            return [rect.Left, rect.Top, rect.Right, rect.Bottom];
        }
        void ApplyWidth(int width)
        {
            var rect = Bounds();
            InteractionAssert(PerfSetWindowPos(hwnd, 0, 0, 0, width, rect[3] - rect[1], 0x0016), "Native resize failed.");
            Layout();
        }
        try
        {
            foreach (var (sourceWidth, sourceHeight) in new[] { (1082u, 2340u), (2340u, 1082u) })
            foreach (var (left, right) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                SetKeyboardField(vm, "_isCapturing", false);
                window.Width = 1540; window.Height = 900;
                KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, left ? "Devices" : "None"));
                KeyboardCall(window, "SetSettingsPanelVisible", right);
                FinishWorkspaceReveal(window); Layout();
                SetKeyboardField(vm, "_sourceVideoWidth", sourceWidth);
                SetKeyboardField(vm, "_sourceVideoHeight", sourceHeight);
                SetKeyboardField(vm, "_isCapturing", true);
                KeyboardCall(window, "ApplyApplicationDisplayMode"); Layout();
                var initial = Bounds();
                var originalPicture = PictureWidth();
                var expected = Math.Max(480, window.ActualWidth - preview.ActualWidth + originalPicture + 2);
                expected = Math.Min(expected, IPhoneMirror.SharedUI.Services.WindowWorkAreaController.GetAvailableSize(window).Width);
                var minimumPixels = (int)Math.Ceiling(expected * scale - .001);
                foreach (var edge in new[] { 1, 2, 4, 5, 7, 8 })
                {
                    // Submit a proposal beyond the picture, on each horizontal
                    // and corner grip. The opposite edge must stay anchored.
                    var rect = (int[])initial.Clone();
                    if (edge is 1 or 4 or 7) rect[0] = rect[2] - minimumPixels + 100;
                    else rect[2] = rect[0] + minimumPixels - 100;
                    var memory = Marshal.AllocHGlobal(16);
                    try
                    {
                        Marshal.Copy(rect, 0, memory, 4);
                        InteractionAssert(ResizeSendMessage(hwnd, 0x0214, edge, memory) == 1,
                            "The native sizing hook did not stop at the picture boundary.");
                        Marshal.Copy(memory, rect, 0, 4);
                        InteractionAssert(Math.Abs(rect[2] - rect[0] - minimumPixels) <= 1 &&
                            (edge is 1 or 4 or 7 ? rect[2] == initial[2] : rect[0] == initial[0]) &&
                            rect[1] == initial[1] && rect[3] == initial[3],
                            $"The picture boundary or anchored edge moved for grip {edge}.");
                    }
                    finally { Marshal.FreeHGlobal(memory); }
                }
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    ApplyWidth(minimumPixels - 100);
                    InteractionAssert(PictureWidth() >= originalPicture - 2 &&
                        Math.Abs(window.ActualWidth * scale - minimumPixels) <= 2,
                        $"Repeated shrinking compressed the picture or drifted: {sourceWidth}x{sourceHeight}, left={left}, right={right}, attempt={attempt}, picture={PictureWidth():F2}/{originalPicture:F2}, window={window.ActualWidth * scale:F2}/{minimumPixels}, preview={preview.ActualWidth:F2}x{preview.ActualHeight:F2}, reserve={KeyboardField(window, "_completeResizePreviewWidth")}.");
                }
                // Grow and remove the new black space again, without shrinking
                // the original picture or changing the preview height.
                var originalHeight = preview.ActualHeight;
                ApplyWidth(minimumPixels + 80);
                var grownPicture = PictureWidth();
                ApplyWidth(minimumPixels - 80);
                InteractionAssert(PictureWidth() >= grownPicture - 2 &&
                    Math.Abs(preview.ActualHeight - originalHeight) < 1,
                    $"Growing/shrinking compressed the displayed picture: {PictureWidth():F2}/{grownPicture:F2}, height={preview.ActualHeight:F2}/{originalHeight:F2}.");
                var resized = Bounds();
                var reducedHeight = resized[3] - resized[1] - (int)Math.Round(100 * scale);
                var expectedPictureAfterHeightChange = Math.Min(preview.ActualWidth - 2,
                    (preview.ActualHeight - 102) * sourceWidth / sourceHeight);
                InteractionAssert(PerfSetWindowPos(hwnd, 0, 0, 0, (int)Math.Round(800 * scale), reducedHeight, 0x0016),
                    "Could not resize height at the picture boundary.");
                Layout();
                InteractionAssert(Math.Abs(PictureWidth() - expectedPictureAfterHeightChange) <= 2,
                    $"Height change did not update the width boundary: {PictureWidth():F2}/{expectedPictureAfterHeightChange:F2}.");
                SaveWindowRender(window, Path.Combine(output, $"boundary-{sourceWidth}x{sourceHeight}-left-{left}-right-{right}.png"));
                Console.WriteLine($"PASS source={sourceWidth}x{sourceHeight}, left={left}, right={right}: six grips, anchored edges, five repeated native shrink attempts, picture width={PictureWidth():F1} DIP.");
            }
            var before = Bounds();
            KeyboardCall(window, "ToggleFullScreen"); Layout();
            InteractionAssert((bool)KeyboardField(window, "_isFullScreen"), "Cannot enter fullscreen with the width limit enabled.");
            KeyboardCall(window, "ToggleFullScreen"); Layout();
            var restored = Bounds();
            InteractionAssert(Math.Abs(restored[2] - restored[0] - before[2] + before[0]) <= 2,
                "The width floor changed the fullscreen restore width.");
            KeyboardCall(window, "MaximizeWindow", (Rect?)null); Layout();
            KeyboardCall(window, "RestoreWindowFromMaximized"); Layout();
            restored = Bounds();
            InteractionAssert(Math.Abs(restored[2] - restored[0] - before[2] + before[0]) <= 2,
                "The width floor changed the maximize restore width.");
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight; Layout();
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete; Layout();
            restored = Bounds();
            InteractionAssert(Math.Abs(restored[2] - restored[0] - before[2] + before[0]) <= 2,
                "The width floor changed the complete/lightweight restore width.");
            SetKeyboardField(vm, "_isCapturing", false);
            KeyboardCall(window, "ApplyApplicationDisplayMode");
            ApplyWidth((int)Math.Round(800 * scale));
            InteractionAssert(Math.Abs(window.ActualWidth - 800) <= 2,
                "Stopping video retained the picture width floor.");
            Console.WriteLine("PASS: height changes update the boundary; fullscreen/maximize/mode restore and stopping video release their temporary limits.");
            return 0;
        }
        finally { SetKeyboardField(vm, "_isCapturing", false); CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }
}
