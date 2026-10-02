using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using IPhoneMirror.App;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestLogicReviewRegressions()
    {
        TestDelayedAudioReader();
        WaitReviewTask(TestPasteFrameLimitsAsync());
        TestWiredCaptureTeardown();
        Console.WriteLine("Logic review regressions passed: audio lifetime, paste frame limits, physical-device teardown and restart ordering.");
    }

    private static void WaitReviewTask(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
            AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
        InteractionAssert(task.IsCompleted, "Logic review regression timed out.");
        task.GetAwaiter().GetResult();
    }

    private static void TestDelayedAudioReader()
    {
        var type = typeof(App).Assembly.GetType("IPhoneMirror.App.Services.MediaCastAudioDecoder", true)!;
        var decoder = Activator.CreateInstance(type, nonPublic: true)!;
        var diagnostics = new ConcurrentQueue<string>();
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            InteractionAssert(ThreadPool.SetMinThreads(1, minIo) && ThreadPool.SetMaxThreads(1, maxIo),
                "Cannot arrange the delayed audio-reader scheduler interleaving.");
            ThreadPool.QueueUserWorkItem(_ => { entered.Set(); release.Wait(); });
            InteractionAssert(entered.Wait(TimeSpan.FromSeconds(5)), "Audio fixture worker did not start.");
            // Port zero fails locally. The test checks the actual decoder's
            // process ownership and cleanup, without media or device access.
            KeyboardCall(decoder, "Start", new Uri("http://127.0.0.1:0/review.wav"), 0.0, 1.0,
                (Action<string>)diagnostics.Enqueue);
            var reader = (Task?)type.GetField("_readTask", KeyboardTestMembers)!.GetValue(decoder);
            InteractionAssert(reader is not null, "Bundled FFmpeg did not start: " + string.Join("; ", diagnostics));
            release.Set();
            InteractionAssert(reader!.Wait(TimeSpan.FromSeconds(8)), "Audio reader did not finish after source failure.");
            InteractionAssert(!diagnostics.Any(message => message.Contains("NullReferenceException")),
                "Delayed audio reader lost its process reference.");
            InteractionAssert(type.GetField("_process", KeyboardTestMembers)!.GetValue(decoder) is null,
                "Audio source failure retained a stale process reference.");
            InteractionAssert(type.GetField("_cancellation", KeyboardTestMembers)!.GetValue(decoder) is null,
                "Audio source failure retained its cancellation source.");
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorker, maxIo);
            ThreadPool.SetMinThreads(minWorker, minIo);
            ((IDisposable)decoder).Dispose();
        }
    }

    private static async Task TestPasteFrameLimitsAsync()
    {
        await using var bridge = new DirectUsbInputBridge();
        using var packets = new MemoryStream();
        using var writer = new StreamWriter(packets, leaveOpen: true);
        SetKeyboardField(bridge, "_stdin", writer);
        typeof(DirectUsbInputBridge).GetProperty("IsReady")!.SetValue(bridge, true);

        // Measure framing overhead at the upcoming one-digit sequence number.
        await bridge.SendPasteTextAsync("");
        var overhead = BitConverter.ToInt32(packets.ToArray());
        packets.SetLength(0);
        var exactLimit = new string('a', CoreDeviceTouchProtocol.MaxFrameSize - overhead);
        await bridge.SendPasteTextAsync(exactLimit);
        InteractionAssert(BitConverter.ToInt32(packets.ToArray()) == CoreDeviceTouchProtocol.MaxFrameSize,
            "An exactly-at-limit paste should be accepted.");
        packets.SetLength(0);

        foreach (var text in new[] { exactLimit + "a", new string('中', 700_000) })
        {
            var rejected = false;
            try { await bridge.SendPasteTextAsync(text); }
            catch (ArgumentException error) when (error.ParamName == "text") { rejected = true; }
            InteractionAssert(rejected && packets.Length == 0 && bridge.IsReady,
                "Oversized paste must write no header/payload and leave the bridge ready.");
        }

        const string valid = "正常粘贴 / café / 😀";
        await bridge.SendPasteTextAsync(valid);
        var bytes = packets.ToArray();
        var length = BitConverter.ToInt32(bytes);
        InteractionAssert(bytes.Length == length + 4, "Rejected paste corrupted the following frame.");
        using var document = JsonDocument.Parse(bytes.AsMemory(4, length));
        InteractionAssert(document.RootElement.GetProperty("text").GetString() == valid,
            "Valid Unicode paste failed after an oversized paste.");
    }

    private static void TestWiredCaptureTeardown()
    {
        var assembly = typeof(App).Assembly;
        var vmType = assembly.GetType("IPhoneMirror.App.ViewModels.MainViewModel", true)!;
        var vm = Activator.CreateInstance(vmType)!;
        var originalSessions = KeyboardField(vm, "_sessions");
        const string physical = "review-usb-physical";
        const string mirror = "airplay://review-owner";
        var controls = (IDictionary)KeyboardField(vm, "_deviceControls");
        var control = KeyboardCall(vm, "GetOrCreateControl", mirror)!;
        SetKeyboardField(control, "AppleUdid", physical);
        var unrelated = KeyboardCall(vm, "GetOrCreateControl", "review-other-phone")!;
        SetKeyboardField(unrelated, "AppleUdid", "review-other-physical");
        SetKeyboardField(unrelated, "WiredEnabled", true);
        // A network-only control with the same Apple identity owns no USB claim.
        var networkOnly = KeyboardCall(vm, "GetOrCreateControl", "airplay://review-network")!;
        SetKeyboardField(networkOnly, "AppleUdid", physical);
        SetKeyboardField(networkOnly, "WirelessEnabled", true);
        SetKeyboardField(networkOnly, "RequestedWireless", true);
        try
        {
            foreach (var phase in new[] { "active", "startup", "recovery" })
            {
                SetKeyboardField(control, "WiredEnabled", phase != "startup");
                SetKeyboardField(control, "WiredConnected", phase == "active");
                SetKeyboardField(control, "Starting", phase != "active");
                SetKeyboardField(control, "WiredTarget", mirror);
                var operation = KeyboardField(control, "WiredOperation");
                var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = false;
                var cleaned = false;
                Func<CancellationToken, Task> startup = async token =>
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally
                    {
                        cancelled = true;
                        await cleanup.Task;
                        cleaned = true;
                        SetKeyboardField(control, "Starting", false);
                    }
                };
                if (phase != "active") KeyboardCall(operation, "RunAsync", startup, CancellationToken.None);
                var stopped = (Task)KeyboardCall(vm, "DisableWiredControlForCaptureTeardownAsync", physical)!;
                if (phase != "active")
                {
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                    InteractionAssert(cancelled && !stopped.IsCompleted,
                        "Capture teardown must cancel and await in-flight wired cleanup.");
                    cleanup.SetResult();
                }
                WaitReviewTask(stopped);
                InteractionAssert(!(bool)KeyboardField(control, "WiredEnabled") &&
                    !(bool)KeyboardField(control, "WiredConnected") &&
                    (phase == "active" || cleaned), "Alias-owned wired control survived teardown.");
                InteractionAssert((bool)KeyboardField(unrelated, "WiredEnabled") &&
                    (bool)KeyboardField(networkOnly, "WirelessEnabled"), "Teardown stopped an unrelated transport.");
            }

            // Exercise the real settings restart. Fake only native stop/destroy,
            // and end at the shutdown boundary before any real capture creation.
            var stoppedAfterControl = false;
            var destroyed = false;
            var managerType = originalSessions.GetType();
            Action<object> stop = _ =>
            {
                stoppedAfterControl = !(bool)KeyboardField(control, "WiredEnabled");
                SetKeyboardField(vm, "_disposed", true);
            };
            Action<object> destroy = _ => destroyed = true;
            var handleType = assembly.GetType("IPhoneMirror.App.Interop.NativeSessionHandle", true)!;
            var callbackType = typeof(Action<>).MakeGenericType(handleType);
            var stopCallback = Delegate.CreateDelegate(callbackType, stop.Target, stop.Method);
            var destroyCallback = Delegate.CreateDelegate(callbackType, destroy.Target, destroy.Method);
            var sessions = Activator.CreateInstance(managerType, KeyboardTestMembers, null,
                [stopCallback, destroyCallback], null)!;
            SetKeyboardField(vm, "_sessions", sessions);
            var stateType = assembly.GetType("IPhoneMirror.App.Models.DeviceCaptureState", true)!;
            var state = Activator.CreateInstance(stateType)!;
            stateType.GetProperty("Udid", KeyboardTestMembers)!.SetValue(state, physical);
            using var handle = (IDisposable)Activator.CreateInstance(handleType,
                KeyboardTestMembers, null, [123UL, false], null)!;
            stateType.GetProperty("Handle", KeyboardTestMembers)!.SetValue(state, handle);
            KeyboardCall(sessions, "Set", state);
            var constructor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
            var device = constructor.Invoke([physical, "Review device", "iPhone15,2", "18.0", "USB", "",
                Enum.Parse(constructor.GetParameters()[6].ParameterType, "Ready")]);
            SetKeyboardField(control, "WiredEnabled", true);
            WaitReviewTask((Task)KeyboardCall(vm, "RestartUsbSessionAsync", device, state, "usb_projection")!);
            InteractionAssert(stoppedAfterControl && destroyed,
                "Settings restart must stop wired control before native capture teardown.");
        }
        finally
        {
            controls.Clear();
            SetKeyboardField(vm, "_sessions", originalSessions);
            SetKeyboardField(vm, "_disposed", false);
            WaitReviewTask((Task)KeyboardCall(vm, "ShutdownAsync")!);
        }
    }
}
