using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int MeasureLiveWindowInteraction(MainWindow window, MainViewModel vm, string output)
    {
        var type = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
        KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(type, "Devices"));
        KeyboardCall(window, "SetSettingsPanelVisible", true);
        FinishWorkspaceReveal(window); AdvanceDispatcher(TimeSpan.FromSeconds(2)); window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        var reports = new List<object>();
        using var process = Process.GetCurrentProcess();
        void Flush()
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            window.UpdateLayout();
        }
        void Measure(string name, Action<int> action, int steps)
        {
            var layouts = 0; var posted = 0;
            EventHandler onLayout = (_, _) => layouts++;
            DispatcherHookEventHandler onPosted = (_, _) => posted++;
            var samples = new List<double>();
            var actionSamples = new List<double>();
            window.LayoutUpdated += onLayout;
            window.Dispatcher.Hooks.OperationPosted += onPosted;
            var cpu = process.TotalProcessorTime;
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            try
            {
                for (var i = 0; i < steps; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    action(i);
                    actionSamples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    Flush();
                    samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
            }
            finally
            {
                window.LayoutUpdated -= onLayout;
                window.Dispatcher.Hooks.OperationPosted -= onPosted;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            samples.Sort();
            actionSamples.Sort();
            var report = new { name, steps, wallMs = watch.Elapsed.TotalMilliseconds,
                cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, layouts, posted,
                uiAllocatedBytes = allocated, actionMedianMs = actionSamples[actionSamples.Count / 2], medianMs = samples[samples.Count / 2],
                p95Ms = samples[(int)Math.Ceiling(samples.Count * .95) - 1], maxMs = samples[^1] };
            reports.Add(report); Console.WriteLine(JsonSerializer.Serialize(report));
            InteractionAssert(vm.IsCapturing && vm.CurrentSessionHandle != 0, "Real projection stopped during performance exercise.");
        }
        PreviewStyleGetWindowRect(handle, out var initial);
        var scale = RepairGetDpiForWindow(handle) / 96d;
        for (var pass = 0; pass < 2; pass++)
        {
            Measure($"move-{pass}", i => PerfSetWindowPos(handle, 0, initial.Left + (i % 12) * 2,
                initial.Top + (i % 8), 0, 0, 0x0015), 60);
            Measure($"resize-height-{pass}", i => window.Height = 700 + (i % 20) * 6, 60);
            Measure($"resize-width-{pass}", i => window.Width = (initial.Right - initial.Left) / scale + 40 + i % 16 * 3, 60);
        }
        var update = typeof(MainWindow).GetMethod("UpdateWorkspaceLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<bool>>(window);
        var region = window.FindName("MainPreviewHost");
        var updateRegion = region.GetType().GetMethod("UpdateWindowRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action>(region);
        Measure("unchanged-workspace", _ => update(false), 500);
        Measure("unchanged-native-region", _ => updateRegion(), 500);
        vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, "live-phone-after.png")));
        File.WriteAllText(Path.Combine(output, "performance.json"), JsonSerializer.Serialize(new
        {
            source = $"{vm.SourceVideoWidth}x{vm.SourceVideoHeight}", realProjection = true,
            renderTier = System.Windows.Media.RenderCapability.Tier >> 16, reports,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool PerfSetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
