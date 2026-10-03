using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunVirtualCameraRegressionTests()
    {
        TestVirtualCameraRegressionsAsync().GetAwaiter().GetResult();
        Console.WriteLine("Virtual camera regressions passed: static frames, timestamp resets, missing sources, first-frame timeout, cancellation and cleanup.");
        return 0;
    }

    private static async Task TestVirtualCameraRegressionsAsync()
    {
        TestVirtualCameraElevationBootstrap();
        // These independent services use only managed native-operation substitutes.
        // No test probes, registers, starts or stops a Windows camera device.
        await Task.WhenAll(
            TestVirtualCameraStaticFramesAsync(),
            TestVirtualCameraMissingFramesAsync(initiallyAvailable: false),
            TestVirtualCameraMissingFramesAsync(initiallyAvailable: true),
            TestVirtualCameraDestroyedSourceAsync(),
            TestVirtualCameraCancellationAsync(),
            TestVirtualCameraPublishFailureAsync(),
            TestVirtualCameraDuplicateStartAsync(),
            TestVirtualCameraIdleStopRaceAsync(),
            TestVirtualCameraStartFailureAsync(),
            TestVirtualCameraStartupObserverFailureAsync());
    }

    private static void TestVirtualCameraElevationBootstrap()
    {
        var type = typeof(App).Assembly.GetType(
            "IPhoneMirror.App.Services.VirtualCameraService", true)!;
        var validate = type.GetMethod("ValidateElevationEnvironment",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var name in new[] { "COMPLUS_InstallRoot", "COR_ENABLE_PROFILING",
                     "CORECLR_PROFILER", "DOTNET_STARTUP_HOOKS", "DEVPATH",
                     "APPDOMAIN_MANAGER_ASM", "APPDOMAIN_MANAGER_TYPE", "CLRConfigFile" })
        {
            var rejected = false;
            try
            {
                validate.Invoke(null, [new Hashtable { [name.ToLowerInvariant()] = "untrusted" }]);
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
            {
                rejected = true;
            }
            InteractionAssert(rejected, "The camera bootstrap accepted CLR injection: " + name);
        }
        validate.Invoke(null, [new Hashtable { ["DOTNET_ROOT"] = "", ["PATH"] = "user tools" }]);
        var build = type.GetMethod("BuildAdminStartInfo",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        const string helper = @"C:\review\摄像头 '$();\Admin.exe";
        const string mediaSource = @"C:\review\媒体 '$();\Camera.dll";
        foreach (var install in new[] { false, true })
        {
            var start = (ProcessStartInfo)build.Invoke(null,
                [helper, mediaSource, new string('A', 64), new string('B', 64), install])!;
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[^1]));
            InteractionAssert(start.UseShellExecute && start.Verb == "runas" &&
                start.WorkingDirectory == Environment.SystemDirectory &&
                start.FileName == System.IO.Path.Combine(Environment.SystemDirectory,
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                "The camera elevation host or working directory is not system-owned.");
            InteractionAssert(script.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(helper))) &&
                script.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(mediaSource))) &&
                !script.Contains(helper) && !script.Contains(mediaSource) &&
                script.Contains("$install = " + (install ? "$true" : "$false")),
                "Camera bootstrap arguments escaped their literal encoding.");
            InteractionAssert(!script.Contains("ConvertFrom-Json") &&
                !script.Contains("Start-Process") && !script.Contains("Push-Location") &&
                script.Contains("$security.SetOwner($administrators)") &&
                script.Contains("$start.EnvironmentVariables.Clear()"),
                "Camera bootstrap can load inherited modules or use a writable staging environment.");
        }
    }

    private static async Task TestVirtualCameraStaticFramesAsync()
    {
        var frame = new VideoFrame(2, 2, 8, 1_000_000, new byte[16]);
        await using var fixture = new VirtualCameraFixture(() => Volatile.Read(ref frame));
        await fixture.StartAsync();
        await fixture.Published.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var hold = Task.Delay(TimeSpan.FromMilliseconds(5500));
        InteractionAssert(await Task.WhenAny(hold, fixture.Completed.Task) == hold,
            "A valid static frame must remain publishable beyond the five-second timeout.");
        InteractionAssert(fixture.IsRunning && fixture.Frames.Count > 100,
            "The camera did not keep publishing its valid static frame.");

        // A provider can update pixels without advancing its clock, or reset the
        // clock on a source transition. Both are valid frames, not stale reads.
        var sameTimestamp = new VideoFrame(2, 2, 8, frame.Timestamp100Ns,
            Enumerable.Repeat((byte)42, 16).ToArray());
        Volatile.Write(ref frame, sameTimestamp);
        await fixture.WaitForFrameAsync(sameTimestamp);
        var resetTimestamp = sameTimestamp with { Timestamp100Ns = 1 };
        Volatile.Write(ref frame, resetTimestamp);
        await fixture.WaitForFrameAsync(resetTimestamp);

        await fixture.StopAsync();
        fixture.AssertStopped(failed: false);
    }

    private static async Task TestVirtualCameraMissingFramesAsync(bool initiallyAvailable)
    {
        var first = new VideoFrame(2, 2, 8, 1, new byte[16]);
        var reads = 0;
        await using var fixture = new VirtualCameraFixture(() =>
            Interlocked.Increment(ref reads) == 1 && initiallyAvailable ? first : null);
        await fixture.StartAsync();
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        fixture.AssertStopped(failed: true);
        InteractionAssert(fixture.Completed.Task.Result.Message == VirtualCameraText(
                initiallyAvailable ? "MediaOutputFrameStalled" : "MediaOutputFrameTimeout"),
            "Missing frames must report the correct initial/source-loss timeout.");
        InteractionAssert(initiallyAvailable ? fixture.Frames.Count > 1 : fixture.Frames.IsEmpty,
            "The camera must reuse its last valid frame only during the source-loss grace period.");
        InteractionAssert(reads > 100,
            "A temporary unavailable read must not cause an immediate timeout.");
    }

    private static async Task TestVirtualCameraDestroyedSourceAsync()
    {
        var reads = 0;
        await using var fixture = new VirtualCameraFixture(() =>
            Interlocked.Increment(ref reads) == 1
                ? new VideoFrame(2, 2, 8, 1, new byte[16])
                : throw new ObjectDisposedException("capture-session"));
        await fixture.StartAsync();
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.AssertStopped(failed: true);
        InteractionAssert(fixture.Frames.Count == 1,
            "An explicitly destroyed source must not keep publishing its cached frame.");
    }

    private static async Task TestVirtualCameraCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = new VirtualCameraFixture(() => null);
        await fixture.StartAsync(cancellation.Token);
        cancellation.Cancel();
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.AssertStopped(failed: false);
        InteractionAssert(fixture.Frames.IsEmpty,
            "Cancelling before the first frame must not publish or report a frame timeout.");
    }

    private static async Task TestVirtualCameraPublishFailureAsync()
    {
        await using var fixture = new VirtualCameraFixture(
            () => new VideoFrame(2, 2, 8, 1, new byte[16]),
            publishResult: unchecked((int)0x80004005));
        await fixture.StartAsync();
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.AssertStopped(failed: true);
    }

    private static async Task TestVirtualCameraDuplicateStartAsync()
    {
        await using var fixture = new VirtualCameraFixture(
            () => new VideoFrame(2, 2, 8, 1, new byte[16]));
        await fixture.StartAsync();
        var rejected = false;
        try { await fixture.StartAsync(); }
        catch (InvalidOperationException) { rejected = true; }
        InteractionAssert(rejected && fixture.IsRunning && fixture.NativeStops == 0,
            "Rejecting a duplicate start must not stop the already-running native camera.");
        await fixture.StopAsync();
        fixture.AssertStopped(failed: false);
    }

    private static async Task TestVirtualCameraIdleStopRaceAsync()
    {
        using var stopEntered = new ManualResetEventSlim();
        using var releaseStop = new ManualResetEventSlim();
        var stopCalls = 0;
        await using var fixture = new VirtualCameraFixture(
            () => new VideoFrame(2, 2, 8, 1, new byte[16]),
            onStop: () =>
            {
                if (Interlocked.Increment(ref stopCalls) != 1) return;
                stopEntered.Set();
                InteractionAssert(releaseStop.Wait(TimeSpan.FromSeconds(3)),
                    "The idle-stop fixture was not released.");
            });
        var stop = fixture.StopAsync();
        Task? start = null;
        var startWasBlocked = false;
        try
        {
            InteractionAssert(await Task.Run(() => stopEntered.Wait(TimeSpan.FromSeconds(2))),
                "The idle native stop did not begin.");
            start = fixture.StartAsync();
            await Task.Delay(100);
            startWasBlocked = !start.IsCompleted && fixture.NativeStarts == 0;
        }
        finally
        {
            releaseStop.Set();
            await stop;
            if (start is not null) await start;
        }
        InteractionAssert(startWasBlocked,
            "A new camera must wait until an older idle native stop has completed.");
        await fixture.StopAsync();
        fixture.AssertStopped(failed: false, expectedStops: 2);
    }

    private static async Task TestVirtualCameraStartFailureAsync()
    {
        await using var fixture = new VirtualCameraFixture(() => null,
            startResult: unchecked((int)0x80004005));
        var failed = false;
        try { await fixture.StartAsync(); }
        catch (InvalidOperationException) { failed = true; }
        InteractionAssert(failed && fixture.NativeStarts == 1 && fixture.NativeStops == 1 &&
            !fixture.IsRunning && !fixture.Completed.Task.IsCompleted,
            "A failed native start must release its backend without creating a pump.");
        fixture.AssertRunReleased();
    }

    private static async Task TestVirtualCameraStartupObserverFailureAsync()
    {
        await using var fixture = new VirtualCameraFixture(() => null,
            onStarted: () => throw new InvalidOperationException("startup observer failed"));
        var failed = false;
        try { await fixture.StartAsync(); }
        catch (InvalidOperationException) { failed = true; }
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        InteractionAssert(failed, "The startup observer failure was swallowed.");
        fixture.AssertStopped(failed: false);
    }

    private static string VirtualCameraText(string key) => (string)typeof(App).Assembly
        .GetType("IPhoneMirror.App.Localization.LocalizationService", true)!
        .GetMethod("Get", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [key])!;

    private sealed class VirtualCameraFixture : IAsyncDisposable
    {
        private const BindingFlags Members = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Type _type = typeof(App).Assembly.GetType(
            "IPhoneMirror.App.Services.VirtualCameraService", true)!;
        private readonly object _service;
        private int _starts;
        private int _stops;
        private int _terminalEvents;

        internal ConcurrentQueue<VideoFrame> Frames { get; } = new();
        internal TaskCompletionSource Published { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<(string Message, bool Failed)> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsRunning => (bool)_type.GetProperty("IsRunning", Members)!
            .GetValue(_service)!;
        internal int NativeStarts => Volatile.Read(ref _starts);
        internal int NativeStops => Volatile.Read(ref _stops);

        internal VirtualCameraFixture(Func<VideoFrame?> provider, int publishResult = 0,
            Action? onStop = null, int startResult = 0, Action? onStarted = null)
        {
            _service = Activator.CreateInstance(_type, Members, binder: null,
                args:
                [
                    (Func<ulong, uint, uint, VideoFrame?>)((handle, width, height) =>
                    {
                        InteractionAssert(handle == 42 && width == 2 && height == 2,
                            "The camera requested a frame from a different source or format.");
                        return provider();
                    }),
                    (Func<uint, uint, int, int>)((width, height, fps) =>
                    {
                        InteractionAssert(width == 2 && height == 2 && fps == 30,
                            "Unexpected native camera start parameters.");
                        Interlocked.Increment(ref _starts);
                        return startResult;
                    }),
                    (Func<VideoFrame, int>)(frame =>
                    {
                        Frames.Enqueue(frame);
                        Published.TrySetResult();
                        return publishResult;
                    }),
                    (Func<int>)(() =>
                    {
                        Interlocked.Increment(ref _stops);
                        onStop?.Invoke();
                        return 0;
                    }),
                ], culture: null)!;
            _type.GetEvent("StatusChanged", Members)!.GetAddMethod(nonPublic: true)!
                .Invoke(_service,
                [
                    (Action<string, bool>)((message, failed) =>
                    {
                        if (!failed && message == "VirtualCamera")
                        {
                            onStarted?.Invoke();
                            return;
                        }
                        Interlocked.Increment(ref _terminalEvents);
                        Completed.TrySetResult((message, failed));
                    }),
                ]);
        }

        internal Task StartAsync(CancellationToken cancellationToken = default) =>
            (Task)_type.GetMethod("StartAsync", Members)!.Invoke(_service,
                [42UL, 2U, 2U, 30, cancellationToken])!;

        internal Task StopAsync() =>
            (Task)_type.GetMethod("StopAsync", Members)!.Invoke(_service, null)!;

        internal async Task WaitForFrameAsync(VideoFrame expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!Frames.Any(frame => ReferenceEquals(frame, expected)))
            {
                InteractionAssert(!Completed.Task.IsCompleted,
                    "The camera stopped before publishing a valid replacement frame.");
                await Task.Delay(10, timeout.Token);
            }
        }

        internal void AssertStopped(bool failed, int expectedStops = 1)
        {
            InteractionAssert(Completed.Task.IsCompletedSuccessfully &&
                Completed.Task.Result.Failed == failed && !IsRunning,
                "The camera reported an unexpected terminal status.");
            InteractionAssert(Volatile.Read(ref _starts) == 1 &&
                Volatile.Read(ref _stops) == expectedStops && Volatile.Read(ref _terminalEvents) == 1,
                "A camera run must release its native backend and report completion once.");
            AssertRunReleased();
        }

        internal void AssertRunReleased()
        {
            InteractionAssert((ulong)_type.GetProperty("SessionHandle", Members)!
                    .GetValue(_service)! == 0 &&
                _type.GetField("_runTask", Members)!.GetValue(_service) is null &&
                _type.GetField("_runCancellation", Members)!.GetValue(_service) is null,
                "A stopped camera retained its source or run ownership.");
        }

        public ValueTask DisposeAsync() => ((IAsyncDisposable)_service).DisposeAsync();
    }
}
