using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using IPhoneMirror.App;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestRetryFallbackRegressions(string wirelessHost)
    {
        TestUnstartedWifiSyncCleanup();
        WaitReviewTask(TestRetryableBridgeStopAsync());
        TestExhaustedBridgeCleanupOwnership();
        TestHlsBridgeExitClassification();
        WaitReviewTask(TestOriginalReceiverCrashAsync(wirelessHost));
        Console.WriteLine("Retry/fallback regressions passed: Wi-Fi gates, retryable USB cleanup, teardown ownership, HLS exit and Original receiver crash guard.");
    }

    private static void TestUnstartedWifiSyncCleanup()
    {
        using var process = new Process();
        using var lockdown = new SemaphoreSlim(0, 1);
        using var core = new SemaphoreSlim(0, 1);
        MainViewModel.CleanupWifiSyncHelper(process, lockdown, core);
        InteractionAssert(lockdown.CurrentCount == 1 && core.CurrentCount == 1,
            "An unstarted Wi-Fi helper stranded the lifecycle gates.");
        core.Wait();
        try { MainViewModel.CleanupWifiSyncHelper(process, lockdown, core); }
        catch (SemaphoreFullException) { }
        InteractionAssert(core.CurrentCount == 1,
            "Failure releasing the first gate skipped release of the core gate.");
    }

    private static async Task TestRetryableBridgeStopAsync()
    {
        await using var host = new UsbTouchBridgeHost();
        SetKeyboardField(host, "_started", 1);
        var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
        ((CancellationTokenSource)KeyboardField(bridge, "_cts")).Dispose();
        var failed = false;
        try { await host.StopAsync(); }
        catch (ObjectDisposedException) { failed = true; }
        InteractionAssert(failed, "Fault injection did not reach the real bridge stop path.");
        var replacementBlocked = false;
        try { await host.StartAsync(UsbTouchTransport.Usb, "test-device", "unused.exe"); }
        catch (InvalidOperationException) { replacementBlocked = true; }
        InteractionAssert(replacementBlocked, "A failed cleanup allowed a replacement start.");
        using var freshCancellation = new CancellationTokenSource();
        SetKeyboardField(bridge, "_cts", freshCancellation);
        var lifecycle = (SemaphoreSlim)KeyboardField(bridge, "_lifecycleLock");
        await lifecycle.WaitAsync();
        var retry = host.StopAsync();
        var duplicate = host.StopAsync();
        InteractionAssert(ReferenceEquals(retry, duplicate) && !retry.IsCompleted,
            "Concurrent stop requests did not share the pending retry.");
        lifecycle.Release();
        await retry.WaitAsync(TimeSpan.FromSeconds(3));
        InteractionAssert(freshCancellation.IsCancellationRequested && host.State == ReverseControlState.Idle,
            "The second stop did not re-enter and finish actual cleanup.");
    }

    private static void TestExhaustedBridgeCleanupOwnership()
    {
        var vm = new MainViewModel();
        var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", "retry-cleanup-phone")!;
        var host = new UsbTouchBridgeHost();
        SetKeyboardField(host, "_started", 1);
        var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
        var lifecycle = (SemaphoreSlim)KeyboardField(bridge, "_lifecycleLock");
        lifecycle.Wait();
        control.WiredBridge = host;
        control.WiredTarget = control.DeviceUdid;
        control.Failed = true;
        try
        {
            var cleanup = (Task)KeyboardCall(vm, "DisposeFailedUsbBridgeAsync", control, host)!;
            InteractionAssert(control.Stopping && ReferenceEquals(control.WiredBridge, host),
                "Recovery exhaustion released ownership before bridge disposal completed.");
            var teardown = (Task)KeyboardCall(vm, "DisableWiredControlForCaptureTeardownAsync", control.DeviceUdid)!;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
            InteractionAssert(!teardown.IsCompleted && !cleanup.IsCompleted,
                "Capture teardown did not join the existing terminal cleanup.");
            lifecycle.Release();
            WaitReviewTask(teardown);
            WaitReviewTask(cleanup);
            InteractionAssert(!control.Stopping && control.WiredBridge is null && control.WiredTarget is null,
                "Completed terminal cleanup retained stale ownership.");

            var failedHost = new UsbTouchBridgeHost();
            SetKeyboardField(failedHost, "_started", 1);
            var failedBridge = (DirectUsbInputBridge)KeyboardField(failedHost, "_bridge");
            ((CancellationTokenSource)KeyboardField(failedBridge, "_cts")).Dispose();
            control.WiredBridge = failedHost;
            WaitReviewTask((Task)KeyboardCall(vm, "DisposeFailedUsbBridgeAsync", control, failedHost)!);
            InteractionAssert(ReferenceEquals(control.WiredBridge, failedHost),
                "Failed cleanup discarded the resource needed by a later retry.");
            using var restored = new CancellationTokenSource();
            SetKeyboardField(failedBridge, "_cts", restored);
            WaitReviewTask((Task)KeyboardCall(vm, "DisableWiredControlForCaptureTeardownAsync", control.DeviceUdid)!);
            InteractionAssert(control.WiredBridge is null && restored.IsCancellationRequested,
                "Capture teardown could not retry a prior failed bridge cleanup.");
        }
        finally
        {
            if (lifecycle.CurrentCount == 0) lifecycle.Release();
            WaitReviewTask(host.StopAsync());
            WaitReviewTask(vm.ShutdownAsync());
        }
    }

    private static void TestHlsBridgeExitClassification()
    {
        foreach (var exitCode in new[] { 0, 7 })
        {
            var start = new ProcessStartInfo("cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "/d", "/c", $"exit {exitCode}" }) start.ArgumentList.Add(argument);
            var process = Process.Start(start)!;
            InteractionAssert(process.WaitForExit(3000), "HLS process fixture did not exit.");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var hls = (HlsMediaPlaybackBridge)typeof(HlsMediaPlaybackBridge)
                .GetConstructors(KeyboardTestMembers).Single().Invoke([process, listener, new Uri("http://127.0.0.1/test.ts"), null]);
            InteractionAssert(!hls.IsRunning && hls.ExitedSuccessfully == (exitCode == 0),
                "HLS bridge treated a nonzero exit as normal EOF.");
            var window = new MainWindow { ShowInTaskbar = false };
            try
            {
                var element = (System.Windows.Controls.MediaElement)window.FindName("MediaCastMediaElement");
                var gate = (MediaCastEventGate)KeyboardField(window, "_mediaCastEvents");
                var generation = gate.BeginGeneration();
                gate.TryBind(generation, element);
                SetKeyboardField(window, "_mediaHlsBridge", hls);
                SetKeyboardField(window, "_mediaCastActive", true);
                SetKeyboardField(window, "_mediaUsesHlsBridge", true);
                SetKeyboardField(window, "_mediaSource", new Uri("http://127.0.0.1:0/test.m3u8"));
                SetKeyboardField(window, "_mediaShouldPlay", true);
                KeyboardCall(window, "CompleteMediaCastMediaEnded", element, generation);
                InteractionAssert((int)KeyboardField(window, "_mediaRecoveryRevision") == (exitCode == 0 ? 0 : 1),
                    "MediaEnded did not distinguish clean HLS EOF from an error requiring recovery.");
                InteractionAssert((bool)KeyboardField(window, "_mediaShouldPlay") == (exitCode != 0),
                    "An HLS transport failure was treated as successful programme completion.");
            }
            finally
            {
                KeyboardCall(window, "StopMediaCastPlayback", "test_cleanup");
                CloseWorkspaceTestWindow(window);
                WaitReviewTask(((MainViewModel)KeyboardField(window, "_viewModel")).ShutdownAsync());
            }
            hls.Dispose();
            InteractionAssert(!hls.ExitedSuccessfully, "Disposal was misclassified as successful EOF.");
        }
    }

    private static async Task TestOriginalReceiverCrashAsync(string hostPath)
    {
        const string key = "IPHONE_MIRROR_AIRPLAY_HOST";
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, hostPath);
        using var core = new NativeCore();
        var controller = new WirelessReceiverController(core);
        try
        {
            InteractionAssert(controller.IsAvailable, "Original receiver test runtime must pass integrity checks.");
            SetKeyboardField(controller, "_lastReceiverStartUtc", DateTime.UtcNow);
            var first = await controller.EnsureStartedAsync();
            InteractionAssert(!first.Started && first.IsNewError && !string.IsNullOrWhiteSpace(first.Error),
                "Original receiver immediate exit was not surfaced as a startup failure.");
            for (var tick = 0; tick < 4; tick++)
            {
                var repeated = await controller.EnsureStartedAsync();
                InteractionAssert(!repeated.Started && !repeated.IsNewError && repeated.Error == first.Error,
                    "Automatic polling restarted a failed receiver or erased its error.");
            }
            var manual = await controller.EnsureStartedAsync("Retry regression", backend: WirelessReceiverBackend.Original);
            InteractionAssert(manual.Started && !(bool)KeyboardField(controller, "_automaticRestartBlocked"),
                "Manual Apply did not release the crash guard and retry startup.");
            InteractionAssert(controller.StartError == first.Error,
                "Creating a process cleared the previous error before Ready was confirmed.");
        }
        finally
        {
            await controller.StopAsync();
            Environment.SetEnvironmentVariable(key, previous);
        }
    }
}
