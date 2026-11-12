using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPreviewEdgeAnchorTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            foreach (var mode in new[] { ApplicationDisplayMode.Lightweight, ApplicationDisplayMode.Complete })
            {
                SetApplicationDisplayMode(app, mode);
                var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                var vm = KeyboardField(window, "_viewModel");
                try
                {
                    var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
                    var preview = (FrameworkElement)window.FindName("PreviewPanel");
                    var center = (FrameworkElement)window.FindName("CenterPanel");
                    var right = (FrameworkElement)window.FindName("ControlPanel");
                    var rightGap = (ColumnDefinition)window.FindName("RightGapColumn");
                    foreach (var video in new[] { false, true })
                    foreach (var leftOpen in new[] { false, true })
                    {
                        SetKeyboardField(vm, "_isCapturing", video);
                        SetKeyboardField(vm, "_sourceVideoWidth", video ? 1082u : 0u);
                        SetKeyboardField(vm, "_sourceVideoHeight", video ? 2340u : 0u);
                        KeyboardCall(window, "SetSettingsPanelVisible", false);
                        KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, leftOpen ? "Devices" : "None"));
                        FinishWorkspaceReveal(window); DrainDispatcher(); window.UpdateLayout();
                        // Leave spare center space to exercise capped source-aspect
                        // previews as well as the ordinary fitted window size.
                        window.Width = mode == ApplicationDisplayMode.Lightweight ? 1200 : 1500;
                        window.UpdateLayout(); DrainDispatcher(); window.UpdateLayout();
                        var startX = preview.PointToScreen(new Point()).X;
                        var centerX = center.PointToScreen(new Point()).X;
                        var samples = new List<double>();
                        var gutterErrors = new List<double>();
                        void Check(string stage)
                        {
                            var x = preview.PointToScreen(new Point()).X;
                            samples.Add(x);
                            if (right.IsVisible && right.ActualWidth > 1)
                            {
                                var previewRight = preview.TranslatePoint(new Point(preview.ActualWidth, 0), window).X;
                                var separation = right.TranslatePoint(new Point(), window).X - previewRight;
                                gutterErrors.Add(Math.Abs(separation - rightGap.ActualWidth));
                                if (Math.Abs(separation - rightGap.ActualWidth) > 2)
                                    SaveWindowRender(window, Path.Combine(output, "unexpected-gap.png"));
                                InteractionAssert(Math.Abs(separation - rightGap.ActualWidth) <= 2,
                                    $"{mode}/video={video}/left={leftOpen}/{stage}: preview/sidebar gap {separation:F2} exceeds animated gutter {rightGap.ActualWidth:F2}.");
                            }
                            InteractionAssert(Math.Abs(preview.ActualWidth - Math.Min(center.ActualWidth, preview.MaxWidth)) <= 2,
                                "Anchoring must preserve preview fill up to the source width limit.");
                            var expectedX = startX;
                            if (mode == ApplicationDisplayMode.Lightweight && !leftOpen)
                            {
                                // Both-closed is intentionally centered. Once a
                                // card starts opening, its preview uses the left
                                // anchor again so the cards retain their gutter.
                                var offset = !right.IsVisible && !WorkspaceRevealRunning(window)
                                    ? (center.ActualWidth - preview.ActualWidth) / 2 : 0;
                                expectedX = center.PointToScreen(new Point(offset, 0)).X;
                            }
                            InteractionAssert(Math.Abs(x - expectedX) <= 2,
                                $"{mode}/video={video}/left={leftOpen}/{stage}: right reveal moved preview left edge {startX:F2} -> {x:F2}; center {centerX:F2} -> {center.PointToScreen(new Point()).X:F2}.");
                        }
                        for (var toggle = 0; toggle < 6; toggle++)
                        {
                            KeyboardCall(window, "OnNavigateSettingsClick", window, new RoutedEventArgs());
                            window.UpdateLayout(); Check($"toggle-{toggle}");
                            foreach (var progress in new[] { .1, .3, .5, .7 })
                            {
                                KeyboardCall(window, "ApplyWorkspaceTransitionFrame", progress);
                                window.UpdateLayout(); Check($"toggle-{toggle}/{progress}");
                                if (mode == ApplicationDisplayMode.Lightweight && !video && !leftOpen && toggle < 2)
                                    SaveWindowRender(window, Path.Combine(output, $"intermediate-{toggle}-{progress:F1}.png"));
                            }
                            // Reverse before completion; the final two operations
                            // also cover fully settled open and closed states.
                            if (toggle >= 4)
                            {
                                FinishWorkspaceReveal(window); Check($"settled-{toggle}");
                            }
                        }
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(300)); window.UpdateLayout(); Check("late");
                        SaveWindowRender(window, Path.Combine(output, $"{mode}-video-{video}-left-{leftOpen}.png"));
                        Console.WriteLine($"PASS {mode}/video={video}/left={leftOpen}: preview left edge {samples.Min():F2}..{samples.Max():F2} px; maximum gutter error {gutterErrors.DefaultIfEmpty(0).Max():F2} DIP through {samples.Count} samples of right-panel reversal and completion.");
                    }
                }
                finally
                {
                    SetKeyboardField(vm, "_isCapturing", false);
                    CloseWorkspaceTestWindow(window);
                }
            }
            return 0;
        }
        finally { app.Shutdown(); }
    }
}
