using System.Collections.Concurrent;
using System.Text.Json;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Services;

public sealed partial class DirectUsbInputBridge
{
    internal bool SupportsDeviceHub { get; private set; }
    private readonly ConcurrentDictionary<string, (long Generation, TaskCompletionSource<JsonElement> Result)> _hubRequests = new();
    private readonly SemaphoreSlim _hubGate = new(1, 1);

    private void CompleteDeviceHub(JsonElement root)
    {
        if (!root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
            !_hubRequests.TryGetValue(id.GetString()!, out var pending) ||
            !root.TryGetProperty("generation", out var gen) || gen.GetInt64() != pending.Generation) return;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
            pending.Result.TrySetResult(root.GetProperty("result").Clone());
        else
        {
            var code = root.TryGetProperty("code", out var value) ? value.GetString() ?? "DEVICE_HUB_UNAVAILABLE" : "DEVICE_HUB_UNAVAILABLE";
            var type = root.TryGetProperty("errorType", out var errorType) ? errorType.GetString() : null;
            pending.Result.TrySetException(new AutomationException(code,
                $"Device operation failed ({code}{(type is null ? "" : ": " + type)}). Operations are never automatically retried.",
                code == "INVALID_REQUEST" ? 422 : code is "CANCELLED" or "SESSION_CHANGED" ? 409 : 503));
        }
    }

    private void CancelDeviceHub()
    {
        foreach (var pending in _hubRequests.Values)
            pending.Result.TrySetException(new AutomationException("SESSION_CHANGED", "Device control disconnected.", 503));
    }

    internal async Task<JsonElement> DeviceHubAsync(string action, object? arguments, Func<bool> current,
        CancellationToken cancellation)
    {
        if (!SupportsDeviceHub || !IsReady)
            throw new AutomationException("CAPABILITY_NOT_SUPPORTED", "Enable the dev device bridge first.", 422);
        var policy = DeviceHubContract.Require(action);
        if (!await _hubGate.WaitAsync(0, cancellation))
            throw new AutomationException("CONTROL_LOCKED", "Another Device Hub operation is in progress.");
        var id = Guid.NewGuid().ToString("N");
        var generation = InputGeneration;
        var wire = Interlocked.Read(ref _wireGeneration);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hubRequests[id] = (wire, completion);
        try
        {
            using var admission = !policy.ReadOnly ? InputAdmission?.Invoke() : null;
            await _sendLock.WaitAsync(cancellation);
            try
            {
                if (!IsReady || generation != InputGeneration || !current()) throw new OperationCanceledException();
                await WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = CoreDeviceTouchProtocol.MessageSchema, kind = "hub_request", requestId = id,
                    generation = wire, seq = NextSequence(), action, arguments
                }), cancellation);
            }
            finally { _sendLock.Release(); }
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(policy.Timeout + 5), cancellation);
            if (generation != InputGeneration || !current()) throw new OperationCanceledException();
            return result;
        }
        finally
        {
            if (!completion.Task.IsCompleted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await _sendLock.WaitAsync(cleanup.Token);
                    try
                    {
                        if (IsReady && generation == InputGeneration)
                            await WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(new
                            { schema = CoreDeviceTouchProtocol.MessageSchema, kind = "cancel_hub", requestId = id,
                                generation = wire, seq = NextSequence() }), cleanup.Token);
                    }
                    finally { _sendLock.Release(); }
                    try { await completion.Task.WaitAsync(cleanup.Token); }
                    catch (AutomationException) { }
                }
                catch (Exception error) when (error is OperationCanceledException or System.IO.IOException or InvalidOperationException)
                {
                    // A launch whose cancellation was not acknowledged may still be pending.
                    // Retire the route before allowing a new control owner.
                    if (generation == InputGeneration) await StopAsync();
                }
            }
            _hubRequests.TryRemove(id, out _);
            if (completion.Task.IsFaulted) _ = completion.Task.Exception;
            _hubGate.Release();
        }
    }
}
