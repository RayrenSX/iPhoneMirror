namespace IPhoneMirror.App.Services;

internal sealed record MappedTouchRoute(string Target, Func<bool> IsCurrent,
    Func<string, double, double, CancellationToken, Task> SendAsync,
    Func<double, double, (double X, double Y)> Transform);

// One bounded gesture at a time. The caller captures an existing route; this
// class never resolves a device, connects a backend, or converts coordinates.
internal sealed class KeyboardMappingExecutor : IDisposable
{
    private CancellationTokenSource? _running;
    private TaskCompletionSource? _completion;
    internal Task Completion => _completion?.Task ?? Task.CompletedTask;
    internal bool IsBusy => _running is not null;
    internal void Cancel() => _running?.Cancel();
    public void Dispose() => Cancel();

    internal async Task<bool> ExecuteAsync(KeyboardMappingEntry mapping, MappedTouchRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (IsBusy) return false;
        if (mapping.Validate() is { } error) throw new ArgumentException(error, nameof(mapping));
        using var cancellation = new CancellationTokenSource();
        _running = cancellation;
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (mapping.IsSwipe)
            {
                var start = point;
                var end = route.Transform(mapping.EndPoint.X, mapping.EndPoint.Y);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                double progress;
                do
                {
                    await Delay(Math.Min(16, mapping.DurationMs));
                    progress = Math.Min(1, clock.Elapsed.TotalMilliseconds / mapping.DurationMs);
                    await Send("move", start.X + (end.X - start.X) * progress,
                        start.Y + (end.Y - start.Y) * progress);
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
            finally { _running = null; _completion.TrySetResult(); }
        }
    }
}
