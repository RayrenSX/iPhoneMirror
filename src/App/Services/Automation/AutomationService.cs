using System.Diagnostics;
using System.Text;

namespace IPhoneMirror.App.Services.Automation;

internal sealed class AutomationService(IAutomationBackend backend, AutomationOwnership ownership,
    DeviceInputService input) : IAsyncDisposable
{
    private readonly KeyboardMappingExecutor _gestures = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _screenshots = new(2, 2);
    private readonly object _frameLock = new();
    private readonly Dictionary<string, Task<byte[]>> _frames = [];
    private Task? _maintenance;
    private readonly object _admissionLock = new();
    private bool _accepting;
    internal AutomationOwnership Ownership => ownership;
    internal void Start()
    {
        lock (_admissionLock) _accepting = true;
        _maintenance ??= MaintainAsync();
    }
    internal void Suspend() { lock (_admissionLock) _accepting = false; }
    internal Task<IReadOnlyList<AutomationDevice>> DevicesAsync(CancellationToken token) => backend.GetDevicesAsync(token);
    internal Task<AutomationTarget> TargetAsync(string id, CancellationToken token) => backend.ResolveAsync(id, token);

    internal async Task<object> AcquireAsync(string id, string principal, string? token, int seconds, CancellationToken cancellation)
    {
        if (seconds is < 5 or > 300) throw new AutomationException("INVALID_DURATION", "Lease duration must be 5..300 seconds.", 422);
        var target = await backend.ResolveAsync(id, cancellation);
        Online(target);
        if (!target.Device.ControlAvailable) throw new AutomationException("CONTROL_NOT_AVAILABLE", "Enable a bound device control transport in iPhoneMirror first.", 503);
        AutomationOwnership.Lease? lease = null;
        await backend.ReserveAsync(target.PhysicalId, () =>
        {
            lock (_admissionLock)
            {
                if (!_accepting) throw new AutomationException("CONTROL_NOT_AVAILABLE", "The API server is stopping.", 503);
                lease = ownership.Acquire(target, principal, token, seconds);
            }
        }, cancellation);
        return new { sessionToken = lease!.Token, expiresAt = lease.ExpiresAt };
    }

    internal async Task ReleaseAsync(string id, string principal, string? token, CancellationToken cancellation)
    {
        var target = await backend.ResolveAsync(id, cancellation);
        var lease = ownership.Snapshot().FirstOrDefault(l => l.PhysicalId == target.PhysicalId && l.Principal == principal && l.Token == token);
        if (lease is null)
        {
            if (token?.Length == 64 && !ownership.Snapshot().Any(l => l.PhysicalId == target.PhysicalId)) return;
            throw new AutomationException("CONTROL_LOCKED", "The control session does not belong to this client.");
        }
        await ReleaseLeaseAsync(lease);
    }

    internal async Task<object> InputAsync(string id, string principal, string? session, string action,
        AutomationInput request, CancellationToken cancellation)
    {
        var target = await backend.ResolveAsync(id, cancellation);
        Online(target);
        var lease = ownership.Require(target.PhysicalId, principal, session);
        using var inputOwner = ownership.Use(lease);
        if (target.Device.Geometry.Version != lease.Target.Device.Geometry.Version ||
            (request.GeometryVersion is not null && request.GeometryVersion != target.Device.Geometry.Version))
            throw new AutomationException("GEOMETRY_CHANGED", "Refresh device geometry and acquire a new control session.");
        if (!await lease.Operation.WaitAsync(0, cancellation))
            throw new AutomationException("CONTROL_LOCKED", "Another input operation is in progress.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease.Cancellation.Token, _stop.Token);
        var ct = linked.Token;
        bool Current() => ownership.IsCurrent(lease) && target.IsCurrent();
        try
        {
            if (!Current()) throw new OperationCanceledException();
            if (lease.Touch is not null && action != "touch-up")
                throw new AutomationException("CONTROL_LOCKED", "Release the active touch before another operation.");
            if (action == "key")
            {
                Supported(target.Device.Capabilities.Keyboard);
                var (modifier, usage) = ParseKey(request.Key);
                await input.KeyAsync(target.KeyboardRoute!, modifier, usage, Current, ct);
            }
            else if (action == "text")
            {
                Supported(target.Device.Capabilities.TextInput);
                ValidateText(request.Text);
                var gate = input.KeyboardGate(target.KeyboardRoute!);
                await gate.WaitAsync(ct);
                try { await target.Clipboard!("paste_text", request.Text, ct); }
                finally { gate.Release(); }
            }
            else if (action == "touch-up")
            {
                if (lease.Touch is null || request.TouchId != lease.TouchId)
                    throw new AutomationException("INVALID_REQUEST", "Unknown touchId for this control session.", 400);
                await ReleaseTouchAsync(lease);
            }
            else
            {
                Supported(target.Device.Capabilities.Tap);
                var capturedRoute = target.TouchRoute(Current) ?? throw new AutomationException("CONTROL_NOT_AVAILABLE", "The touch route is unavailable.", 503);
                MappedTouchRoute? route = null;
                route = capturedRoute with { SendAsync = async (phase, x, y, token) =>
                {
                    if (phase == "down")
                    {
                        lease.Touch = route;
                        lease.TouchDeadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
                    }
                    lease.Position = (x, y);
                    await capturedRoute.SendAsync(phase, x, y, token);
                    if (phase == "up") { lease.Touch = null; lease.TouchId = null; }
                } };
                var geometry = target.Device.Geometry;
                (double X, double Y) Point(double? x, double? y)
                {
                    if (x is null || y is null || !double.IsFinite(x.Value) || !double.IsFinite(y.Value) ||
                        x < 0 || y < 0 || x >= geometry.Width || y >= geometry.Height || geometry.Width == 0 || geometry.Height == 0)
                        throw new AutomationException("INVALID_COORDINATE", "Coordinates must lie inside the current capture frame.", 422);
                    return (x.Value / geometry.Width, y.Value / geometry.Height);
                }
                var points = action switch
                {
                    "tap" or "long-press" or "touch-down" => new[] { Point(request.X, request.Y) },
                    "swipe" => [Point(request.X1, request.Y1), Point(request.X2, request.Y2)],
                    "touch-path" when request.Points is { Length: >= 2 and <= 256 } => request.Points.Select(p => Point(p?.X, p?.Y)).ToArray(),
                    _ => throw new AutomationException("INVALID_REQUEST", "Unknown action or invalid path (2..256 points required).", 400)
                };
                var duration = action is "tap" or "touch-down" ? 0.04 : request.Duration;
                if (duration is null || !double.IsFinite(duration.Value) || (action is not ("tap" or "touch-down") && duration is < .05 or > 10))
                    throw new AutomationException("INVALID_DURATION", "Duration must be 0.05..10 seconds.", 422);
                if (action == "swipe" && points[0] == points[^1])
                    throw new AutomationException("INVALID_COORDINATE", "A swipe must have different endpoints.", 422);
                if (action == "touch-down")
                {
                    lease.Touch = route;
                    lease.TouchId = Guid.NewGuid().ToString("N");
                    lease.Position = route.Transform(points[0].X, points[0].Y);
                    lease.TouchDeadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
                    try { await route.SendAsync("down", lease.Position.X, lease.Position.Y, ct); }
                    catch { await ReleaseTouchAsync(lease); throw; }
                    return new { status = "dispatched", touchId = lease.TouchId };
                }
                var gesture = new TouchGesture(Guid.NewGuid(), action == "tap" ? MappedTouchAction.Tap :
                    action == "long-press" ? MappedTouchAction.LongPress : MappedTouchAction.Swipe,
                    points[0].X, points[0].Y, points[^1].X, points[^1].Y, (int)Math.Round(duration.Value * 1000),
                    Path: action == "touch-path" ? points : null);
                if (!await _gestures.ExecuteGestureAsync(gesture, route, ct))
                    throw new AutomationException("CONTROL_LOCKED", "The device touch budget is exhausted.");
            }
            if (!Current()) throw new OperationCanceledException();
            return new { status = "dispatched" };
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException)
        { throw new AutomationException("INPUT_FAILED", "The input could not be dispatched; its effect may be unknown.", 500); }
        finally { lease.Operation.Release(); }
    }

    internal async Task<string> ClipboardAsync(string id, string principal, string? session,
        bool write, string? text, CancellationToken cancellation)
    {
        var target = await backend.ResolveAsync(id, cancellation);
        Online(target);
        Supported(write ? target.Device.Capabilities.ClipboardWrite : target.Device.Capabilities.ClipboardRead);
        if (!write) return await target.Clipboard!("read_clipboard", null, cancellation) ?? "";
        ValidateText(text);
        var lease = ownership.Require(target.PhysicalId, principal, session);
        using var inputOwner = ownership.Use(lease);
        if (!await lease.Operation.WaitAsync(0, cancellation)) throw new AutomationException("CONTROL_LOCKED", "An input operation is in progress.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease.Cancellation.Token, _stop.Token);
        try { return await target.Clipboard!("write_clipboard", text, linked.Token) ?? ""; }
        finally { lease.Operation.Release(); }
    }

    internal async Task<byte[]> ScreenshotAsync(string id, CancellationToken cancellation)
    {
        var target = await backend.ResolveAsync(id, cancellation);
        Online(target);
        if (!target.Device.Capabilities.Screenshot)
            throw new AutomationException("SCREENSHOT_UNAVAILABLE", "Start capture and wait for a device frame.", 503);
        Task<byte[]> task;
        var frameKey = id + ":" + target.Device.Geometry.Version;
        lock (_frameLock)
        {
            if (!_frames.TryGetValue(frameKey, out task!))
            {
                _frames[frameKey] = task = CapturePngAsync(target);
                _ = RetireFrameAsync(frameKey, task);
            }
        }
        return await task.WaitAsync(cancellation);
    }
    private async Task RetireFrameAsync(string key, Task<byte[]> task)
    {
        try { await task; }
        catch { /* The request observes the error; still evict abandoned work. */ }
        finally { lock (_frameLock) if (_frames.GetValueOrDefault(key) == task) _frames.Remove(key); }
    }
    private async Task<byte[]> CapturePngAsync(AutomationTarget target)
    {
        await Task.Yield();
        if (!await _screenshots.WaitAsync(0, _stop.Token))
            throw new AutomationException("RATE_LIMITED", "Screenshot encoders are busy.", 429);
        try
        {
            var frame = await target.Capture(_stop.Token) ?? throw new AutomationException("SCREENSHOT_UNAVAILABLE", "No device frame is available.", 503);
            return await Task.Run(() => ScreenshotService.EncodePng(frame), _stop.Token);
        }
        finally { _screenshots.Release(); }
    }

    private async Task MaintainAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
                foreach (var lease in ownership.Snapshot())
                {
                    try
                    {
                        if (!ownership.IsCurrent(lease)) await ReleaseLeaseAsync(lease);
                        else if (lease.Touch is not null && Stopwatch.GetTimestamp() >= lease.TouchDeadline && await lease.Operation.WaitAsync(0))
                        {
                            using var inputOwner = ownership.Use(lease);
                            try { await ReleaseTouchAsync(lease); }
                            finally { lease.Operation.Release(); }
                        }
                    }
                    catch (Exception error)
                    {
                        DiagnosticLogger.Warning("AutomationAPI", "lease_cleanup_failed", ("type", error.GetType().Name));
                    }
                }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private static async Task ReleaseTouchAsync(AutomationOwnership.Lease lease)
    {
        if (lease.Touch is not { } route) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await route.SendAsync("up", lease.Position.X, lease.Position.Y, timeout.Token);
        lease.Touch = null;
        lease.TouchId = null;
    }
    private async Task ReleaseLeaseAsync(AutomationOwnership.Lease lease)
    {
        await lease.ReleaseGate.WaitAsync();
        try
        {
            if (!ownership.Snapshot().Contains(lease)) return;
            ownership.BeginRelease(lease);
            using var inputOwner = ownership.Use(lease);
            // Keep ownership closed when drain/release fails. A disconnected old
            // session is already revoked and must never release its replacement.
            if (!await lease.Operation.WaitAsync(TimeSpan.FromSeconds(2)))
                throw new AutomationException("CONTROL_NOT_AVAILABLE", "Input cleanup is still pending.", 503);
            try
            {
                if ((lease.Target.SessionCurrent ?? lease.Target.IsCurrent)())
                {
                    await ReleaseTouchAsync(lease);
                    if (lease.Target.KeyboardRoute is { } keyboard)
                        await keyboard.SendAsync(0, [], null).WaitAsync(TimeSpan.FromSeconds(1));
                }
                ownership.FinishRelease(lease);
            }
            finally { lease.Operation.Release(); }
        }
        finally { lease.ReleaseGate.Release(); }
    }
    internal async Task RevokeAllAsync()
    {
        await Task.WhenAll(ownership.Snapshot().Select(ReleaseLeaseAsync));
    }
    public async ValueTask DisposeAsync()
    {
        Suspend();
        _stop.Cancel();
        await RevokeAllAsync();
        if (_maintenance is not null) await _maintenance;
    }
    private static void Online(AutomationTarget target)
    { if (!target.Device.Online) throw new AutomationException("DEVICE_NOT_CONNECTED", "The device is disconnected.", 503); }
    private static void Supported(bool supported)
    { if (!supported) throw new AutomationException("CAPABILITY_NOT_SUPPORTED", "This operation is not supported by the current connection.", 422); }
    private static void ValidateText(string? text)
    { if (text is null || Encoding.UTF8.GetByteCount(text) > 65536) throw new AutomationException("INVALID_REQUEST", "Text is required and must not exceed 64 KiB UTF-8.", 400); }
    private static (byte Modifier, byte Usage) ParseKey(string? name)
    {
        var key = name?.ToUpperInvariant();
        var vk = key switch
        {
            "ENTER" => 0x0D, "ESCAPE" => 0x1B, "TAB" => 9, "SPACE" => 0x20,
            "BACKSPACE" => 8, "DELETE" => 0x2E, "INSERT" => 0x2D,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            "SHIFT" => 0x10, "CTRL" => 0x11, "ALT" => 0x12,
            { Length: 1 } when char.IsAsciiLetterOrDigit(key[0]) => key[0],
            _ when key?.StartsWith('F') == true && int.TryParse(key.AsSpan(1), out var f) && f is >= 3 and <= 12 => 0x6F + f,
            _ => 0
        };
        if (!DeviceKeyMap.TryMapVirtualKey(vk, out var usage, out var modifier))
            throw new AutomationException("INVALID_REQUEST", "Unsupported key name.", 400);
        return (modifier, usage);
    }
}
