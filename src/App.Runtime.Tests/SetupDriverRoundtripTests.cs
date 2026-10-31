using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunSetupDriverRoundtrip(string driver, string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_DRIVER_MANAGER", Path.GetFullPath(driver));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var workspace = new IPhoneMirror.App.MainWindow();
        app.MainWindow = workspace;
        var vm = (MainViewModel)workspace.DataContext;
        var records = new List<object>();
        async Task Run()
        {
            var inventory = await FirstRunDriverClient.RunAsync("", false,
                new Progress<SetupDriverProgress>(), CancellationToken.None, diagnose: true);
            var target = inventory.Devices?.FirstOrDefault() ??
                throw new InvalidOperationException("Connect an iPhone for the real driver roundtrip.");
            var store = new FirstRunSetupStore(Path.Combine(Path.GetFullPath(output), "state.json"));
            store.Save(new() { Step = SetupStep.Environment, Usage = SetupUsage.WiredOnly,
                Devices = [new() { Id = target.Serial, Name = target.Name }] });
            var window = new FirstRunSetupWindow(vm, store, previewOnly: true);
            // Match the actual owned modal setup, including the main window's
            // focus hooks and suspended refresh timers during setup ownership.
            workspace.Show();
            window.Owner = workspace;
            _ = workspace.Dispatcher.BeginInvoke(new Action(() => window.ShowDialog()));
            await Task.Delay(250);
            var phase = "baseline";
            var watch = Stopwatch.StartNew();
            var last = watch.Elapsed.TotalMilliseconds;
            var maximumGap = 0d;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Input)
            { Interval = TimeSpan.FromMilliseconds(16) };
            heartbeat.Tick += (_, _) =>
            {
                var now = watch.Elapsed.TotalMilliseconds;
                maximumGap = Math.Max(maximumGap, now - last);
                if (now - last > 100)
                    records.Add(new { phase, gapMs = now - last, atMs = now });
                last = now;
            };
            heartbeat.Start();
            try
            {
                await Task.Delay(1000);
                var launcher = new DriverManagerLauncher();
                window.OpenDriverToolAsync = async token =>
                {
                    phase = "driver_start";
                    var launch = launcher.Launch();
                    if (!launch.Success || launch.ProcessId is not { } id) return launch;
                    using var process = Process.GetProcessById(id);
                    try
                    {
                        var deadline = Stopwatch.StartNew();
                        while (!process.HasExited && process.MainWindowHandle == 0 &&
                            deadline.Elapsed < TimeSpan.FromSeconds(20))
                        { await Task.Delay(100, token); process.Refresh(); }
                        if (process.HasExited || process.MainWindowHandle == 0)
                            throw new InvalidOperationException("Driver window did not appear.");
                        phase = "driver_open";
                        await Task.Delay(1500, token);
                        // Close only this test's read-only driver window. No
                        // install, repair, service restart or input is issued.
                        phase = "driver_close";
                        if (!process.CloseMainWindow()) throw new InvalidOperationException("Driver close failed.");
                        await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(20));
                        records.Add(new { phase = "driver_exit", elapsedMs = deadline.ElapsedMilliseconds });
                        phase = "verification";
                        return launch;
                    }
                    finally
                    {
                        if (!process.HasExited) process.CloseMainWindow();
                    }
                };
                for (var pass = 0; pass < 2; pass++)
                {
                    var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                    var timer = Stopwatch.StartNew();
                    var task = (Task)typeof(FirstRunSetupWindow).GetMethod("RunAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false])!;
                    await task.WaitAsync(TimeSpan.FromSeconds(60));
                    var verified = (bool)typeof(FirstRunSetupWindow).GetField("_verified",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    records.Add(new { pass, phase = "verified", verified,
                        elapsedMs = timer.ElapsedMilliseconds,
                        cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds });
                    if (!verified) throw new InvalidOperationException("Real post-driver verification failed.");
                    phase = "returned_page";
                    await Task.Delay(3000);
                }
                records.Add(new { maximumDispatcherGapMs = maximumGap });
            }
            finally
            {
                heartbeat.Stop();
                File.WriteAllText(Path.Combine(output, "roundtrip.json"),
                    JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                typeof(FirstRunSetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, true);
                window.Close();
            }
        }
        var run = Run();
        var frame = new DispatcherFrame();
        _ = run.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try { run.GetAwaiter().GetResult(); }
        finally
        {
            typeof(IPhoneMirror.App.MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(workspace, true);
            workspace.Close();
            app.Shutdown();
        }
        Console.WriteLine("PASS two real driver open/close, post-exit helper checks and returned-page dispatcher observations.");
        return 0;
    }
}
