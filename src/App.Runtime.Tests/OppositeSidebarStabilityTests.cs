using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunOppositeSidebarStabilityTests()
    {
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            foreach (var fromRight in new[] { false, true })
            {
                var card = new Border();
                var viewport = new IPhoneMirror.App.Controls.WorkspaceRevealPanel
                {
                    ContentWidth = 362, Width = 362,
                    HorizontalAlignment = fromRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                };
                viewport.Children.Add(card);
                var frame = new Grid(); frame.Children.Add(viewport);
                foreach (var width in new[] { 1d, 20d, 80d, 160d, 240d, 362d, 100d, 1d })
                {
                    viewport.MaxWidth = width;
                    frame.Measure(new Size(width, 500)); frame.Arrange(new Rect(0, 0, width, 500));
                    var bounds = viewport.TransformToAncestor(frame).TransformBounds(new Rect(viewport.RenderSize));
                    var content = card.TransformToAncestor(viewport).TransformBounds(new Rect(card.RenderSize));
                    InteractionAssert(bounds.Left >= -.5 && bounds.Right <= width + .5,
                        "The animated viewport is centered outside its constrained column.");
                    InteractionAssert(Math.Abs(content.Left) < .5 && Math.Abs(content.Width - 362) < .5,
                        "The card content must stay stationary while the viewport width changes.");
                }
            }
            Console.WriteLine("PASS: constrained viewports remain inside their columns; left/right content anchors remain fixed through growth and shrink.");
            foreach (var mode in new[] { ApplicationDisplayMode.Lightweight, ApplicationDisplayMode.Complete })
            {
                SetApplicationDisplayMode(app, mode);
                var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                try
                {
                    var type = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
                    void Left(bool open) => KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(type, open ? "Devices" : "None"));
                    void Settings(bool open) => KeyboardCall(window, "SetSettingsPanelVisible", open);
                    if (mode == ApplicationDisplayMode.Lightweight)
                    {
                        var vm = KeyboardField(window, "_viewModel");
                        foreach (var video in new[] { false, true })
                        {
                            SetKeyboardField(vm, "_isCapturing", video);
                            SetKeyboardField(vm, "_sourceVideoWidth", video ? 1082u : 0u);
                            SetKeyboardField(vm, "_sourceVideoHeight", video ? 2340u : 0u);
                            foreach (var handler in new[] { "OnNavigateDevicesClick", "OnNavigateSettingsClick" })
                            {
                                foreach (var (progress, initiallyOpen) in new[] { (.15d, false), (.55d, false), (.85d, false), (.55d, true) })
                                {
                                    Left(false); Settings(false); FinishWorkspaceReveal(window);
                                    var preview = (FrameworkElement)window.FindName("PreviewPanel");
                                    if (initiallyOpen)
                                    {
                                        KeyboardCall(window, handler, window, new RoutedEventArgs());
                                        FinishWorkspaceReveal(window);
                                    }
                                    KeyboardCall(window, "QueueLightweightPreviewWidth");
                                    KeyboardCall(window, handler, window, new RoutedEventArgs());
                                    window.UpdateLayout();
                                    KeyboardCall(window, "ApplyWorkspaceTransitionFrame", progress);
                                    window.UpdateLayout();
                                    var beforePreview = preview.ActualWidth;
                                    var beforeTarget = (double)KeyboardField(window, "_lightweightWindowWidthTargetDips");
                                    for (var click = 0; click < 12; click++)
                                    {
                                        KeyboardCall(window, handler, window, new RoutedEventArgs());
                                        window.UpdateLayout();
                                        InteractionAssert(Math.Abs(preview.ActualWidth - beforePreview) < 2,
                                            $"{handler}: interruption changed preview width {beforePreview:F2} -> {preview.ActualWidth:F2}.");
                                    }
                                    var afterTarget = (double)KeyboardField(window, "_lightweightWindowWidthTargetDips");
                                    InteractionAssert(Math.Abs(beforeTarget - afterTarget) < 2,
                                        $"Repeated interruptions accumulated window target width: {beforeTarget:F2} -> {afterTarget:F2}.");
                                    FinishWorkspaceReveal(window);
                                    var settled = preview.ActualWidth;
                                    AdvanceDispatcher(TimeSpan.FromMilliseconds(300)); window.UpdateLayout();
                                    InteractionAssert(Math.Abs(preview.ActualWidth - settled) < 2,
                                        "An obsolete queued fit changed the settled preview.");
                                }
                            }
                        }
                        SetKeyboardField(vm, "_isCapturing", false);
                        SetKeyboardField(vm, "_sourceVideoWidth", 0u);
                        SetKeyboardField(vm, "_sourceVideoHeight", 0u);
                        Console.WriteLine("PASS: preview and native window target remain continuous through repeated mid-frame reversals.");
                    }
                    foreach (var handler in new[] { "OnNavigateDevicesClick", "OnNavigateSettingsClick" })
                    {
                        Left(false); Settings(false); FinishWorkspaceReveal(window);
                        var surface = (FrameworkElement)window.FindName(handler == "OnNavigateDevicesClick" ? "LeftPanelHost" : "ControlPanel");
                        for (var click = 0; click < 21; click++)
                        {
                            var before = Math.Min(surface.Width, surface.MaxWidth);
                            KeyboardCall(window, handler, window, new RoutedEventArgs());
                            InteractionAssert(Math.Abs(Math.Min(surface.Width, surface.MaxWidth) - before) < 2,
                                "Interrupting the same toggle jumped to a stale frame.");
                            // Some clicks share a dispatcher turn; others interrupt
                            // a running clock before the next Arrange.
                            if (click % 3 == 0) AdvanceDispatcher(TimeSpan.FromMilliseconds(18));
                        }
                        FinishWorkspaceReveal(window); DrainDispatcher(); window.UpdateLayout();
                        InteractionAssert(surface.IsVisible && surface.Width > 1,
                            "Odd repeated clicks did not leave the last requested open state.");
                        KeyboardCall(window, handler, window, new RoutedEventArgs());
                        FinishWorkspaceReveal(window); AdvanceDispatcher(TimeSpan.FromMilliseconds(300));
                        InteractionAssert(surface.Visibility == Visibility.Collapsed && surface.Width == 0,
                            "A superseded animation callback restored the closed sidebar.");
                    }
                    Console.WriteLine($"PASS {mode}: same-button 21 rapid clicks, same-turn interruption, reversal and late-callback final state.");
                    foreach (var existingLeft in new[] { true, false })
                    {
                        Left(false); Settings(false); FinishWorkspaceReveal(window);
                        window.MinWidth = 600; window.Width = 1000;
                        if (existingLeft) Left(true); else Settings(true);
                        FinishWorkspaceReveal(window); DrainDispatcher(); window.UpdateLayout();
                        var existing = (FrameworkElement)window.FindName(existingLeft ? "LeftPanelHost" : "ControlPanel");
                        var before = existing.ActualWidth;
                        var samples = new List<double>();
                        EventHandler sample = (_, _) => samples.Add(existing.ActualWidth);
                        window.LayoutUpdated += sample;
                        try
                        {
                            if (existingLeft) Settings(true); else Left(true);
                            var opening = (IPhoneMirror.App.Controls.WorkspaceRevealPanel)window.FindName(existingLeft ? "ControlPanel" : "LeftPanelHost");
                            InteractionAssert(Math.Abs(opening.ContentWidth - (existingLeft ? 362 : 300)) < 1,
                                "A newly opening card retained the old window's leftover width instead of preparing its final content width.");
                            FinishWorkspaceReveal(window); AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
                            if (existingLeft) Settings(false); else Left(false);
                            FinishWorkspaceReveal(window); AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
                        }
                        finally { window.LayoutUpdated -= sample; }
                        InteractionAssert(samples.Count > 2 && samples.All(width => Math.Abs(width - before) <= 2),
                            $"{mode}/{(existingLeft ? "left" : "right")}: opposite reveal changed existing card width {before} -> {samples.Min()}..{samples.Max()}.");
                        Console.WriteLine($"PASS {mode}: existing {(existingLeft ? "left" : "right")} sidebar remains {before:F1} DIP while opposite sidebar opens/closes ({samples.Count} layout samples).");
                    }
                }
                finally { CloseWorkspaceTestWindow(window); }
            }
            return 0;
        }
        finally { app.Shutdown(); }
    }
}
