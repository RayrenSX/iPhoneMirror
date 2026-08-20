using System.Diagnostics;
using System.Text;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

// Decodes the audio track from the cast source itself. This follows the same
// HLS/HTTP media source as the video bridge and deliberately does not capture
// the Windows output device, so unrelated system sounds never enter a file or
// stream.
internal sealed class MediaCastAudioDecoder : IDisposable
{
    private const int SampleRate = 48000;
    private const ushort Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(short);
    private const int PacketDurationMilliseconds = 100;
    private const int PacketBytes = SampleRate * BytesPerFrame /
        (1000 / PacketDurationMilliseconds);
    private const int MaximumQueuedPackets = 100;
    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _cancellation;
    private Task? _readTask;
    private readonly Queue<AudioPacket> _packets = new();
    private ulong _sequence;
    private int _diagnosticOutputReported;

    internal bool IsRunning => _readTask is { IsCompleted: false };

    internal void Start(Uri source, double startPosition, double playbackRate,
        Action<string>? diagnostic = null)
    {
        Stop();
        Interlocked.Exchange(ref _diagnosticOutputReported, 0);
        var ffmpeg = HlsMediaPlaybackBridge.FindBundledFfmpeg();
        if (ffmpeg is null)
        {
            diagnostic?.Invoke("media_audio_unavailable reason=ffmpeg_missing");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in BuildArguments(source, startPosition,
                     playbackRate))
            startInfo.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException("FFmpeg audio decoder could not start.");
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data) &&
                    Interlocked.Exchange(ref _diagnosticOutputReported, 1) == 0)
                    diagnostic?.Invoke("media_audio_ffmpeg_diagnostics_active");
            };
            process.BeginErrorReadLine();
            var cancellation = new CancellationTokenSource();
            lock (_gate)
            {
                _process = process;
                _cancellation = cancellation;
                _readTask = Task.Run(() => ReadLoopAsync(process,
                    cancellation.Token, diagnostic), CancellationToken.None);
                process = null;
            }
            diagnostic?.Invoke("media_audio_started");
        }
        catch (Exception error)
        {
            diagnostic?.Invoke($"media_audio_start_failed error={error.GetType().Name}");
            try { process?.Kill(entireProcessTree: true); }
            catch { }
            process?.Dispose();
        }
    }

    internal static IReadOnlyList<string> BuildArguments(Uri source,
        double startPosition = 0, double playbackRate = 1)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "warning",
            "-reconnect", "1", "-reconnect_streamed", "1",
            "-reconnect_delay_max", "5",
            "-protocol_whitelist", "http,https,tcp,tls,crypto",
        };
        if (double.IsFinite(startPosition) && startPosition > 0.05)
        {
            args.Add("-ss");
            args.Add(startPosition.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }
        playbackRate = double.IsFinite(playbackRate)
            ? Math.Clamp(playbackRate, 0.5, 2.0) : 1.0;
        args.Add("-readrate");
        args.Add(playbackRate.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        args.AddRange([
            "-i", source.AbsoluteUri,
            "-map", "0:a:0?", "-vn", "-sn", "-dn",
        ]);
        if (Math.Abs(playbackRate - 1.0) > 0.001)
        {
            args.Add("-af");
            args.Add($"atempo={playbackRate.ToString(
                System.Globalization.CultureInfo.InvariantCulture)}");
        }
        args.AddRange([
            "-c:a", "pcm_s16le", "-ar", SampleRate.ToString(),
            "-ac", Channels.ToString(), "-f", "s16le", "pipe:1",
        ]);
        return args;
    }

    internal AudioPacket? GetPacket(ulong afterSequence)
    {
        lock (_gate)
        {
            while (_packets.Count > 0 &&
                   _packets.Peek().Sequence <= afterSequence)
                _packets.Dequeue();
            return _packets.Count == 0 ? null : _packets.Peek();
        }
    }

    private async Task ReadLoopAsync(Process process,
        CancellationToken cancellationToken, Action<string>? diagnostic)
    {
        var packetBuffer = new byte[PacketBytes];
        var filled = 0;
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(
                    packetBuffer.AsMemory(filled), cancellationToken);
                if (read == 0) break;
                filled += read;
                if (filled != packetBuffer.Length) continue;
                var packet = new AudioPacket(++_sequence, SampleRate, Channels,
                    16, packetBuffer.ToArray());
                lock (_gate)
                {
                    _packets.Enqueue(packet);
                    while (_packets.Count > MaximumQueuedPackets)
                        _packets.Dequeue();
                }
                filled = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            if (failure is not null)
                diagnostic?.Invoke($"media_audio_read_failed error={failure.GetType().Name}");
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _readTask = null;
                    _cancellation?.Dispose();
                    _cancellation = null;
                }
            }
            try { process.Dispose(); }
            catch { }
        }
    }

    internal void Stop()
    {
        Process? process;
        CancellationTokenSource? cancellation;
        Task? readTask;
        lock (_gate)
        {
            process = _process;
            cancellation = _cancellation;
            readTask = _readTask;
            _process = null;
            _cancellation = null;
            _readTask = null;
            _packets.Clear();
        }
        cancellation?.Cancel();
        try
        {
            if (process is not null && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { readTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        cancellation?.Dispose();
        try { process?.Dispose(); }
        catch { }
    }

    public void Dispose() => Stop();
}
