using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWorkspaceMotionTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Lightweight);
            var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
            var vm = KeyboardField(window, "_viewModel");
            try
            {
                var preview = (FrameworkElement)window.FindName("PreviewPanel");
                var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
                foreach (var video in new[] { false, true })
                {
                    SetKeyboardField(vm, "_isCapturing", video);
                    SetKeyboardField(vm, "_sourceVideoWidth", video ? 1082u : 0u);
                    SetKeyboardField(vm, "_sourceVideoHeight", video ? 2340u : 0u);
                    foreach (var handler in new[] { "OnNavigateSettingsClick", "OnNavigateDevicesClick" })
                    foreach (var opening in new[] { true, false })
                    {
                        KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "None"));
                        KeyboardCall(window, "SetSettingsPanelVisible", false);
                        FinishWorkspaceReveal(window);
                        if (!opening)
                        {
                            KeyboardCall(window, handler, window, new RoutedEventArgs());
                            FinishWorkspaceReveal(window);
                        }
                        var rows = new List<string> { "ms,preview,limit,window,left,right,running" };
                        var widths = new List<(double Time, double Width, double Limit)>();
                        var trackingErrors = new List<double>();
                        var watch = Stopwatch.StartNew();
                        EventHandler sample = (_, _) =>
                        {
                            var time = watch.Elapsed.TotalMilliseconds;
                            widths.Add((time, preview.ActualWidth, preview.MaxWidth));
                            if ((bool)KeyboardField(window, "_workspacePreviewAnimationActive"))
                                trackingErrors.Add(Math.Abs(preview.ActualWidth - preview.MaxWidth));
                            rows.Add($"{time:F2},{preview.ActualWidth:F3},{preview.MaxWidth:F3},{window.ActualWidth:F3},{((FrameworkElement)window.FindName("LeftPanelHost")).ActualWidth:F3},{((FrameworkElement)window.FindName("ControlPanel")).ActualWidth:F3},{WorkspaceRevealRunning(window)}");
                        };
                        CompositionTarget.Rendering += sample;
                        try
                        {
                            KeyboardCall(window, handler, window, new RoutedEventArgs());
                            FinishWorkspaceReveal(window);
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                        }
                        finally { CompositionTarget.Rendering -= sample; }
                        File.WriteAllLines(Path.Combine(output, $"video-{video}-{handler}-{opening}.csv"), rows);
                        var maximumError = trackingErrors.DefaultIfEmpty(0).Max();
                        InteractionAssert(maximumError <= 2, $"Preview stopped following the active animation: {maximumError:F2} DIP error.");
                        Console.WriteLine($"video={video}/{handler}/{opening}: {widths.Count} rendered frames; maximum tracking error {maximumError:F2} DIP.");
                        // Reach the exact last animated frame, then run settlement.
                        // The two frames must agree even when the last sidebar closes
                        // and the minimum window width leaves room for a wider preview.
                        KeyboardCall(window, handler, window, new RoutedEventArgs());
                        KeyboardCall(window, "ApplyWorkspaceTransitionFrame", 1d);
                        window.UpdateLayout();
                        var lastFrameWidth = preview.ActualWidth;
                        KeyboardCall(window, "CompleteWorkspaceTransition");
                        window.UpdateLayout();
                        InteractionAssert(Math.Abs(preview.ActualWidth - lastFrameWidth) <= 2,
                            $"{handler}/{!opening}: completion jumped preview width {lastFrameWidth:F2} -> {preview.ActualWidth:F2}.");
                        Console.WriteLine($"PASS: completion width {lastFrameWidth:F2} -> {preview.ActualWidth:F2} DIP.");
                    }
                }
                return 0;
            }
            finally
            {
                SetKeyboardField(vm, "_isCapturing", false);
                CloseWorkspaceTestWindow(window);
            }
        }
        finally { app.Shutdown(); }
    }
}
