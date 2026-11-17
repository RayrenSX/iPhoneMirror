using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunScreenshotBenchmark(string fixture)
    {
        using var source = System.IO.File.OpenRead(fixture);
        var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(source,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0],
            System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var frame = new VideoFrame((uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight,
            (uint)bitmap.PixelWidth * 4, 0, pixels);
        foreach (var (name, encode) in new (string, Func<byte[]>)[] {
            ("WPF", () => ScreenshotService.EncodePng(frame)), ("Fast", () => FastPngEncoder.Encode(frame, default)) })
        {
            _ = encode();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var length = 0;
            for (var i = 0; i < 8; i++) length = encode().Length;
            Console.WriteLine($"{name}: {frame.Width}x{frame.Height}, mean_ms={timer.Elapsed.TotalMilliseconds / 8:F2}, allocated_bytes={(GC.GetAllocatedBytesForCurrentThread() - allocated) / 8}, png_bytes={length}");
        }
        return 0;
    }

    private static async Task TestAutomationScreenshotQueueAsync()
    {
        using var stop = new CancellationTokenSource();
        var queue = new AutomationScreenshotQueue(stop.Token);
        using (var cancelledBeforeQueue = new CancellationTokenSource())
        {
            cancelledBeforeQueue.Cancel();
            try
            {
                await queue.CaptureAsync("already-cancelled", _ => throw new InvalidOperationException("Cancelled work was started."), cancelledBeforeQueue.Token);
                throw new InvalidOperationException("Pre-cancelled screenshot completed successfully.");
            }
            catch (OperationCanceledException) { }
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0; var maximum = 0; var captures = 0;
        async Task<byte[]> Capture(CancellationToken token)
        {
            Interlocked.Increment(ref captures);
            var active = Interlocked.Increment(ref running);
            int previous;
            do { previous = maximum; } while (active > previous && Interlocked.CompareExchange(ref maximum, active, previous) != previous);
            if (active == 2) entered.TrySetResult();
            try { await release.Task.WaitAsync(token); return [1, 2, 3]; }
            finally { Interlocked.Decrement(ref running); }
        }
        // Fifteen sources submit at once. Joining a source must not reserve
        // another encoder, and cancelling one client must preserve its peers.
        var jobs = Enumerable.Range(0, 15).Select(i => queue.CaptureAsync("phone-" + i, Capture, default)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var abandonedWaiter = queue.CaptureAsync("phone-0", Capture, cancelled.Token);
        cancelled.Cancel();
        try { await abandonedWaiter; throw new InvalidOperationException("Cancelled client received a screenshot."); }
        catch (OperationCanceledException) { }
        release.SetResult();
        await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(5));
        if (maximum != 2 || captures != 15) throw new InvalidOperationException("Screenshot burst exceeded the encoder bound or duplicated a source.");

        var stalled = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lastWaiter = new CancellationTokenSource();
        var allCancelled = queue.CaptureAsync("stalled", _ => { started.SetResult(); return stalled.Task; }, lastWaiter.Token);
        await started.Task;
        lastWaiter.Cancel();
        try { await allCancelled; throw new InvalidOperationException("Abandoned client did not return."); }
        catch (OperationCanceledException) { }
        try
        {
            await queue.CaptureAsync("stalled", _ => Task.FromResult(new byte[] { 9 }), default);
            throw new InvalidOperationException("Unfinished native work was replaced by another job.");
        }
        catch (AutomationException error) when (error.Status == 429) { }
        stalled.SetResult([7]);
        try { await queue.DrainAsync(); } catch (OperationCanceledException) { }
        await queue.CaptureAsync("stalled", _ => Task.FromResult(new byte[] { 9 }), default);

        var blocked = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capacityJobs = Enumerable.Range(0, AutomationScreenshotQueue.Capacity)
            .Select(i => queue.CaptureAsync("capacity-" + i, _ => blocked.Task, default)).ToArray();
        try
        {
            await queue.CaptureAsync("overflow", _ => blocked.Task, default);
            throw new InvalidOperationException("Screenshot queue grew beyond its bound.");
        }
        catch (AutomationException error) when (error.Status == 429) { }
        blocked.SetResult([1]);
        await Task.WhenAll(capacityJobs);

        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var onStop = queue.CaptureAsync("stop", async token => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, token); return []; }, default);
        await waiting.Task;
        stop.Cancel();
        try { await onStop; throw new InvalidOperationException("Stopping left a screenshot running."); }
        catch (OperationCanceledException) { }
        try { await queue.DrainAsync(); } catch (OperationCanceledException) { }

        var malformed = new VideoFrame(1, uint.MaxValue, 4, 0, []);
        try { ScreenshotService.CreateBitmap(malformed); throw new InvalidOperationException("Overflowed frame dimensions were accepted."); }
        catch (System.IO.InvalidDataException) { }

        var owner = new AutomationOwnership();
        var backend = new AutomationTestBackend(owner);
        await using var service = new AutomationService(backend, owner, new DeviceInputService());
        backend.CaptureFrame = (_, _) => throw new System.IO.IOException("Failed readback");
        try { await service.ScreenshotAsync("a", default); throw new InvalidOperationException("Failed capture succeeded."); }
        catch (AutomationException error) when (error.Code == "SCREENSHOT_UNAVAILABLE" && error.Status == 503) { }
        backend.CaptureFrame = null;
        await service.ScreenshotAsync("a", default);
        backend.CaptureFrame = (_, _) => throw new TimeoutException("Readback timeout");
        try { await service.ScreenshotAsync("a", default); throw new InvalidOperationException("Timed-out capture succeeded."); }
        catch (AutomationException error) when (error.Code == "SCREENSHOT_UNAVAILABLE" && error.Status == 503) { }
        backend.CaptureFrame = null;
        backend.ScreenshotReady = false;
        var waitingForFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Resolving = () => waitingForFrame.TrySetResult();
        var warming = service.ScreenshotAsync("a", default);
        await waitingForFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Another status request can observe the new geometry before this
        // screenshot resolves it. The live capture is still eligible to wait.
        backend.Geometry++;
        backend.ScreenshotReady = true;
        await warming.WaitAsync(TimeSpan.FromSeconds(2));
        backend.Resolving = null;
        backend.ScreenshotReady = false;
        var readWithoutFrame = false;
        backend.CaptureFrame = (_, _) => { readWithoutFrame = true; return Task.FromResult<VideoFrame?>(null); };
        try { await service.ScreenshotAsync("a", default).WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("Missing frame was accepted."); }
        catch (AutomationException error) when (error.Code == "SCREENSHOT_UNAVAILABLE" && error.Status == 503) { }
        if (readWithoutFrame) throw new InvalidOperationException("A pending frame started native readback.");
        backend.ScreenshotReady = true;
        backend.CaptureFrame = null;
        var emptyReads = 0;
        backend.CaptureFrame = (_, _) => Task.FromResult<VideoFrame?>(Interlocked.Increment(ref emptyReads) <= 2
            ? null : new VideoFrame(2, 2, 8, 1, new byte[16]));
        await service.ScreenshotAsync("a", default).WaitAsync(TimeSpan.FromSeconds(2));
        backend.CaptureFrame = null;
        var oldFrame = new TaskCompletionSource<VideoFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.CaptureFrame = (_, _) => { readStarted.SetResult(); return oldFrame.Task; };
        var stale = service.ScreenshotAsync("a", default);
        await readStarted.Task;
        backend.Generation++;
        oldFrame.SetResult(new VideoFrame(1, 1, 4, 0, new byte[4]));
        try { await stale; throw new InvalidOperationException("Screenshot from a replaced capture was returned."); }
        catch (AutomationException error) when (error.Code == "SCREENSHOT_UNAVAILABLE") { }
        await TestScreenshotDeadlinesAsync();
        await TestScreenshotCancellationRaceAsync();
        TestFastScreenshotPng();
        Console.WriteLine("Screenshot burst: 15 sources, 2 encoders, coalescing, cancellation, bounded backlog, shutdown and failure recovery passed.");
    }

    private static async Task TestScreenshotCancellationRaceAsync()
    {
        using var stop = new CancellationTokenSource();
        var queue = new AutomationScreenshotQueue(stop.Token);
        // Race completion with the last waiter leaving, including callbacks
        // which are still using the token after the capture itself completes.
        for (var iteration = 0; iteration < 200; iteration++)
        {
            using var cancel = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = queue.CaptureAsync("race", _ => { entered.SetResult(); return finish.Task; }, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(Task.Run(cancel.Cancel), Task.Run(() => finish.TrySetResult([1])));
            try { await job.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            try { await queue.DrainAsync(); } catch (OperationCanceledException) { }
        }

        using var client = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = queue.CaptureAsync("slow-callback", token =>
        {
            token.Register(() =>
            {
                callbackEntered.SetResult();
                try
                {
                    if (!releaseCallback.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test callback was not released.");
                    using var registration = token.Register(() => { });
                    callbackCompleted.SetResult();
                }
                catch (Exception error) { callbackCompleted.SetException(error); }
            });
            started.SetResult();
            return native.Task;
        }, client.Token);
        await started.Task;
        client.Cancel();
        try
        {
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("Cancelled screenshot completed successfully."); }
            catch (OperationCanceledException) { }
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            native.SetResult([1]);
            try { await queue.DrainAsync(); } catch (OperationCanceledException) { }
            try
            {
                await queue.CaptureAsync("slow-callback", _ => Task.FromResult(new byte[] { 2 }), default);
                throw new InvalidOperationException("Unfinished cancellation cleanup was removed from the bounded backlog.");
            }
            catch (AutomationException error) when (error.Status == 429) { }
        }
        finally { releaseCallback.Set(); }
        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            while (true)
            {
                try { await queue.CaptureAsync("slow-callback", _ => Task.FromResult(new byte[] { 2 }), cleanupDeadline.Token); break; }
                catch (AutomationException error) when (error.Status == 429)
                { await Task.Delay(10, cleanupDeadline.Token); }
            }
        Console.WriteLine("Screenshot cancellation: 200 completion races and slow callbacks preserve token lifetime and prompt client cancellation.");
    }

    private static async Task TestScreenshotDeadlinesAsync()
    {
        using var stop = new CancellationTokenSource();
        var queue = new AutomationScreenshotQueue(stop.Token);
        var native = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        Task<byte[]> Read(CancellationToken _) { if (Interlocked.Increment(ref active) == 2) entered.SetResult(); return native.Task; }
        var first = queue.CaptureAsync("first", Read, default);
        var second = queue.CaptureAsync("second", Read, default);
        await entered.Task;
        try
        {
            await queue.CaptureAsync("waiting", Read, default).WaitAsync(TimeSpan.FromSeconds(4));
            throw new InvalidOperationException("Queue deadline did not reject a waiting screenshot.");
        }
        catch (AutomationException error) when (error.Status == 429) { }
        foreach (var job in new[] { first, second })
        {
            try { await job.WaitAsync(TimeSpan.FromSeconds(12)); throw new InvalidOperationException("Unresponsive native readback did not time out."); }
            catch (AutomationException error) when (error.Status == 503) { }
        }
        if (active != 2) throw new InvalidOperationException("A deadline started extra native operations.");
        native.SetResult([1]);
        try { await queue.DrainAsync(); } catch (OperationCanceledException) { }
        await queue.CaptureAsync("recovered", _ => Task.FromResult(new byte[] { 2 }), default);

        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowNative = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = queue.CaptureAsync("deadline-callback", token =>
        {
            token.Register(() =>
            {
                callbackEntered.SetResult();
                try { releaseCallback.Wait(); }
                finally { callbackDone.SetResult(); }
            });
            return slowNative.Task;
        }, default);
        try
        {
            try { await deadline.WaitAsync(TimeSpan.FromSeconds(12)); throw new InvalidOperationException("Screenshot deadline was ignored."); }
            catch (AutomationException error) when (error.Status == 503) { }
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            slowNative.SetResult([1]);
            try { await queue.DrainAsync(); } catch (OperationCanceledException) { }
        }
        finally { releaseCallback.Set(); slowNative.TrySetResult([1]); }
        await callbackDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static void TestFastScreenshotPng()
    {
        var random = new Random(501);
        foreach (var (width, height, padding) in new[] { (1, 1, 0), (3, 7, 12), (257, 129, 4), (319, 177, 0) })
        {
            var stride = width * 4 + padding;
            var pixels = new byte[stride * height];
            random.NextBytes(pixels);
            var frame = new VideoFrame((uint)width, (uint)height, (uint)stride, 0, pixels);
            var png = FastPngEncoder.Encode(frame, default);
            using var stream = new System.IO.MemoryStream(png);
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0],
                System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var decoded = new byte[width * height * 4];
            bitmap.CopyPixels(decoded, width * 4, 0);
            for (var y = 0; y < height; y++)
                if (!pixels.AsSpan(y * stride, width * 4).SequenceEqual(decoded.AsSpan(y * width * 4, width * 4)))
                    throw new InvalidOperationException("Fast PNG changed screenshot pixels, alpha, padding or channel order.");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { FastPngEncoder.Encode(new VideoFrame(1, 1, 4, 0, new byte[4]), cancelled.Token); throw new InvalidOperationException("Cancelled encoder produced an image."); }
        catch (OperationCanceledException) { }
        foreach (var invalid in new[] {
            new VideoFrame(0, 1, 4, 0, new byte[4]),
            new VideoFrame(1, 0, 4, 0, new byte[4]),
            new VideoFrame(2, 1, 4, 0, new byte[4]),
            new VideoFrame(2, 2, 8, 0, new byte[15]),
            new VideoFrame(uint.MaxValue, 1, 4, 0, new byte[4]),
            new VideoFrame(1, uint.MaxValue, 4, 0, new byte[4]),
            new VideoFrame(1, 2, uint.MaxValue, 0, new byte[4]) })
        {
            try { FastPngEncoder.Encode(invalid, default); throw new InvalidOperationException("Invalid frame layout reached the PNG encoder."); }
            catch (System.IO.InvalidDataException) { }
        }
        Console.WriteLine("Fast PNG: independent WPF decoding preserved all BGRA/alpha pixels, odd sizes, padded strides and multiple IDAT chunks.");
    }
}
