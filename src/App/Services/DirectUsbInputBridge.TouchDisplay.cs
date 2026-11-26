using System.Text.Json;

namespace IPhoneMirror.App.Services;

public sealed partial class DirectUsbInputBridge
{
    private (uint Width, uint Height) _sentTouchDisplaySize;
    private long _sentTouchDisplayGeneration;
    internal bool SupportsTouchDisplayPreview { get; private set; }

    internal async Task UpdateTouchDisplayPreviewAsync(uint width, uint height, CancellationToken cancellation)
    {
        if (width is 0 or > 65535 || height is 0 or > 65535 || !SupportsTouchDisplayPreview || !IsReady) return;
        var generation = InputGeneration;
        var wireGeneration = Interlocked.Read(ref _wireGeneration);
        var size = (Width: Math.Min(width, height), Height: Math.Max(width, height));
        await _sendLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!IsReady || _stdin is null || generation != InputGeneration ||
                (_sentTouchDisplayGeneration == generation && _sentTouchDisplaySize == size)) return;

            await WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = CoreDeviceTouchProtocol.MessageSchema,
                kind = "touch_display_preview",
                generation = wireGeneration,
                width = size.Width,
                height = size.Height,
            }), cancellation).ConfigureAwait(false);
            _sentTouchDisplaySize = size;
            _sentTouchDisplayGeneration = generation;
        }
        finally { _sendLock.Release(); }
    }
}
