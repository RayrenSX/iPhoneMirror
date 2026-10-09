using System.Collections.Concurrent;
using System.Text.Json;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Services;

public sealed partial class DirectUsbInputBridge
{
    internal bool SupportsAutomationClipboard { get; private set; }
    internal Func<IDisposable>? InputAdmission { get; set; }
    internal bool InputWritePending => _sendLock.CurrentCount == 0;
    private readonly SemaphoreSlim _clipboardRequestGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (long Generation, TaskCompletionSource<string?> Result)> _clipboardRequests = new();

    private void CompleteAutomationClipboard(JsonElement root)
    {
        if (!root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
            !_clipboardRequests.TryGetValue(id.GetString()!, out var pending)) return;
        if (!root.TryGetProperty("generation", out var gen) || gen.GetInt64() != pending.Generation) return;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
            pending.Result.TrySetResult(root.TryGetProperty("text", out var text) ? text.GetString() : null);
        else pending.Result.TrySetException(new AutomationException("CLIPBOARD_UNAVAILABLE", "The device clipboard operation failed or was cancelled.", 503));
    }

    internal async Task<string?> AutomationClipboardAsync(string kind, string? text, Func<bool> current,
        CancellationToken cancellation)
    {
        if (!SupportsAutomationClipboard)
            throw new AutomationException("CAPABILITY_NOT_SUPPORTED", "The installed bridge does not support clipboard requests.", 422);
        if (kind is not ("read_clipboard" or "write_clipboard" or "paste_text")) throw new ArgumentException(nameof(kind));
        if (!await _clipboardRequestGate.WaitAsync(0, cancellation))
            throw new AutomationException("CONTROL_LOCKED", "Another clipboard request is in progress.");
        var id = Guid.NewGuid().ToString("N");
        var generation = InputGeneration;
        var wireGeneration = Interlocked.Read(ref _wireGeneration);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clipboardRequests[id] = (wireGeneration, result);
        var sent = false;
        IDisposable? inputAdmission = null;
        try
        {
            inputAdmission = kind == "read_clipboard" ? null : InputAdmission?.Invoke();
            await _sendLock.WaitAsync(cancellation);
            try
            {
                if (!IsReady || generation != InputGeneration || !current()) throw new OperationCanceledException();
                cancellation.ThrowIfCancellationRequested();
                sent = true;
                await WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = CoreDeviceTouchProtocol.MessageSchema, kind, requestId = id, generation = wireGeneration,
                    seq = NextSequence(), text
                }), cancellation);
            }
            finally { _sendLock.Release(); }
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation);
        }
        catch (Exception) when (sent && !result.Task.IsCompleted)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _sendLock.WaitAsync(cleanup.Token);
                try
                {
                    if (IsReady && generation == InputGeneration)
                        await WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(new
                        { schema = CoreDeviceTouchProtocol.MessageSchema, kind = "cancel_clipboard", requestId = id, generation = wireGeneration, seq = NextSequence() }), cleanup.Token);
                }
                finally { _sendLock.Release(); }
                try { await result.Task.WaitAsync(cleanup.Token); }
                catch (AutomationException) { }
            }
            catch (Exception error) when (error is OperationCanceledException or System.IO.IOException or InvalidOperationException)
            {
                // A lost cancellation acknowledgement leaves the paste's effect
                // unknown. Retire this existing session before admitting another
                // owner; never let a delayed old paste overlap new input.
                if (generation == InputGeneration) await StopAsync();
                throw new AutomationException("CLIPBOARD_UNAVAILABLE", "Clipboard cleanup was not acknowledged; reconnect device control before retrying.", 503);
            }
            throw;
        }
        finally
        {
            inputAdmission?.Dispose();
            _clipboardRequests.TryRemove(id, out _);
            // Observe a simultaneous failure even when cancellation won the wait.
            if (result.Task.IsFaulted) _ = result.Task.Exception;
            _clipboardRequestGate.Release();
        }
    }
}
