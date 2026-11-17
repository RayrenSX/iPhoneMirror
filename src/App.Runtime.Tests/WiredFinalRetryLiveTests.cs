using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Explicit hardware opt-in: only the named, already bound USB phone. Inject
    // exhaustion at the IPC boundary, then exercise real shutdown, mux handoff,
    // replacement process, HID acknowledgement and uninterrupted native capture.
    private static int RunWiredFinalRetryLive(string udid, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = new MainWindow();
        app.MainWindow = main;
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        vm.Devices.CollectionChanged -= (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
            typeof(NotifyCollectionChangedEventHandler), main,
            typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
        var core = (NativeCore)KeyboardField(vm, "_core");
        var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
        bool Wait(Func<bool> done, int seconds = 30)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.Elapsed.TotalSeconds < seconds)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            return done();
        }
        void Complete(Task task, int seconds = 60)
        {
            MappingAssert(Wait(() => task.IsCompleted, seconds), "Live wired final Retry timed out.");
            task.GetAwaiter().GetResult();
        }
        bool Exited(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return process.HasExited; }
            catch (ArgumentException) { return true; }
        }
        try
        {
            var native = core.GetDevices().Single(d => DeviceViewModel.UdidEquals(d.Udid, udid));
            var device = DeviceViewModel.FromNative(native);
            vm.Devices.Add(device);
            SetKeyboardField(vm, "_selectedDevice", device);
            MappingAssert(DeviceViewModel.UdidEquals(vm.ResolveAppleUdid(udid), udid),
                "Selected phone lacks a matching saved binding; refusing to rebind it.");
            SetKeyboardField(vm, "_wiredControlPrerequisiteAcknowledged", true);
            var capture = vm.StartBackgroundSessionAsync(device);
            Complete(capture, 90);
            MappingAssert(capture.Result.Success, capture.Result.Message);
            var handle = sessions.Get(udid)!.Handle!;
            MappingAssert(Wait(() => core.GetDeviceSessionStatus(handle).State == CaptureState.Streaming, 45),
                "Selected phone never produced a capture frame.");
            // Keep initial automatic startup quiet, then remove that scope so a
            // real recovery failure would have to show the production window.
            Complete(vm.StartAutomationControlAsync(udid, false, CancellationToken.None), 365);
            var control = (DeviceControlSession)KeyboardCall(vm, "FindControl", udid)!;
            MappingAssert(control.WiredBridge is { IsReady: true } && control.InputEnabled, "Initial wired control failed.");
            foreach (var window in app.Windows.OfType<ReverseControlStatusWindow>().ToArray()) window.Close();
            var previous = control.WiredBridge!;
            var previousPid = ((Process)KeyboardField(KeyboardField(previous, "_bridge"), "_process")).Id;
            var before = core.GetDeviceSessionStatus(handle);
            // This is the exact error emitted when the child exhausts its own
            // three recovery attempts. No device reset or cable pull is used.
            KeyboardCall(KeyboardField(previous, "_bridge"), "HandleLine",
                "{\"event\":\"error\",\"code\":\"direct_hid_recovery_exhausted\",\"message\":\"Live final Retry fixture\"}");
            var watch = Stopwatch.StartNew();
            var samples = 0;
            while ((!control.WiredConnected || ReferenceEquals(control.WiredBridge, previous)) &&
                watch.Elapsed < TimeSpan.FromSeconds(90))
            {
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                MappingAssert(ReferenceEquals(sessions.Get(udid)?.Handle, handle) &&
                    core.GetDeviceSessionStatus(handle).State == CaptureState.Streaming,
                    "Final control Retry interrupted or recreated capture.");
                MappingAssert(!app.Windows.OfType<ReverseControlStatusWindow>().Any(w => w.IsVisible),
                    "Successful automatic recovery opened a status window.");
                samples++;
            }
            MappingAssert(control.WiredBridge is { IsReady: true } replacement &&
                !ReferenceEquals(replacement, previous) && control.InputEnabled && Exited(previousPid),
                "Final Retry did not replace and retire the actual child.");
            var next = control.WiredBridge!;
            var acknowledged = false;
            next.StatusChanged += (_, e) => { if (e.Code == "input_verified") acknowledged = true; };
            Complete(next.SendKeyboardAsync([], CancellationToken.None, releaseAll: true), 10);
            MappingAssert(Wait(() => acknowledged, 10), "Recovered HID did not acknowledge an empty release report.");
            var frame = core.GetDeviceOutputFrame(handle, 0, 0, owned: true);
            MappingAssert(frame is not null && FastPngEncoder.Encode(frame, default).Length > 33,
                "Capture screenshot was unavailable after recovery.");
            var after = core.GetDeviceSessionStatus(handle);
            var result = new { passed = true, seconds = watch.Elapsed.TotalSeconds, samples,
                captureHandleUnchanged = ReferenceEquals(sessions.Get(udid)?.Handle, handle),
                framesBefore = before.VideoFrames, framesAfter = after.VideoFrames, hidAcknowledged = acknowledged };
            File.WriteAllText(Path.Combine(output, "result.json"), System.Text.Json.JsonSerializer.Serialize(result));
            Console.WriteLine("PASS live wired final Retry: " + System.Text.Json.JsonSerializer.Serialize(result));
            return 0;
        }
        finally
        {
            Complete(vm.CancelReverseControlAsync(ControlStatusMode.Usb, udid), 30);
            Complete(vm.StopDeviceSessionAsync(udid), 60);
            foreach (var window in app.Windows.OfType<ReverseControlStatusWindow>().ToArray()) window.Close();
            main.Close();
            app.Shutdown();
        }
    }
}
