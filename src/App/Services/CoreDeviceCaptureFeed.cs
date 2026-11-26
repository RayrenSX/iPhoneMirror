using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.IO;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

// Owns one native source and a current-user pipe. Dispose before destroying its handle.
internal sealed class CoreDeviceCaptureFeed : IAsyncDisposable
{
    internal const int VideoFramesBeforeBufferShrink = 30;
    private readonly NativeCore _core;
    private readonly NativeSessionHandle _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _reader;
    private readonly Func<string, Task>? _stopProducer;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OwnedBridgeProcess? _replay;
    private int _disposed;
    internal string PipeName { get; } = "iPhoneMirror.CoreDevice." + Guid.NewGuid().ToString("N");
    internal string? Error { get; private set; }
    internal ulong Frames { get; private set; }
    internal Task Completion => _reader;
    internal Task FirstFrame => _firstFrame.Task;
    internal CoreDeviceCaptureFeed(NativeCore core, NativeSessionHandle handle, Func<string, Task>? stopProducer)
    {
        _core = core; _handle = handle; _stopProducer = stopProducer;
        _pipe = new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1024 * 1024, 0);
        _reader = Task.Run(ReadAsync);
    }

    internal void StartReplay(string recording, double speed = 1)
    {
        if (!File.Exists(recording)) throw new FileNotFoundException("Choose an RTP recording.", recording);
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe"))
        { WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--replay-media", Path.GetFullPath(recording), "--media-pipe", PipeName,
            "--replay-speed", speed.ToString(System.Globalization.CultureInfo.InvariantCulture) }) info.ArgumentList.Add(arg);
        _replay = OwnedBridgeProcess.Start(info);
        // Drain bounded chunks; raw media and app identities never enter diagnostics.
        _ = DrainAsync(_replay.Output);
        _ = DrainAsync(_replay.Error);
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { var buffer = new char[4096]; while (await reader.ReadAsync(buffer) != 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadAsync()
    {
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(45));
            await _pipe.WaitForConnectionAsync(connect.Token);
            var header = new byte[28];
            byte[] videoBuffer = [], audioBuffer = [];
            var smallVideoFrames = 0;
            while (true)
            {
                await _pipe.ReadExactlyAsync(header, _stop.Token);
                var kind = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var w = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                var h = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
                if (header.AsSpan(0, 4).SequenceEqual("IMCD"u8) && kind == 3 && w == 0 && h == 0 && length == 0)
                { _core.SetExternalActive(_handle, false); continue; }
                var video = (kind == 1 || kind is >= 5 and <= 8) && w >= 2 && h >= 2 && w <= 8192 && h <= 8192 &&
                    length == w * (ulong)h + 2 * ((w + 1UL) / 2) * ((h + 1UL) / 2);
                var audio = kind == 2 && w == 48000 && h is 1 or 2 && length > 0 && length <= 192000 && length % (h * 2) == 0;
                if (!header.AsSpan(0, 4).SequenceEqual("IMCD"u8) || (!video && !audio) || length > 64 * 1024 * 1024)
                    throw new InvalidDataException("Invalid decoded media header.");
                var payloadLength = (int)length;
                var data = video ? videoBuffer : audioBuffer;
                if (video)
                {
                    // Wait for a sustained drop to avoid reallocating during size changes.
                    smallVideoFrames = payloadLength <= videoBuffer.Length / 2 ? smallVideoFrames + 1 : 0;
                    if (data.Length < payloadLength || smallVideoFrames == VideoFramesBeforeBufferShrink)
                    {
                        data = videoBuffer = GC.AllocateUninitializedArray<byte>(payloadLength);
                        smallVideoFrames = 0;
                    }
                }
                else if (data.Length < payloadLength)
                {
                    data = audioBuffer = GC.AllocateUninitializedArray<byte>(payloadLength);
                }
                await _pipe.ReadExactlyAsync(data.AsMemory(0, payloadLength), _stop.Token);
                _core.PushExternalFrame(_handle, kind, w, h, data, payloadLength);
                if (video)
                {
                    Frames++;
                    _firstFrame.TrySetResult();
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested) Error = error.GetType().Name;
        }
        finally
        {
            if (!_stop.IsCancellationRequested) _core.SetExternalActive(_handle, false);
            _firstFrame.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_stopProducer is not null) await _stopProducer(PipeName);
        }
        finally
        {
            // Keep draining until the producer has sent the device stop.
            // Closing the pipe earlier can trigger a competing recovery.
            _stop.Cancel(); _pipe.Dispose();
            try
            {
                if (_replay is not null) await _replay.RetireAsync(TimeSpan.Zero);
            }
            finally
            {
                try { await _reader; }
                finally { _replay?.Dispose(); _stop.Dispose(); }
            }
        }
    }
}
