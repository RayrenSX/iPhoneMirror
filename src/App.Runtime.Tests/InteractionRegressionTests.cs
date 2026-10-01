using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private const BindingFlags InteractionMembers = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static int RunInteractionRegressionTests()
    {
        TestReverseControlCursorShapes();
        TestPreviewRegionAndCapture();
        Task.Run(TestBluetoothWaitAndLifecycleAsync).GetAwaiter().GetResult();
        Task.Run(TestBluetoothPendingInputAndRecoveryAsync).GetAwaiter().GetResult();
        Console.WriteLine("Interaction regression tests passed; no Bluetooth device input was sent.");
        return 0;
    }

    private static void InteractionAssert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object? InteractionField(object owner, string name) =>
        owner.GetType().GetField(name, InteractionMembers)!.GetValue(owner);

    private static void InteractionSet(object owner, string name, object? value) =>
        owner.GetType().GetField(name, InteractionMembers)!.SetValue(owner, value);

    private static void TestPreviewRegionAndCapture()
    {
        using var parent = new HwndSource(new HwndSourceParameters("Interaction regression")
        { Width = 480, Height = 360, WindowStyle = unchecked((int)0x80000000) });
        var child = InteractionCreateWindow(0, "STATIC", "", 0x40000000,
            0, 0, 240, 180, parent.Handle, 0, 0, 0);
        InteractionAssert(child != 0, "Create preview test HWND");
        var hostType = typeof(App).Assembly.GetType("IPhoneMirror.App.Controls.NativePreviewHost", true)!;
        var host = Activator.CreateInstance(hostType, nonPublic: true)!;
        var profileType = typeof(App).Assembly.GetType("IPhoneMirror.App.Models.DeviceCornerProfile", true)!;
        var profile = Activator.CreateInstance(profileType, InteractionMembers, null,
            ["test-rounded", true, 0.08, 4.0, 0.08], null);
        var region = InteractionCreateRectRgn(0, 0, 0, 0);
        try
        {
            InteractionSet(host, "_window", child);
            InteractionSet(host, "_cornerProfile", profile);
            InteractionSet(host, "_usesDeviceCornerProfile", true);
            var update = hostType.GetMethod("UpdateWindowRegion", InteractionMembers)!
                .CreateDelegate<Action>(host);
            for (var index = 0; index < 16; ++index) update();
            InteractionAssert(InteractionGetWindowRgn(child, region) == 3,
                "Device preview must retain its curved region");
            var start = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (var index = 0; index < 500; ++index) update();
            var allocations = GC.GetAllocatedBytesForCurrentThread() - start;
            InteractionAssert(allocations < 8192,
                $"Position-only updates rebuilt the polygon: {allocations} bytes");
            Console.WriteLine($"500 unchanged preview-region updates: {allocations} allocated bytes, {timer.Elapsed.TotalMilliseconds:F2} ms.");

            InteractionSetWindowPos(child, 0, 15, 20, 300, 210, 0x0014);
            update();
            InteractionGetWindowRgn(child, region);
            InteractionGetRgnBox(region, out var bounds);
            InteractionAssert(bounds.Right == 300 && bounds.Bottom == 210,
                "Resizing must update the region dimensions");
            hostType.GetProperty("IsFullScreenPresentation", InteractionMembers)!.SetValue(host, true);
            InteractionAssert(InteractionGetWindowRgn(child, region) == 0,
                "Fullscreen must remove the rounded region");
            hostType.GetProperty("IsFullScreenPresentation", InteractionMembers)!.SetValue(host, false);
            InteractionAssert(InteractionGetWindowRgn(child, region) == 3,
                "Leaving fullscreen must restore the rounded region");

            var release = hostType.GetMethod("ReleasePointerCapture", InteractionMembers)!;
            InteractionSetCapture(parent.Handle);
            InteractionAssert(InteractionGetCapture() == parent.Handle, "Acquire parent drag capture");
            release.Invoke(host, null);
            InteractionAssert(InteractionGetCapture() == parent.Handle,
                "Preview reset must not release the title bar's capture");
            InteractionSetCapture(child);
            release.Invoke(host, null);
            InteractionAssert(InteractionGetCapture() == 0, "Preview must release its own capture");
        }
        finally
        {
            InteractionReleaseCapture();
            InteractionSet(host, "_window", (nint)0);
            ((IDisposable)host).Dispose();
            InteractionDestroyWindow(child);
            InteractionDeleteObject(region);
        }
    }

    private static async Task TestBluetoothWaitAndLifecycleAsync()
    {
        var type = typeof(App).Assembly.GetType("IPhoneMirror.App.Services.BluetoothHidMouseService", true)!;
        var wait = type.GetMethod("WaitForMouseNotificationAsync", InteractionMembers)!;
        Task<bool> Wait(Task operation, TimeSpan timeout, CancellationToken token = default) =>
            (Task<bool>)wait.Invoke(null, [operation, timeout, token])!;
        InteractionAssert(await Wait(Task.CompletedTask, TimeSpan.FromSeconds(1)), "Completed BLE notification");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        InteractionAssert(!await Wait(pending.Task, TimeSpan.FromMilliseconds(40)).WaitAsync(TimeSpan.FromSeconds(2)),
            "Stalled BLE notification must hit its watchdog");
        InteractionAssert(!pending.Task.IsCompleted, "Watchdog must not cancel the native notification");
        using var cancellation = new CancellationTokenSource();
        var cancelledWait = Wait(pending.Task, TimeSpan.FromSeconds(30), cancellation.Token);
        cancellation.Cancel();
        InteractionAssert(!await cancelledWait.WaitAsync(TimeSpan.FromSeconds(1)),
            "Route cancellation must promptly end the managed wait");
        try
        {
            await Wait(Task.FromException(new IOException("native failure")), TimeSpan.FromSeconds(1));
            throw new InvalidOperationException("Native failure was swallowed");
        }
        catch (IOException) { }

        var service = Activator.CreateInstance(type, nonPublic: true)!;
        var disposed = false;
        try
        {
            var callbacks = 0;
            type.GetEvent("StatusChanged")!.AddEventHandler(service,
                new EventHandler((_, _) => throw new COMException("simulated callback failure")));
            type.GetEvent("StatusChanged")!.AddEventHandler(service,
                new EventHandler((_, _) => ++callbacks));
            type.GetMethod("SetStatus", InteractionMembers)!.Invoke(service, ["test", null]);
            InteractionAssert(callbacks == 1, "One failing status subscriber must not escape or block other subscribers");

            // Keep the producer lock held: a background pump cannot consume
            // this item yet. An inline/reentrant pump would consume it here.
            InteractionSet(service, "_targetDeviceUdid", "offline-test");
            lock (InteractionField(service, "_mousePumpSync")!)
            {
                var send = type.GetMethods().Single(method => method.Name == "SendMouseAsync" &&
                    method.GetParameters().Length == 4);
                var queued = (Task)send.Invoke(service, [1, 2, (byte)0, 0])!;
                InteractionAssert(queued.IsCompletedSuccessfully, "Input enqueue must return immediately");
                InteractionAssert(InteractionField(service, "_pendingMouseReport") is not null,
                    "Mouse input must enqueue without running WinRT inline under the producer lock");
                InteractionSet(service, "_mousePumpStopping", true);
            }
            await ((Task)InteractionField(service, "_mousePumpTask")!).WaitAsync(TimeSpan.FromSeconds(2));
            InteractionSet(service, "_mousePumpGeneration", 2L);
            InteractionSet(service, "_mousePumpRunning", true);
            var retained = new byte[6];
            InteractionSet(service, "_pendingMouseReport", retained);
            await ((Task)type.GetMethod("PumpReportsAsync", InteractionMembers)!.Invoke(service, [1L])!);
            InteractionAssert(ReferenceEquals(retained, InteractionField(service, "_pendingMouseReport")) &&
                (bool)InteractionField(service, "_mousePumpRunning")!,
                "Old-generation pumps must leave the new generation's queue/state untouched");

            var gate = (SemaphoreSlim)InteractionField(service, "_mouseNotificationTransportGate")!;
            await gate.WaitAsync();
            var release = (Task)type.GetMethod("ReleaseMouseNotificationGateAsync", InteractionMembers)!
                .Invoke(service, [pending.Task])!;
            InteractionAssert(!gate.Wait(0), "Timed-out native notification must retain the transport slot");
            pending.SetResult();
            await release.WaitAsync(TimeSpan.FromSeconds(1));
            InteractionAssert(gate.Wait(0), "Native completion must release the transport slot");
            var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateRelease = (Task)type.GetMethod("ReleaseMouseNotificationGateAsync", InteractionMembers)!
                .Invoke(service, [late.Task])!;
            await ((IAsyncDisposable)service).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            disposed = true;
            late.SetException(new IOException("late native failure after disposal"));
            await lateRelease.WaitAsync(TimeSpan.FromSeconds(1));
            Console.WriteLine("BLE watchdog, cancellation, late completion/disposal, callback isolation and pump-generation checks passed.");
        }
        finally
        {
            pending.TrySetResult();
            if (!disposed) await ((IAsyncDisposable)service).DisposeAsync();
        }
    }

    private static async Task InteractionWaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(2))
                throw new TimeoutException("Bluetooth regression condition did not settle.");
            await Task.Delay(10);
        }
    }

    private static async Task TestBluetoothPendingInputAndRecoveryAsync()
    {
        var type = typeof(App).Assembly.GetType("IPhoneMirror.App.Services.BluetoothHidMouseService", true)!;
        var service = Activator.CreateInstance(type, nonPublic: true)!;
        var sync = InteractionField(service, "_mousePumpSync")!;
        var keyboard = type.GetMethods().Single(method => method.Name == "SendKeyboardAsync" &&
            method.GetParameters().Length == 2);
        var targetGate = (SemaphoreSlim)InteractionField(service, "_targetClientGate")!;
        var lifecycleGate = (SemaphoreSlim)InteractionField(service, "_gate")!;
        var scheduleRecovery = type.GetMethod("ScheduleMouseStallRecovery", InteractionMembers)!;
        try
        {
            InteractionSet(service, "_targetDeviceUdid", "offline-test");
            await targetGate.WaitAsync();
            Task queuedKey;
            try
            {
                var mouse = type.GetMethods().Single(method => method.Name == "SendMouseAsync" &&
                    method.GetParameters().Length == 4);
                mouse.Invoke(service, [1, 2, (byte)0, 0]);
                await InteractionWaitUntilAsync(() =>
                {
                    lock (sync) return InteractionField(service, "_pendingMouseReport") is null;
                });
                lock (sync)
                {
                    // Exhaust retries so only the keyboard wake-up can settle this task.
                    InteractionSet(service, "_mouseStateRetryAttempts", int.MaxValue);
                    queuedKey = (Task)keyboard.Invoke(service, [(byte)0, new byte[] { 4 }])!;
                    InteractionAssert(!queuedKey.IsCompleted, "Key must wait behind the in-flight mouse report");
                }
            }
            finally { targetGate.Release(); }
            try
            {
                await queuedKey.WaitAsync(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("Offline key must report unavailable transport");
            }
            catch (IOException) { }
            await ((Task)InteractionField(service, "_mousePumpTask")!).WaitAsync(TimeSpan.FromSeconds(2));

            await lifecycleGate.WaitAsync();
            try
            {
                scheduleRecovery.Invoke(service, [0]);
                InteractionSet(service, "_routeGeneration", 1);
            }
            finally { lifecycleGate.Release(); }
            await InteractionWaitUntilAsync(() => (int)InteractionField(service, "_mouseStallRecoveryInProgress")! == 0);
            InteractionAssert((int)InteractionField(service, "_transportFailed")! == 0 &&
                (string?)InteractionField(service, "_targetDeviceUdid") == "offline-test",
                "Recovery for an old route must leave the replacement route intact");

            lock (sync)
            {
                InteractionSet(service, "_mousePumpRunning", true);
                queuedKey = (Task)keyboard.Invoke(service, [(byte)0, new byte[] { 4 }])!;
            }
            var failureWasPublished = false;
            type.GetEvent("StatusChanged")!.AddEventHandler(service, new EventHandler((_, _) =>
                failureWasPublished = (int)InteractionField(service, "_transportFailed")! != 0));
            scheduleRecovery.Invoke(service, [1]);
            await InteractionWaitUntilAsync(() => (int)InteractionField(service, "_mouseStallRecoveryInProgress")! == 0);
            InteractionAssert(queuedKey.IsCanceled, "Recovery must cancel every pending keyboard completion");
            InteractionAssert(failureWasPublished, "Recovery status must retain transport failure after teardown");

            // A delayed release must not reset state owned by a later stop/release.
            var heldPump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            InteractionSet(service, "_mousePumpTask", heldPump.Task);
            var release = (Task)type.GetMethod("ReleaseAllAsync", InteractionMembers)!.Invoke(service, [false])!;
            lock (sync)
            {
                InteractionSet(service, "_mousePumpGeneration", (long)InteractionField(service, "_mousePumpGeneration")! + 1);
                InteractionSet(service, "_mousePumpStopping", true);
                InteractionSet(service, "_mousePumpRunning", true);
            }
            heldPump.SetResult();
            await release.WaitAsync(TimeSpan.FromSeconds(2));
            InteractionAssert((bool)InteractionField(service, "_mousePumpStopping")! &&
                (bool)InteractionField(service, "_mousePumpRunning")!,
                "Old release completion must not overwrite the new pump state");
            Console.WriteLine("BLE pending keyboard, stale recovery, failure publication and overlapping release checks passed.");
        }
        finally
        {
            await ((IAsyncDisposable)service).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InteractionRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint InteractionCreateWindow(int ex, string cls, string title, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow")] private static extern bool InteractionDestroyWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool InteractionSetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowRgn")] private static extern int InteractionGetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn")] private static extern nint InteractionCreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", EntryPoint = "GetRgnBox")] private static extern int InteractionGetRgnBox(nint region, out InteractionRect rect);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] private static extern bool InteractionDeleteObject(nint handle);
    [DllImport("user32.dll", EntryPoint = "SetCapture")] private static extern nint InteractionSetCapture(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetCapture")] private static extern nint InteractionGetCapture();
    [DllImport("user32.dll", EntryPoint = "ReleaseCapture")] private static extern bool InteractionReleaseCapture();
}
