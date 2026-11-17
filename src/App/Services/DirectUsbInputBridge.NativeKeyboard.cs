using System.Collections.Concurrent;
using System.Text.Json;

namespace IPhoneMirror.App.Services;

public sealed partial class DirectUsbInputBridge
{
    internal bool NativeKeyboardVisible { get; set; } = true;
    internal bool SupportsNativeKeyboardVisibility { get; private set; }
    private readonly SemaphoreSlim _nativeKeyboardGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (long Generation, bool Visible, TaskCompletionSource<bool> Result)>
        _nativeKeyboardRequests = new();

    private void CompleteNativeKeyboard(JsonElement root)
    {
        if (!root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
            !_nativeKeyboardRequests.TryGetValue(id.GetString()!, out var pending) ||
            !root.TryGetProperty("generation", out var generation) ||
            !generation.TryGetInt64(out var wire) || wire != pending.Generation) return;
        if (root.TryGetProperty("uncertain", out var uncertain) && uncertain.ValueKind == JsonValueKind.True)
        {
            pending.Result.TrySetException(new InvalidOperationException("Keyboard route change was not acknowledged."));
            return;
        }
        pending.Result.TrySetResult(root.TryGetProperty("success", out var success) &&
            success.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("visible", out var visible) &&
            visible.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            visible.GetBoolean() == pending.Visible);
    }

    private void CancelNativeKeyboard()
    {
        SupportsNativeKeyboardVisibility = false;
        foreach (var pending in _nativeKeyboardRequests.Values) pending.Result.TrySetResult(false);
    }

    internal async Task<bool> SetNativeKeyboardVisibleAsync(bool visible, Func<bool> current,
        CancellationToken cancellation = default)
    {
        if (!IsReady || !SupportsNativeKeyboardVisibility) return false;
        await _nativeKeyboardGate.WaitAsync(cancellation).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        var generation = InputGeneration;
        var wire = Interlocked.Read(ref _wireGeneration);
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _nativeKeyboardRequests[id] = (wire, visible, result);
        var sent = false;
        try
        {
            using var admission = InputAdmission?.Invoke();
            await _sendLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (!IsReady || !SupportsNativeKeyboardVisibility || generation != InputGeneration || !current())
                    return false;
                cancellation.ThrowIfCancellationRequested();
                sent = true;
                await WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = CoreDeviceTouchProtocol.MessageSchema, kind = "native_keyboard_visibility",
                    requestId = id, generation = wire, seq = NextSequence(), visible
                }), cancellation).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
            var success = await result.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellation).ConfigureAwait(false);
            if (!success || generation != InputGeneration || !current()) return false;
            NativeKeyboardVisible = visible;
            return true;
        }
        catch (Exception) when (sent && (!result.Task.IsCompleted || result.Task.IsFaulted))
        {
            // A timed-out operation may still change keyboard ownership on the
            // phone. Retire this route rather than let a late command outlive
            // the UI owner that requested it. Never automatically replay it.
            if (generation == InputGeneration) await StopAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _nativeKeyboardRequests.TryRemove(id, out _);
            _nativeKeyboardGate.Release();
        }
    }
}
