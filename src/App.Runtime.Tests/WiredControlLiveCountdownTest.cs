using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

// Opt-in hardware test. Uses real discovery, saved bindings, the toolbar
// handler, USB bridge and status window. It never manufactures a Ready event.
internal static class WiredControlLiveCountdownTest
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run()
    {
        // Match a real toolbar event dispatched by WPF: async continuations
        // must return to the UI dispatcher, including between pump frames.
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", Instance)!.SetValue(app, true);
        app.InitializeComponent();
        var main = new MainWindow();
        app.MainWindow = main;
        main.Show();
        var model = typeof(MainWindow).GetField("_viewModel", Instance)!.GetValue(main)!;
        var modelType = model.GetType();
        var devices = (IList)modelType.GetProperty("Devices")!.GetValue(model)!;
        try
        {
            if (!WaitUntil(() => devices.Count > 0, TimeSpan.FromSeconds(30)))
            {
                Console.WriteLine("HARDWARE BLOCKED: production discovery returned no devices in 30 seconds; Wired Control cannot start.");
                return 2;
            }
            var canEnable = modelType.GetMethod("CanEnableUsbControlFor", Instance)!;
            var eligible = devices.Cast<object>().Where(device =>
                (bool)canEnable.Invoke(model, [device])!).ToArray();
            if (eligible.Length != 1)
            {
                Console.WriteLine($"HARDWARE BLOCKED: {devices.Count} discovered devices, {eligible.Length} eligible bound targets; one unambiguous target is required.");
                return 2;
            }
            modelType.GetProperty("SelectedDevice")!.SetValue(model, eligible[0]);
            // The informational prerequisites have been acknowledged for this
            // explicitly requested hardware test. No phone trust, pairing,
            // Developer Mode, driver or other security setting is changed.
            modelType.GetField("_wiredControlPrerequisiteAcknowledged", Instance)!.SetValue(model, true);
            typeof(MainWindow).GetMethod("OnStartUsbControlClick", Instance)!
                .Invoke(main, [main, new RoutedEventArgs()]);
            var window = app.Windows.Cast<Window>().Single(w => w is ReverseControlStatusWindow);
            var service = modelType.GetProperty("ControlStatus", Instance)!.GetValue(model)!;
            var current = service.GetType().GetProperty("Current", Instance)!;
            string? Stage() => current.GetValue(service)?.GetType().GetProperty("Stage")!
                .GetValue(current.GetValue(service))?.ToString();
            var closed = false;
            var readyAt = TimeSpan.Zero;
            var closedAt = TimeSpan.Zero;
            var watch = Stopwatch.StartNew();
            window.Closed += (_, _) => { closed = true; closedAt = watch.Elapsed; };
            Console.WriteLine($"HARDWARE: toolbar handler showed status window; visible={window.IsVisible}.");
            if (!WaitUntil(() => Stage() is "Ready" or "Failed" or "Cancelled", TimeSpan.FromSeconds(120)))
            {
                Console.WriteLine($"HARDWARE BLOCKED: startup did not reach Ready; stage={Stage()}.");
                return 2;
            }
            if (Stage() != "Ready")
            {
                Console.WriteLine($"HARDWARE BLOCKED: real bridge startup ended at {Stage()}; no successful countdown claimed.");
                return 2;
            }
            readyAt = watch.Elapsed;
            if (!WaitUntil(() => closed, TimeSpan.FromSeconds(7)))
                throw new InvalidOperationException("Real device connected but its visible status window did not reach Closed.");
            var duration = (closedAt - readyAt).TotalSeconds;
            if (window.IsVisible || app.Windows.Cast<Window>().Contains(window) || duration < 4.8 || duration > 6.5)
                throw new InvalidOperationException($"Unexpected real-device close: visible={window.IsVisible}, seconds={duration:F3}.");
            Console.WriteLine($"HARDWARE PASS: real USB connection reached Ready; same visible window reached Closed after {duration:F3}s.");
            return 0;
        }
        finally
        {
            // Exercise normal control teardown before closing the test host.
            var stop = (Task)modelType.GetMethod("DisableUsbControlAsync", Instance)!.Invoke(model, null)!;
            WaitUntil(() => stop.IsCompleted, TimeSpan.FromSeconds(15));
            main.Close();
            WaitUntil(() => !app.Windows.Cast<Window>().Contains(main), TimeSpan.FromSeconds(15));
            app.Shutdown();
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        return condition();
    }
}
