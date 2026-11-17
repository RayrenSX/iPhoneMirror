namespace IPhoneMirror.App.Services.Automation;

// Coalesce before resolving the WPF/native target. Both active encoders and
// waiting sources are bounded, including when clients abandon their requests.
internal sealed class AutomationScreenshotQueue(CancellationToken stopping)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _encoders = new(2, 2);
    private readonly Dictionary<string, Work> _pending = new(StringComparer.Ordinal);
    internal const int Capacity = 32;
    internal static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    private sealed class Work(CancellationToken stopping)
    {
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        internal readonly long Started = System.Diagnostics.Stopwatch.GetTimestamp();
        internal Task<byte[]> Task = null!;
        internal int Waiters;
        internal bool Completed;
        internal bool Abandoned;
        internal bool Cancelling;
        internal bool Disposed;
    }

    internal async Task<byte[]> CaptureAsync(string source,
        Func<CancellationToken, Task<byte[]>> capture, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        stopping.ThrowIfCancellationRequested();
        Work work;
        lock (_gate)
        {
            stopping.ThrowIfCancellationRequested();
            if (!_pending.TryGetValue(source, out work!))
            {
                if (_pending.Count >= Capacity)
                    throw new AutomationException("RATE_LIMITED", "The screenshot queue is full.", 429);
                work = new Work(stopping);
                _pending.Add(source, work);
                work.Task = Task.Run(() => RunAsync(source, work, capture));
                _ = work.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else if (work.Abandoned || work.Cancellation.IsCancellationRequested)
                throw new AutomationException("RATE_LIMITED", "The previous screenshot is still finishing.", 429);
            work.Waiters++;
        }
        try
        {
            // Bound the waiter independently of native cancellation callbacks:
            // CancelAfter can block while running one of those callbacks.
            var remaining = OperationTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(work.Started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping);
            return await work.Task.WaitAsync(remaining, wait.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new AutomationException("SCREENSHOT_UNAVAILABLE", "The screenshot timed out. Try again after the current capture finishes.", 503);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            throw new AutomationException("SCREENSHOT_UNAVAILABLE", "The screenshot timed out. Try again after the current capture finishes.", 503);
        }
        finally
        {
            var cancel = false;
            lock (_gate)
            {
                work.Waiters--;
                if (work.Waiters == 0)
                {
                    if (!work.Completed)
                    {
                        work.Abandoned = true;
                        work.Cancelling = true;
                        cancel = true;
                    }
                    TryDispose(work);
                }
            }
            if (cancel)
                _ = CancelAbandonedAsync(source, work);
        }
    }

    private async Task CancelAbandonedAsync(string source, Work work)
    {
        // A client disconnect must return even if a native cancellation callback
        // is slow. Retain its source until both the callback and job finish.
        try { await work.Cancellation.CancelAsync().ConfigureAwait(false); }
        catch (Exception error)
        { DiagnosticLogger.Warning("AutomationAPI", "screenshot_cancel_failed", ("type", error.GetType().Name)); }
        finally
        {
            lock (_gate) { work.Cancelling = false; ReleaseCompleted(source, work); }
        }
    }

    private async Task<byte[]> RunAsync(string source, Work work,
        Func<CancellationToken, Task<byte[]>> capture)
    {
        var acquired = false;
        try
        {
            var token = work.Cancellation.Token;
            if (!await _encoders.WaitAsync(QueueTimeout, token).ConfigureAwait(false))
                throw new AutomationException("RATE_LIMITED", "Screenshot encoders are busy. Retry after a short delay.", 429);
            acquired = true;
            token.ThrowIfCancellationRequested();
            var result = await capture(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            // Retain the encoder slot until native readback/PNG work actually
            // finishes: timing out a waiter must not start extra encoders.
            if (acquired) _encoders.Release();
            lock (_gate)
            {
                work.Completed = true;
                ReleaseCompleted(source, work);
            }
        }
    }

    private void ReleaseCompleted(string source, Work work)
    {
        // Count slow cancellation cleanup against the same bounded backlog.
        // Otherwise completed jobs could create unlimited CancelAsync tasks.
        if (work.Completed && !work.Cancelling) _pending.Remove(source);
        TryDispose(work);
    }

    // All lifetime decisions are made under _gate. A cancellation callback
    // can finish the job before CancelAsync itself completes.
    private static void TryDispose(Work work)
    {
        if (!work.Completed || work.Waiters != 0 || work.Cancelling || work.Disposed) return;
        work.Disposed = true;
        work.Cancellation.Dispose();
    }

    internal Task DrainAsync()
    {
        lock (_gate) return Task.WhenAll(_pending.Values.Select(w => w.Task));
    }
}
