using System.Diagnostics;

namespace IPhoneMirror.App.Services;

// Each control owns one contact and one worker. Mouse input is coalesced into a
// bounded displacement, never a queue of future down/move/up gestures.
internal sealed class KeyboardMappingContinuousEngine
{
    private sealed class Run(KeyboardMappingEntry entry, MappedTouchRoute route, Func<int, bool>? physical)
    {
        internal readonly KeyboardMappingEntry Entry = entry;
        internal readonly MappedTouchRoute Route = route;
        internal readonly Func<int, bool>? Physical = physical;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal int Keys, LastHorizontal = 1, LastVertical = 1;
        internal double Dx, Dy;
    }
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Run> _runs = [];
    internal Task Completion { get { lock (_gate) return Task.WhenAll(_runs.Values.Select(r => r.Done.Task)); } }
    internal bool HasRelativeDrag { get { lock (_gate) return _runs.Values.Any(r => r.Entry.Action == MappedTouchAction.RelativeDrag && !r.Stop.IsCancellationRequested); } }
    internal event Action<Exception>? Failed;

    internal void Input(KeyboardMappingEntry entry, int direction, bool down,
        Func<MappedTouchRoute?> capture, Func<int, bool>? physical = null)
    {
        lock (_gate)
        {
            _runs.TryGetValue(entry.Id, out var run);
            var previous = run?.Stop.IsCancellationRequested == true ? run.Done.Task : Task.CompletedTask;
            if (!previous.IsCompleted) run = null;
            if (entry.Action == MappedTouchAction.RelativeDrag && entry.RelativeDrag.Toggle)
            {
                if (!down) return;
                if (run is not null) { run.Stop.Cancel(); return; }
            }
            if (run is null)
            {
                if (!down || capture() is not { } route) return;
                if (_runs.Values.Count(r => r.Route.Target == route.Target && r.Entry.Id != entry.Id) >= CoreDeviceTouchProtocol.MaxSlots) return;
                if (entry.Validate() is { } error) throw new ArgumentException(error);
                run = new(entry, route, physical);
                _runs[entry.Id] = run;
                var started = run;
                _ = Task.Run(async () => { await previous.ConfigureAwait(false); await ExecuteAsync(started).ConfigureAwait(false); });
            }
            if (down)
            {
                run.Keys |= 1 << direction;
                if (direction is 0 or 1) run.LastVertical = direction == 0 ? -1 : 1;
                if (direction is 2 or 3) run.LastHorizontal = direction == 2 ? -1 : 1;
            }
            else run.Keys &= ~(1 << direction);
            if (run.Keys == 0) run.Stop.Cancel();
        }
    }

    internal void Move(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        lock (_gate)
            foreach (var run in _runs.Values)
            {
                if (run.Entry.Action != MappedTouchAction.RelativeDrag || run.Stop.IsCancellationRequested) continue;
                var p = run.Entry.RelativeDrag;
                // Sensitivity is normalized displacement per 1000 raw counts.
                run.Dx = Math.Clamp(run.Dx + dx * p.SensitivityX / 1000 * (p.InvertX ? -1 : 1), -p.Radius, p.Radius);
                run.Dy = Math.Clamp(run.Dy + dy * p.SensitivityY / 1000 * (p.InvertY ? -1 : 1), -p.Radius, p.Radius);
            }
    }
    internal void Cancel()
    {
        lock (_gate) foreach (var run in _runs.Values) run.Stop.Cancel();
    }

    private async Task ExecuteAsync(Run run)
    {
        var e = run.Entry;
        var route = run.Route;
        var token = run.Stop.Token;
        var point = route.Transform(e.X, e.Y);
        var anchor = point;
        var pressed = false;
        var offset = (X: 0d, Y: 0d);
        var previousTick = Stopwatch.GetTimestamp();
        async Task Send(string action, (double X, double Y) next)
        {
            token.ThrowIfCancellationRequested();
            if (!route.IsCurrent()) throw new OperationCanceledException();
            point = next;
            if (action == "down") pressed = true;
            await route.SendAsync(action, point.X, point.Y, token).ConfigureAwait(false);
            if (action == "up") pressed = false;
        }
        (double X, double Y) Position(double x, double y)
        {
            var d = route.TransformOffset?.Invoke(x, y) ?? (x, y);
            return (Math.Clamp(anchor.X + d.Item1, 0, 1), Math.Clamp(anchor.Y + d.Item2, 0, 1));
        }
        try
        {
            await Send("down", anchor).ConfigureAwait(false);
            while (true)
            {
                await Task.Delay(8, token).ConfigureAwait(false);
                if (!route.IsCurrent()) break;
                int keys, lastX, lastY;
                double dx, dy;
                lock (_gate)
                {
                    // Reconcile only releases; polling never invents a press or
                    // acquires a key that belongs to a shortcut/another owner.
                    if (run.Physical is not null && Stopwatch.GetElapsedTime(run.Started).TotalMilliseconds > 30 &&
                        !(e.Action == MappedTouchAction.RelativeDrag && e.RelativeDrag.Toggle))
                        for (var i = 0; i < 4; i++)
                            if ((run.Keys & (1 << i)) != 0 && !run.Physical(i)) run.Keys &= ~(1 << i);
                    keys = run.Keys; lastX = run.LastHorizontal; lastY = run.LastVertical;
                    dx = run.Dx; dy = run.Dy; run.Dx = run.Dy = 0;
                }
                if (keys == 0) break;
                if (e.Action == MappedTouchAction.Joystick)
                {
                    var x = ((keys & 8) != 0 ? 1 : 0) - ((keys & 4) != 0 ? 1 : 0);
                    var y = ((keys & 2) != 0 ? 1 : 0) - ((keys & 1) != 0 ? 1 : 0);
                    if (!e.Joystick.OppositeNeutral)
                    {
                        if ((keys & 12) == 12) x = lastX;
                        if ((keys & 3) == 3) y = lastY;
                    }
                    var length = Math.Max(1, Math.Sqrt(x * x + y * y));
                    var target = (X: x / length * e.Joystick.Radius, Y: y / length * e.Joystick.Radius);
                    var now = Stopwatch.GetTimestamp();
                    var ms = Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds;
                    var ramp = offset == (0d, 0d) ? e.Joystick.StartupMs : e.Joystick.TurnMs;
                    var amount = ramp == 0 ? 1 : Math.Min(1, ms / ramp);
                    offset = (offset.X + (target.X - offset.X) * amount, offset.Y + (target.Y - offset.Y) * amount);
                    previousTick = now;
                }
                else
                {
                    if (dx == 0 && dy == 0) continue;
                    var radius = e.RelativeDrag.Radius;
                    if (e.RelativeDrag.Recenter && (Math.Abs(offset.X + dx) > radius || Math.Abs(offset.Y + dy) > radius ||
                        point.X is <= 0 or >= 1 || point.Y is <= 0 or >= 1))
                    {
                        await Send("up", point).ConfigureAwait(false);
                        offset = (0, 0);
                        await Send("down", anchor).ConfigureAwait(false);
                    }
                    offset = (Math.Clamp(offset.X + dx, -radius, radius), Math.Clamp(offset.Y + dy, -radius, radius));
                }
                var next = Position(offset.X, offset.Y);
                if (Math.Abs(next.X - point.X) + Math.Abs(next.Y - point.Y) > .00001)
                    await Send("move", next).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Failed?.Invoke(error); }
        finally
        {
            try
            {
                if (pressed)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await route.SendAsync("up", point.X, point.Y, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) { Failed?.Invoke(error); }
            finally
            {
                lock (_gate)
                    if (_runs.TryGetValue(e.Id, out var current) && ReferenceEquals(current, run)) _runs.Remove(e.Id);
                run.Done.TrySetResult();
                run.Stop.Dispose();
            }
        }
    }
}
