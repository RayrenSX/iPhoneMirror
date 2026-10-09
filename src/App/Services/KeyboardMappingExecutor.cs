namespace IPhoneMirror.App.Services;

internal sealed record MappedTouchRoute(string Target, Func<bool> IsCurrent,
    Func<string, double, double, CancellationToken, Task> SendAsync,
    Func<double, double, (double X, double Y)> Transform);

internal sealed record TouchGesture(Guid Id, MappedTouchAction Action, double X, double Y,
    double EndX, double EndY, int DurationMs, int IntervalMs = 100,
    IReadOnlyList<(double X, double Y)>? Path = null)
{
    internal bool IsSwipe => Action is >= MappedTouchAction.Swipe and <= MappedTouchAction.SwipeRight;
    internal (double X, double Y) EndPoint => (EndX, EndY);
}

// Up to five independent gestures. The caller captures an existing route; this
// class never resolves a device, connects a backend, or converts coordinates.
internal sealed class KeyboardMappingExecutor : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Target, Guid Id), (CancellationTokenSource Cancel, TaskCompletionSource Done)> _running = [];
    internal Task Completion { get { lock (_gate) return Task.WhenAll(_running.Values.Select(v => v.Done.Task)); } }
    internal bool IsBusy { get { lock (_gate) return _running.Count != 0; } }
    internal bool IsBusyFor(string target) { lock (_gate) return _running.Keys.Any(k => string.Equals(k.Target, target, StringComparison.OrdinalIgnoreCase)); }
    internal void Cancel()
    {
        lock (_gate)
            foreach (var run in _running.Values.ToArray()) run.Cancel.Cancel();
    }
    public void Dispose() => Cancel();

    internal Task<bool> ExecuteAsync(KeyboardMappingEntry mapping, MappedTouchRoute route,
        CancellationToken keyReleased = default, bool replayCompletedHold = false)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (mapping.Validate() is { } error) throw new ArgumentException(error, nameof(mapping));
        var end = mapping.EndPoint;
        return ExecuteGestureAsync(new TouchGesture(mapping.Id, mapping.Action, mapping.X, mapping.Y,
            end.X, end.Y, mapping.DurationMs, mapping.IntervalMs), route, keyReleased, replayCompletedHold);
    }

    internal async Task<bool> ExecuteGestureAsync(TouchGesture mapping, MappedTouchRoute route,
        CancellationToken keyReleased = default, bool replayCompletedHold = false)
    {
        // A physical press/release can both arrive before the dispatcher runs
        // its queued gesture. Preserve that completed lifetime as a down/up,
        // while generation/focus checks still revoke obsolete input.
        var completedHold = replayCompletedHold && mapping.Action == MappedTouchAction.HoldUntilRelease && keyReleased.IsCancellationRequested;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(completedHold ? default : keyReleased);
        var key = (route.Target.ToUpperInvariant(), mapping.Id);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        while (true)
        {
            Task? previousRelease = null;
            lock (_gate)
            {
                if (_running.TryGetValue(key, out var previous))
                {
                    if (mapping.Action != MappedTouchAction.HoldUntilRelease ||
                        (!replayCompletedHold && !previous.Cancel.IsCancellationRequested))
                        return false;
                    previousRelease = previous.Done.Task;
                }
                else
                {
                    if (_running.Keys.Count(k => k.Target == key.Item1) >= CoreDeviceTouchProtocol.MaxSlots) return false;
                    _running.Add(key, (cancellation, completion));
                    break;
                }
            }
            // A fresh press may arrive while the old press's up is still being
            // written. Preserve ordering without losing the new held contact.
            await previousRelease.WaitAsync(cancellation.Token);
        }
        var token = cancellation.Token;
        var point = (X: 0d, Y: 0d);
        var pressed = false;
        async Task Send(string action, double x, double y)
        {
            token.ThrowIfCancellationRequested();
            if (!route.IsCurrent()) throw new OperationCanceledException();
            point = (x, y);
            // Even a failed down write can have reached the backend. Release
            // conservatively, only to the captured generation, in finally.
            if (action == "down") pressed = true;
            await route.SendAsync(action, x, y, token);
            if (action == "up") pressed = false;
        }
        async Task Delay(int milliseconds)
        {
            // Check the route during long presses, not just at their end.
            var remaining = milliseconds;
            while (remaining > 0)
            {
                await Task.Delay(Math.Min(remaining, 16), token);
                if (!route.IsCurrent()) throw new OperationCanceledException();
                remaining -= Math.Min(remaining, 16);
            }
        }
        try
        {
            point = route.Transform(mapping.X, mapping.Y);
            await Send("down", point.X, point.Y);
            if (mapping.Action == MappedTouchAction.HoldUntilRelease)
            {
                if (completedHold) { await Send("up", point.X, point.Y); return true; }
                // Keep checking the captured route even when the key stays down.
                while (true) await Delay(16);
            }
            else if (mapping.IsSwipe || mapping.Path is not null)
            {
                var start = point;
                var end = route.Transform(mapping.EndPoint.X, mapping.EndPoint.Y);
                var path = mapping.Path?.Select(p => route.Transform(p.X, p.Y)).ToArray() ?? [start, end];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                double progress;
                do
                {
                    await Delay(Math.Min(16, mapping.DurationMs));
                    progress = Math.Min(1, clock.Elapsed.TotalMilliseconds / mapping.DurationMs);
                    var offset = progress * (path.Length - 1);
                    var index = Math.Min(path.Length - 2, (int)offset);
                    var fraction = offset - index;
                    await Send("move", path[index].X + (path[index + 1].X - path[index].X) * fraction,
                        path[index].Y + (path[index + 1].Y - path[index].Y) * fraction);
                } while (progress < 1);
            }
            else await Delay(mapping.Action == MappedTouchAction.LongPress ? mapping.DurationMs : 40);
            await Send("up", point.X, point.Y);
            if (mapping.Action == MappedTouchAction.DoubleTap)
            {
                await Delay(mapping.IntervalMs);
                await Send("down", point.X, point.Y);
                await Delay(40);
                await Send("up", point.X, point.Y);
            }
            return true;
        }
        finally
        {
            try
            {
                if (pressed)
                {
                    // The route delegate permits a release after cancellation
                    // but refuses it after reconnection or backend replacement.
                    using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await route.SendAsync("up", point.X, point.Y, releaseTimeout.Token);
                }
            }
            finally
            {
                lock (_gate) _running.Remove(key);
                completion.TrySetResult();
            }
        }
    }
}
