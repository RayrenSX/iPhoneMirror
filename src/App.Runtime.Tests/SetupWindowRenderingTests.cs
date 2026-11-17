using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunSetupWindowRenderingTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var vm = new MainViewModel();
        var samples = new List<object>();
        // Use the real display render mode here: software-only UI snapshots
        // cannot exercise the layered HWND/compositor path being regressed.
        // Reverse the second pair so one-time WPF/graphics initialization does
        // not make the first rendering mode look artificially more expensive.
        foreach (var layered in new[] { true, false, false, true })
        {
            var store = new FirstRunSetupStore(Path.Combine(Path.GetFullPath(output), $"render-{layered}.json"));
            store.Save(new() { Step = SetupStep.Environment, Usage = SetupUsage.WiredOnly,
                Devices = [new() { Id = "fixture-phone", Name = "iPhone" }] });
            var window = new FirstRunSetupWindow(vm, store, previewOnly: true);
            if (layered)
            {
                window.AllowsTransparency = true;
                System.Windows.Shell.WindowChrome.SetWindowChrome(window, null);
            }
            try
            {
                window.Show(); ToolsPump(300);
                var handle = new WindowInteropHelper(window).Handle;
                var isLayered = (GetSetupWindowStyle(handle, -20).ToInt64() & 0x80000) != 0;
                if (isLayered != layered) throw new InvalidOperationException("Unexpected setup HWND composition mode.");
                var bar = (StackPanel)window.FindName("BusyBar");
                bar.Visibility = Visibility.Visible;
                foreach (UIElement dot in bar.Children)
                    dot.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(.25, .85, TimeSpan.FromMilliseconds(300))
                        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                var watch = Stopwatch.StartNew();
                var last = 0d;
                var maximumGap = 0d;
                var ticks = 0;
                var frame = new DispatcherFrame();
                var heartbeat = new DispatcherTimer(DispatcherPriority.Input)
                { Interval = TimeSpan.FromMilliseconds(16) };
                heartbeat.Tick += (_, _) =>
                {
                    var now = watch.Elapsed.TotalMilliseconds;
                    maximumGap = Math.Max(maximumGap, now - last);
                    last = now; ticks++;
                    if (now >= 2000) frame.Continue = false;
                };
                heartbeat.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { heartbeat.Stop(); }
                samples.Add(new { layered, renderingTier = RenderCapability.Tier >> 16,
                    elapsedMs = watch.Elapsed.TotalMilliseconds,
                    cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
                    maximumDispatcherGapMs = maximumGap, ticks });
                foreach (UIElement dot in bar.Children) dot.BeginAnimation(UIElement.OpacityProperty, null);
                bar.Visibility = Visibility.Collapsed;
                if (!layered)
                {
                    foreach (var state in new[] { WindowState.Minimized, WindowState.Normal,
                                 WindowState.Maximized, WindowState.Normal })
                    { window.WindowState = state; ToolsPump(100); }
                    ThemeService.Apply(IPhoneMirror.App.Updater.AppTheme.Dark);
                    ThemeService.Apply(IPhoneMirror.App.Updater.AppTheme.Light);
                    var surface = (Border)window.Template.FindName("RoundedWindowSurface", window);
                    if (surface.Effect is not null || surface.Background is not SolidColorBrush { Color.A: 255 })
                        throw new InvalidOperationException("Theme/state transition restored layered setup rendering.");
                    ToolsCapture(window, Path.Combine(output, "setup-environment-native.png"));
                }
            }
            finally
            {
                typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, true);
                window.Close();
            }
        }
        File.WriteAllText(Path.Combine(output, "rendering.json"),
            JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        VerifySetupAlignment(output, vm);
        app.Shutdown();
        Console.WriteLine("PASS default-render-mode setup HWND, animation, minimize/restore, maximize and theme transitions.");
        return 0;
    }

    private static void VerifySetupAlignment(string output, MainViewModel vm)
    {
        var originalTheme = ThemeService.Preference;
        var layouts = new List<object>();
        try
        {
            foreach (var theme in new[] { IPhoneMirror.App.Updater.AppTheme.Dark, IPhoneMirror.App.Updater.AppTheme.Light })
            foreach (var size in new[] { (Width: 600d, Height: 540d), (Width: 880d, Height: 820d) })
            foreach (var step in new[] { SetupStep.Welcome, SetupStep.MirrorConnection, SetupStep.Environment })
            {
                ThemeService.Apply(theme);
                var store = new FirstRunSetupStore(Path.Combine(Path.GetFullPath(output), $"alignment-{theme}-{size.Width}-{step}.json"));
                store.Save(new() { Step = step, Usage = SetupUsage.WiredOnly,
                    Devices = [new() { Id = "alignment-phone", Name = "iPhone" }] });
                var window = new FirstRunSetupWindow(vm, store, previewOnly: true)
                { Width = size.Width, Height = size.Height };
                try
                {
                    window.Show(); ToolsPump(120); window.UpdateLayout();
                    T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window)
                        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    double Center(FrameworkElement element) { var bounds = Bounds(element); return bounds.Left + bounds.Width / 2; }
                    void Close(double actual, double expected, string description)
                    {
                        if (Math.Abs(actual - expected) > 1)
                            throw new InvalidOperationException($"Setup {step}/{theme}/{size.Width}: {description}: {actual:F2} vs {expected:F2}.");
                    }
                    var page = Find<StackPanel>("Page");
                    var footer = Find<Grid>("SetupFooter");
                    var progress = Find<Grid>("StepIndicator");
                    Close(Center(progress), Center(page), "progress and page center");
                    Close(Center(footer), Center(page), "footer and page center");
                    Close(footer.ActualWidth, page.ActualWidth, "footer and content width");
                    Close(Center(Find<TextBlock>("Heading")), Center(page), "heading center");
                    var stages = progress.Children.OfType<StackPanel>().ToArray();
                    if (stages.Length != 3) throw new InvalidOperationException("Setup must show three progress stages.");
                    Close(Center(stages[1]), Center(page), "middle progress stage");
                    Close(stages[0].ActualWidth, stages[2].ActualWidth, "equal progress columns");
                    var previous = Find<Button>("Previous");
                    var next = Find<Button>("Next");
                    Close(previous.ActualWidth, next.ActualWidth, "equal navigation widths");
                    var skip = Find<Button>("Skip");
                    if (skip.IsVisible) Close(Center(skip), Center(footer), "centered skip action");
                    var body = Find<StackPanel>("Body");
                    if (step == SetupStep.Environment)
                    {
                        var card = body.Children.OfType<Button>().Single();
                        Close(Center(card), Center(page), "single tool card center");
                        Close(card.ActualWidth, body.ActualWidth, "single tool card width");
                    }
                    if (step == SetupStep.MirrorConnection)
                    {
                        var row = body.Children.OfType<WrapPanel>().Single();
                        var cards = row.Children.OfType<RadioButton>().ToArray();
                        if (cards.Length != 2) throw new InvalidOperationException("Connection choices must retain both transport cards.");
                        var left = Bounds(cards[0]); var right = Bounds(cards[1]);
                        Close((left.Left + right.Right) / 2, Center(page), "balanced connection-card gutter");
                        Close(left.Width, right.Width, "equal connection-card width");
                        if (left.IntersectsWith(right)) throw new InvalidOperationException("Connection choices overlap.");
                    }
                    foreach (var action in new[] { previous, next, skip, Find<Button>("CancelOperation") }.Where(button => button.IsVisible))
                    {
                        var bounds = Bounds(action);
                        if (bounds.Left < 0 || bounds.Right > window.ActualWidth || bounds.Top < 0 || bounds.Bottom > window.ActualHeight)
                            throw new InvalidOperationException("Setup navigation is clipped at the minimum window size.");
                    }
                    ToolsCapture(window, Path.Combine(output, $"setup-aligned-{theme}-{size.Width}-{step}.png"));
                    layouts.Add(new { theme = theme.ToString(), step = step.ToString(), width = window.ActualWidth,
                        height = window.ActualHeight, contentWidth = page.ActualWidth, center = Center(page) });
                }
                finally
                {
                    typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                    window.Close();
                }
            }
        }
        finally { ThemeService.Apply(originalTheme); }
        File.WriteAllText(Path.Combine(output, "alignment.json"),
            JsonSerializer.Serialize(layouts, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS setup alignment: centered progress/content/footer, balanced cards and navigation at minimum/default sizes in both themes.");
    }
}
