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
        app.Shutdown();
        Console.WriteLine("PASS default-render-mode setup HWND, animation, minimize/restore, maximize and theme transitions.");
        return 0;
    }
}
