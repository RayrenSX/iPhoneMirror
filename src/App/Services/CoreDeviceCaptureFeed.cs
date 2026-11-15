using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.IO;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

// Owns one native source and a current-user pipe. Dispose before destroying its handle.
internal sealed class CoreDeviceCaptureFeed : IAsyncDisposable
{
    private readonly NativeCore _core;
    private readonly NativeSessionHandle _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _reader;
    private readonly Func<Task>? _stopProducer;
    private OwnedBridgeProcess? _replay;
    private int _disposed;
    internal string PipeName { get; } = "iPhoneMirror.CoreDevice." + Guid.NewGuid().ToString("N");
    internal string? Error { get; private set; }
    internal ulong Frames { get; private set; }
    internal CoreDeviceCaptureFeed(NativeCore core, NativeSessionHandle handle, Func<Task>? stopProducer)
    {
        _core = core; _handle = handle; _stopProducer = stopProducer;
        _pipe = new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1024 * 1024, 0);
        _reader = ReadAsync();
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
            while (true)
            {
                await _pipe.ReadExactlyAsync(header, _stop.Token);
                var kind = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var w = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                var h = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
                if (header.AsSpan(0, 4).SequenceEqual("IMCD"u8) && kind == 3 && w == 0 && h == 0 && length == 0)
                { _core.SetExternalActive(_handle, false); continue; }
                var video = kind == 1 && w >= 2 && h >= 2 && w <= 8192 && h <= 8192 &&
                    length == w * (ulong)h + 2 * ((w + 1UL) / 2) * ((h + 1UL) / 2);
                var audio = kind == 2 && w == 48000 && h is 1 or 2 && length > 0 && length <= 192000 && length % (h * 2) == 0;
                if (!header.AsSpan(0, 4).SequenceEqual("IMCD"u8) || (!video && !audio) || length > 64 * 1024 * 1024)
                    throw new InvalidDataException("Invalid decoded media header.");
                var data = new byte[length];
                await _pipe.ReadExactlyAsync(data, _stop.Token);
                _core.PushExternalFrame(_handle, kind, w, h, data);
                if (video) Frames++;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested) Error = error.GetType().Name;
        }
        finally
        {
            if (!_stop.IsCancellationRequested) _core.SetExternalActive(_handle, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _pipe.Dispose();
        try
        {
            if (_replay is not null) await _replay.RetireAsync(TimeSpan.Zero);
            if (_stopProducer is not null) await _stopProducer();
        }
        finally
        {
            try { await _reader; }
            finally { _replay?.Dispose(); _stop.Dispose(); }
        }
    }
}
