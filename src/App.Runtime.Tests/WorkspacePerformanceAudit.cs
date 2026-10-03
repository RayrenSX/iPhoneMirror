using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private sealed class WorkspaceMeasureProbe : Decorator
    {
        internal int Measures, Arranges;
        protected override Size MeasureOverride(Size constraint)
        {
            Measures++;
            return base.MeasureOverride(constraint);
        }
        protected override Size ArrangeOverride(Size size)
        {
            Arranges++;
            return base.ArrangeOverride(size);
        }
    }

    // Same real content and render mode before/after; no discovery, settings writes
    // or live video. Counts at the content boundary are actual Measure/Arrange calls.
    private static int RunWorkspacePerformanceAudit(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var mode = GetApplicationDisplayMode(app);
        var results = new List<object>();
        try
        {
            foreach (var display in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                SetApplicationDisplayMode(app, display);
                var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                try
                {
                    var border = (Border)(window.FindName("ControlPanelContent") ?? window.FindName("ControlPanel"));
                    var child = border.Child;
                    border.Child = null;
                    var probe = new WorkspaceMeasureProbe { Child = child };
                    border.Child = probe;
                    void Toggle(bool visible) => AuditFixCall(window, "SetSettingsPanelVisible", visible);
                    Toggle(true); AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
                    Toggle(false); AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
                    probe.Measures = probe.Arranges = 0;
                    var layouts = 0;
                    EventHandler layout = (_, _) => layouts++;
                    window.LayoutUpdated += layout;
                    using var process = Process.GetCurrentProcess();
                    var cpu = process.TotalProcessorTime;
                    var watch = Stopwatch.StartNew();
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 20; i++)
                    {
                        Toggle(i % 2 == 0);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
                    }
                    results.Add(new { mode = display.ToString(), toggles = 20,
                        contentMeasures = probe.Measures, contentArranges = probe.Arranges,
                        layouts, cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                        wallMs = watch.Elapsed.TotalMilliseconds,
                        uiThreadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
                        dpi = VisualTreeHelper.GetDpi(window).DpiScaleX, renderTier = RenderCapability.Tier >> 16 });
                    window.LayoutUpdated -= layout;
                }
                finally { CloseWorkspaceTestWindow(window); }
            }
        }
        finally { SetApplicationDisplayMode(app, mode); app.Shutdown(); }
        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "workspace-performance.json"), json);
        Console.WriteLine(json);
        return 0;
    }
}
