using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;
using IPhoneMirror.UI.Animations;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Real WPF layout in preview mode: no device discovery or user settings writes.
    private static int RunUiPerformanceAudit(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, true);
        app.InitializeComponent();
        var failures = new List<string>();
        var metrics = new Dictionary<string, object>();
        void Verify(bool condition, string message) { if (!condition) failures.Add(message); }
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        try
        {
            var original = new ScaleTransform(0.9, 0.9);
            var page = new Border { Width = 100, Height = 80, Opacity = 0.65, RenderTransform = original };
            var host = new Window { Owner = owner, ShowInTaskbar = false, Width = 200, Height = 160, Content = page };
            try
            {
                PageTransition.SetIsEnabled(page, true);
                PageTransition.SetIsEnabled(page, false);
                host.Show();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                Verify(ReferenceEquals(page.RenderTransform, original), "Disabled page transition ran on Loaded.");
                Verify(Math.Abs(page.Opacity - 0.65) < 0.001, "Page transition overwrote content opacity.");
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    PageTransition.SetIsEnabled(page, true);
                    page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                    PageTransition.SetIsEnabled(page, false);
                    var group = page.RenderTransform as TransformGroup;
                    var offset = group?.Children.OfType<TranslateTransform>().SingleOrDefault();
                    Verify(group?.Children.Count == 2 && ReferenceEquals(group.Children[0], original),
                        "Page transition duplicated or replaced the existing transform.");
                    Verify(offset is not null && Math.Abs(offset.Y) < 0.001,
                        "Disabling a page transition left its animation running.");
                }
            }
            finally { host.Close(); }

            owner.Opacity = 0.63;
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.Light })
            {
                ApplyTheme(typeof(App).Assembly, theme);
                var themeWait = Stopwatch.StartNew();
                do { AdvanceDispatcher(TimeSpan.FromMilliseconds(100)); }
                while (Math.Abs(owner.Opacity - 0.63) > 0.001 && themeWait.Elapsed < TimeSpan.FromSeconds(4));
                Verify(Math.Abs(owner.Opacity - 0.63) < 0.001,
                    $"{theme} theme transition overwrote window opacity: {owner.Opacity:F3}.");
            }
            owner.BeginAnimation(UIElement.OpacityProperty, null);
            owner.Opacity = 1;

            var viewModel = typeof(MainWindow).GetField("_viewModel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            var about = (AboutWindow)Activator.CreateInstance(typeof(AboutWindow),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [app, viewModel], null)!;
            about.Owner = owner;
            var timer = (DispatcherTimer)typeof(AboutWindow).GetField("_logTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(about)!;
            var ticks = 0;
            EventHandler countTick = (_, _) => ticks++;
            timer.Tick += countTick;
            try
            {
                about.Show();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(650));
                metrics["aboutOverviewLogTicks650ms"] = ticks;
                Verify(!timer.IsEnabled && ticks == 0, "About overview polls invisible diagnostic logs.");
                var tabs = (TabControl)about.FindName("AboutTabs");
                tabs.SelectedIndex = 2;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(650));
                Verify(timer.IsEnabled && ticks > 0, "Visible diagnostics no longer refresh.");
                tabs.SelectedIndex = 1;
                Verify(!timer.IsEnabled, "Update settings keep diagnostic polling active.");
                tabs.SelectedIndex = 2;
                about.Hide();
                Verify(!timer.IsEnabled, "Hidden About window keeps polling.");
                about.Show();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                Verify(timer.IsEnabled, "Showing diagnostics does not resume polling.");
                about.WindowState = WindowState.Minimized;
                Verify(!timer.IsEnabled, "Minimized About window keeps polling.");
                about.WindowState = WindowState.Normal;
                Verify(timer.IsEnabled, "Restoring diagnostics does not resume polling.");
            }
            finally { about.Close(); timer.Tick -= countTick; }
            Verify(!timer.IsEnabled, "Closed About window keeps polling.");

            var open = typeof(MainWindow).GetMethod("OpenDeveloperSurface", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var samples = new List<double>();
            var surfaceSamples = new Dictionary<string, List<double>>();
            long allocated = 0;
            foreach (var cycle in Enumerable.Range(0, 7))
            foreach (var surface in new[] { "workspace-mirroring", "workspace-devices", "workspace-settings", "workspace-output" })
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                open.Invoke(owner, [surface]);
                owner.UpdateLayout();
                DrainDispatcher();
                watch.Stop();
                if (cycle > 0)
                {
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                    if (!surfaceSamples.TryGetValue(surface, out var values))
                        surfaceSamples[surface] = values = [];
                    values.Add(watch.Elapsed.TotalMilliseconds);
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                }
                AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                foreach (var child in app.Windows.Cast<Window>().Where(w => w != owner).ToArray())
                    child.Close();
            }
            samples.Sort();
            metrics["workspaceSwitchSamples"] = samples.Count;
            metrics["workspaceSwitchMedianMs"] = samples[samples.Count / 2];
            metrics["workspaceSwitchP95Ms"] = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
            metrics["workspaceSwitchUiThreadAllocatedBytes"] = allocated;
            metrics["workspaceActions"] = surfaceSamples.ToDictionary(pair => pair.Key, pair =>
            {
                var ordered = pair.Value.Order().ToArray();
                return new { samples = ordered.Length, medianMs = ordered[ordered.Length / 2], maxMs = ordered[^1] };
            });
            // Tiered JIT continues optimizing code after the window exercise.
            // Let this bounded startup work settle before sampling idle CPU.
            AdvanceDispatcher(TimeSpan.FromSeconds(10));
            using var process = Process.GetCurrentProcess();
            var threadCpuBefore = process.Threads.Cast<ProcessThread>().ToDictionary(t => t.Id, t => t.TotalProcessorTime);
            var posted = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            var methodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
            var layouts = 0;
            EventHandler onLayout = (_, _) => layouts++;
            DispatcherHookEventHandler onPosted = (_, e) =>
            {
                var callback = methodField?.GetValue(e.Operation) as Delegate;
                var key = callback is null ? e.Operation.Priority.ToString() : $"{callback.Method.DeclaringType?.FullName}.{callback.Method.Name}";
                posted.AddOrUpdate(key, 1, (_, count) => count + 1);
            };
            owner.LayoutUpdated += onLayout;
            owner.Dispatcher.Hooks.OperationPosted += onPosted;
            var cpuBefore = process.TotalProcessorTime;
            var idleWatch = Stopwatch.StartNew();
            AdvanceDispatcher(TimeSpan.FromSeconds(2));
            metrics["previewIdleCpuMs"] = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            metrics["previewIdleWallMs"] = idleWatch.Elapsed.TotalMilliseconds;
            owner.LayoutUpdated -= onLayout;
            owner.Dispatcher.Hooks.OperationPosted -= onPosted;
            metrics["previewIdleLayouts"] = layouts;
            metrics["previewIdleDispatcherCallbacks"] = posted.OrderByDescending(p => p.Value).Take(8).ToArray();
            process.Refresh();
            metrics["previewIdleThreadCpuMs"] = process.Threads.Cast<ProcessThread>()
                .Where(t => threadCpuBefore.ContainsKey(t.Id))
                .Select(t => new { id = t.Id, description = AuditThreadDescription(t.Id), ms = (t.TotalProcessorTime - threadCpuBefore[t.Id]).TotalMilliseconds })
                .OrderByDescending(t => t.ms).Take(5).ToArray();
            metrics["managedHeapBytes"] = GC.GetTotalMemory(forceFullCollection: false);
            metrics["rendering"] = $"WPF default rendering, tier {RenderCapability.Tier >> 16}, preview fixtures, no live media; actions include opening the output dialog and exclude animation wait";
        }
        finally
        {
            CloseWorkspaceTestWindow(owner);
            app.Shutdown();
            File.WriteAllText(Path.Combine(output, "performance-results.json"), JsonSerializer.Serialize(
                new { metrics, failures }, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine(JsonSerializer.Serialize(metrics));
        Console.WriteLine($"UI performance and lifecycle audit: {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static string AuditThreadDescription(int id)
    {
        var thread = AuditOpenThread(0x0800, false, (uint)id);
        if (thread == IntPtr.Zero) return string.Empty;
        try
        {
            if (AuditGetThreadDescription(thread, out var name) < 0) return string.Empty;
            try { return Marshal.PtrToStringUni(name) ?? string.Empty; }
            finally { AuditLocalFree(name); }
        }
        finally { AuditCloseHandle(thread); }
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenThread")]
    private static extern IntPtr AuditOpenThread(uint access, bool inheritHandle, uint id);
    [DllImport("kernel32.dll", EntryPoint = "GetThreadDescription")]
    private static extern int AuditGetThreadDescription(IntPtr thread, out IntPtr description);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr AuditLocalFree(IntPtr memory);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    private static extern bool AuditCloseHandle(IntPtr handle);
}
