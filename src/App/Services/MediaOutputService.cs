using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

internal enum MediaOutputKind
{
    Recording,
    Rtmp,
    Srt,
    Whip,
}

internal sealed record MediaOutputRequest(
    MediaOutputKind Kind,
    string Destination,
    uint Width,
    uint Height,
    int FrameRate,
    int BitrateKbps,
    string Authorization = "");

internal sealed record MediaOutputCapabilities(
    bool FfmpegAvailable,
    bool HasH264Encoder,
    bool HasAacEncoder,
    bool HasOpusEncoder,
    bool HasRtmp,
    bool HasSrt,
    bool HasWhip,
    string PreferredH264Encoder,
    string FfmpegPath,
    string Detail)
{
    internal bool CanRecord => FfmpegAvailable && HasH264Encoder && HasAacEncoder;

    internal bool Supports(MediaOutputKind kind) => kind switch
    {
        MediaOutputKind.Recording => CanRecord,
        MediaOutputKind.Rtmp => CanRecord && HasRtmp,
        MediaOutputKind.Srt => CanRecord && HasSrt,
        MediaOutputKind.Whip => FfmpegAvailable && HasH264Encoder &&
            HasOpusEncoder && HasWhip,
        _ => false,
    };
}

internal sealed class MediaOutputService : IAsyncDisposable
{
    private static readonly TimeSpan StartupObservationWindow =
        TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RecordingFinalizationTimeout =
        TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StreamShutdownTimeout =
        TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AudioSilenceGrace =
        TimeSpan.FromMilliseconds(100);
    private const int MaximumAudioDrainPackets = 512;
    private readonly Func<ulong, uint, uint, VideoFrame?> _frameProvider;
    private readonly Func<ulong, ulong, AudioPacket?> _audioProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Process? _process;
    private Task? _runTask;
    private string _lastError = string.Empty;

    internal event Action<string, bool>? StatusChanged;
    internal bool IsRunning => _runTask is { IsCompleted: false };
    internal ulong SessionHandle { get; private set; }

    internal MediaOutputService(Func<ulong, uint, uint, VideoFrame?> frameProvider,
        Func<ulong, ulong, AudioPacket?> audioProvider)
    {
        _frameProvider = frameProvider;
        _audioProvider = audioProvider;
    }

    internal static async Task<MediaOutputCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var path = FindFfmpeg();
        if (path is null)
            return new(false, false, false, false, false, false, false, string.Empty,
                string.Empty,
                "FFmpeg was not found. Install FFmpeg 8 or place it in the application directory.");

        try
        {
            var encoders = await RunProbeAsync(path, ["-hide_banner", "-encoders"], cancellationToken);
            var protocols = await RunProbeAsync(path, ["-hide_banner", "-protocols"], cancellationToken);
            var muxers = await RunProbeAsync(path, ["-hide_banner", "-muxers"], cancellationToken);
            return CreateCapabilities(path, encoders, protocols, muxers);
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("media_output", "capability_probe_failed", error,
                ("file", Path.GetFileName(path)));
            return new(false, false, false, false, false, false, false,
                string.Empty, path, error.Message);
        }
    }

    internal async Task StartAsync(ulong sessionHandle, MediaOutputRequest request,
        MediaOutputCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sessionHandle);
        Validate(request, capabilities);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) throw new InvalidOperationException("A media output is already active.");
            _lastError = string.Empty;
            var firstAudio = await TryWaitForAudioAsync(sessionHandle, cancellationToken);
            string? recordingStagingPath = null;
            var processRequest = request;
            if (request.Kind == MediaOutputKind.Recording)
            {
                recordingStagingPath = PendingRecordingStore.CreateStagingPath(
                    request.Destination);
                TryDeleteFile(recordingStagingPath);
                processRequest = request with { Destination = recordingStagingPath };
            }
            var audioPipeName = firstAudio is null ? null :
                $"iphoneMirror-audio-{Environment.ProcessId}-{Guid.NewGuid():N}";
            NamedPipeServerStream? audioPipe = audioPipeName is null ? null : new(
                audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
            Process? process = CreateProcess(capabilities.FfmpegPath,
                BuildArguments(processRequest, capabilities,
                    audioPipeName is null ? null : $@"\\.\pipe\{audioPipeName}",
                    firstAudio?.SampleRate ?? 48000,
                    firstAudio?.Channels ?? 2,
                    includeAudio: firstAudio is not null));
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data)) _lastError = args.Data;
            };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("FFmpeg could not be started.");
                process.BeginErrorReadLine();
                using var pipeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                pipeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var pipeConnection = audioPipe?.WaitForConnectionAsync(pipeTimeout.Token);
                await ObserveStartupAsync(process, cancellationToken);
                if (pipeConnection is not null)
                {
                    try { await pipeConnection; }
                    catch (OperationCanceledException)
                        when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            "FFmpeg did not connect to the projection audio input within 5 seconds.");
                    }
                }

                var runCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _process = process;
                SessionHandle = sessionHandle;
                _runCancellation = runCancellation;
                _runTask = PumpAsync(process, audioPipe, sessionHandle, request,
                    firstAudio, recordingStagingPath, runCancellation.Token);
                process = null;
                audioPipe = null;
                recordingStagingPath = null;
                StatusChanged?.Invoke(request.Kind == MediaOutputKind.Recording
                    ? "Recording" : "Live", false);
            }
            catch (Exception error)
            {
                DiagnosticLogger.Exception("media_output", "start_failed", error,
                    ("kind", request.Kind));
                if (process is not null) await DisposeFailedStartAsync(process);
                audioPipe?.Dispose();
                if (recordingStagingPath is not null)
                    TryDeleteFile(recordingStagingPath);
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    internal async Task StopAsync()
    {
        Task? task;
        await _lifecycleGate.WaitAsync();
        try
        {
            _runCancellation?.Cancel();
            task = _runTask;
        }
        finally { _lifecycleGate.Release(); }
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task PumpAsync(Process process, NamedPipeServerStream? audioPipe,
        ulong sessionHandle, MediaOutputRequest request, AudioPacket? firstAudio,
        string? recordingStagingPath, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        Task? videoTask = null;
        Task? audioTask = null;
        using var pumpCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            videoTask = PumpVideoAsync(process, sessionHandle, request,
                pumpCancellation.Token);
            audioTask = audioPipe is not null && firstAudio is not null
                ? PumpAudioAsync(process, audioPipe, sessionHandle,
                    firstAudio, pumpCancellation.Token)
                : Task.Delay(Timeout.InfiniteTimeSpan, pumpCancellation.Token);
            var completed = await Task.WhenAny(videoTask, audioTask);
            await completed;
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
            pumpCancellation.Cancel();
            try { process.StandardInput.Close(); }
            catch (Exception error)
            {
                DiagnosticLogger.ExceptionOnce("media-stdin-close", "media_output",
                    "stdin_close_failed", error);
            }
            try { audioPipe?.Dispose(); }
            catch (Exception error)
            {
                DiagnosticLogger.ExceptionOnce("media-audio-pipe-dispose", "media_output",
                    "audio_pipe_dispose_failed", error);
            }
            try
            {
                await Task.WhenAll(videoTask ?? Task.CompletedTask,
                    audioTask ?? Task.CompletedTask);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { failure ??= error; }
            var shutdownTimeout = request.Kind == MediaOutputKind.Recording
                ? RecordingFinalizationTimeout : StreamShutdownTimeout;
            try
            {
                using var timeout = new CancellationTokenSource(shutdownTimeout);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                failure ??= new TimeoutException(request.Kind == MediaOutputKind.Recording
                    ? "FFmpeg did not finalize the recording within 2 minutes."
                    : "FFmpeg did not stop the live output within 15 seconds.");
                await KillProcessAsync(process);
            }
            catch (Exception error)
            {
                failure ??= error;
                await KillProcessAsync(process);
            }
            if (TryGetExitCode(process, out var exitCode) && exitCode != 0)
            {
                failure ??= new InvalidOperationException(
                    string.IsNullOrWhiteSpace(_lastError)
                        ? $"FFmpeg exited with code {exitCode} while finalizing output."
                        : _lastError);
            }
            if (recordingStagingPath is not null)
            {
                if (failure is null)
                {
                    try
                    {
                        File.Move(recordingStagingPath, request.Destination,
                            overwrite: false);
                    }
                    catch (Exception error)
                    {
                        failure = new IOException(
                            "The finalized recording could not be made available for saving.",
                            error);
                    }
                }
                if (failure is not null) TryDeleteFile(recordingStagingPath);
            }
            process.Dispose();
            await _lifecycleGate.WaitAsync();
            try
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _runTask = null;
                    _runCancellation?.Dispose();
                    _runCancellation = null;
                    SessionHandle = 0;
                }
            }
            finally { _lifecycleGate.Release(); }
            if (failure is not null)
                DiagnosticLogger.Exception("media_output", "session_failed", failure,
                    ("kind", request.Kind), ("handle", AppLog.Handle(sessionHandle)));
            StatusChanged?.Invoke(failure?.Message ?? "Stopped", failure is not null);
        }
    }

    private async Task PumpVideoAsync(Process process, ulong sessionHandle,
        MediaOutputRequest request, CancellationToken cancellationToken)
    {
        var frameInterval = TimeSpan.FromSeconds(1.0 / request.FrameRate);
        var frameCanvas = new byte[checked((int)request.Width * (int)request.Height * 4)];
        var staleSince = Stopwatch.StartNew();
        long lastTimestamp = long.MinValue;
        using var timer = new PeriodicTimer(frameInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (process.HasExited)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(_lastError)
                    ? $"FFmpeg exited with code {process.ExitCode}." : _lastError);
            var frame = _frameProvider(sessionHandle, request.Width, request.Height);
            if (frame is null)
            {
                if (staleSince.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("No projection frame was received for 5 seconds.");
                continue;
            }
            if (frame.Timestamp100Ns != lastTimestamp)
            {
                lastTimestamp = frame.Timestamp100Ns;
                staleSince.Restart();
            }
            else if (staleSince.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The projection session stopped producing frames.");
            }
            await WriteFrameAsync(process.StandardInput.BaseStream, frame,
                request.Width, request.Height, frameCanvas, cancellationToken);
        }
    }

    private async Task PumpAudioAsync(Process process, Stream output,
        ulong sessionHandle, AudioPacket firstAudio,
        CancellationToken cancellationToken)
    {
        var blockAlign = checked(firstAudio.Channels * sizeof(short));
        var bytesPerSecond = checked((long)firstAudio.SampleRate * blockAlign);
        if (firstAudio.Pcm.Length == 0 || firstAudio.Pcm.Length % blockAlign != 0)
            throw new InvalidDataException(
                "The projection audio packet has an invalid PCM layout.");
        var sequence = firstAudio.Sequence;
        await output.WriteAsync(firstAudio.Pcm, cancellationToken);
        var audioClock = Stopwatch.StartNew();
        var lastRealPacket = Stopwatch.StartNew();
        long emittedBytes = firstAudio.Pcm.Length;
        var silenceChunkBytes = checked((int)Math.Max(blockAlign,
            bytesPerSecond / 50 / blockAlign * blockAlign));
        var silence = new byte[silenceChunkBytes];
        var insertedSilence = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(_lastError)
                    ? $"FFmpeg exited with code {process.ExitCode}." : _lastError);
            var packet = insertedSilence
                ? ReadNewestAvailableAudioPacket(
                    cursor => _audioProvider(sessionHandle, cursor), sequence)
                : _audioProvider(sessionHandle, sequence);
            if (packet is null)
            {
                if (lastRealPacket.Elapsed >= AudioSilenceGrace)
                {
                    var targetBytes = checked(firstAudio.Pcm.Length +
                        (long)(audioClock.Elapsed.TotalSeconds * bytesPerSecond));
                    var missingBytes = targetBytes - emittedBytes;
                    var bytesToWrite = checked((int)Math.Min(
                        Math.Max(0, missingBytes), silence.Length));
                    bytesToWrite -= bytesToWrite % blockAlign;
                    if (bytesToWrite > 0)
                    {
                        await output.WriteAsync(
                            silence.AsMemory(0, bytesToWrite), cancellationToken);
                        emittedBytes = checked(emittedBytes + bytesToWrite);
                        insertedSilence = true;
                        continue;
                    }
                }
                await Task.Delay(5, cancellationToken);
                continue;
            }
            if (packet.SampleRate != firstAudio.SampleRate ||
                packet.Channels != firstAudio.Channels ||
                packet.BitsPerSample != 16 || packet.Pcm.Length == 0 ||
                packet.Pcm.Length % blockAlign != 0)
                throw new InvalidDataException(
                    "The projection audio format changed during output.");
            if (packet.Sequence <= sequence)
                throw new InvalidDataException(
                    "The projection audio sequence did not advance.");
            await output.WriteAsync(packet.Pcm, cancellationToken);
            emittedBytes = checked(emittedBytes + packet.Pcm.Length);
            sequence = packet.Sequence;
            lastRealPacket.Restart();
            insertedSilence = false;
        }
    }

    private async Task<AudioPacket?> TryWaitForAudioAsync(ulong sessionHandle,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(250))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packet = ReadNewestAvailableAudioPacket(
                cursor => _audioProvider(sessionHandle, cursor), 0);
            if (packet is { SampleRate: >= 8000 and <= 192000,
                    Channels: >= 1 and <= 8, BitsPerSample: 16 } &&
                packet.Pcm.Length != 0 &&
                packet.Pcm.Length % (packet.Channels * sizeof(short)) == 0)
                return packet;
            await Task.Delay(20, cancellationToken);
        }
        return null;
    }

    internal static AudioPacket? ReadNewestAvailableAudioPacket(
        Func<ulong, AudioPacket?> provider, ulong afterSequence)
    {
        AudioPacket? newest = null;
        var cursor = afterSequence;
        for (var index = 0; index < MaximumAudioDrainPackets; ++index)
        {
            var packet = provider(cursor);
            if (packet is null) break;
            if (packet.Sequence <= cursor)
                throw new InvalidDataException(
                    "The projection audio sequence did not advance.");
            newest = packet;
            cursor = packet.Sequence;
        }
        return newest;
    }

    internal static async Task WriteFrameAsync(Stream output, VideoFrame frame,
        uint width, uint height, byte[] canvas, CancellationToken cancellationToken)
    {
        var targetRowBytes = checked((int)width * 4);
        var targetRows = checked((int)height);
        var targetBytes = checked(targetRowBytes * targetRows);
        var frameRowBytes = checked((int)frame.Width * 4);
        var frameRows = checked((int)frame.Height);
        if (frame.Width == 0 || frame.Height == 0 ||
            frame.Width > width || frame.Height > height ||
            frame.Stride < frameRowBytes ||
            frame.Pixels.Length < checked((int)frame.Stride * frameRows) ||
            canvas.Length < targetBytes)
            throw new InvalidDataException("The native output frame has an invalid layout.");
        if (frame.Width == width && frame.Height == height &&
            frame.Stride == targetRowBytes)
        {
            await output.WriteAsync(frame.Pixels.AsMemory(0, targetBytes), cancellationToken);
            return;
        }

        Array.Clear(canvas, 0, targetBytes);
        var leftBytes = checked(((int)width - (int)frame.Width) / 2 * 4);
        var top = ((int)height - frameRows) / 2;
        for (var row = 0; row < frameRows; ++row)
        {
            var sourceOffset = checked((int)frame.Stride * row);
            var destinationOffset = checked((top + row) * targetRowBytes + leftBytes);
            frame.Pixels.AsSpan(sourceOffset, frameRowBytes)
                .CopyTo(canvas.AsSpan(destinationOffset, frameRowBytes));
        }
        await output.WriteAsync(canvas.AsMemory(0, targetBytes), cancellationToken);
    }

    private static Process CreateProcess(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start, EnableRaisingEvents = true };
    }

    internal static IReadOnlyList<string> BuildArguments(MediaOutputRequest request,
        MediaOutputCapabilities capabilities,
        string? audioPipePath = @"\\.\pipe\iphoneMirror-audio-test",
        uint audioSampleRate = 48000, ushort audioChannels = 2,
        bool includeAudio = true)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin",
        };
        if (includeAudio)
        {
            if (string.IsNullOrWhiteSpace(audioPipePath))
                throw new ArgumentException("An audio pipe is required when audio is enabled.",
                    nameof(audioPipePath));
            args.AddRange([
                "-thread_queue_size", "512", "-f", "s16le",
                "-ar", audioSampleRate.ToString(), "-ac", audioChannels.ToString(),
                "-i", audioPipePath,
            ]);
        }
        args.AddRange([
            "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{request.Width}x{request.Height}",
            "-framerate", request.FrameRate.ToString(), "-i", "pipe:0",
        ]);
        if (includeAudio)
        {
            // FFmpeg opens inputs sequentially. Opening the named audio pipe
            // first lets StartAsync complete its handshake before video data
            // is pumped into stdin.
            args.AddRange(["-map", "1:v:0", "-map", "0:a:0"]);
        }
        else
        {
            args.AddRange(["-map", "0:v:0", "-an"]);
        }
        args.AddRange(["-c:v", capabilities.PreferredH264Encoder]);
        if (string.Equals(capabilities.PreferredH264Encoder, "libx264",
                StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-preset", "veryfast", "-tune", "zerolatency"]);
        args.AddRange([
            "-pix_fmt", "yuv420p", "-g", (request.FrameRate * 2).ToString(),
            "-b:v", $"{request.BitrateKbps}k", "-maxrate", $"{request.BitrateKbps}k",
            "-bufsize", $"{request.BitrateKbps * 2}k",
        ]);
        if (includeAudio)
            args.AddRange(request.Kind == MediaOutputKind.Whip
                ? ["-c:a", "libopus", "-ac", "2", "-b:a", "128k"]
                : ["-c:a", "aac", "-b:a", "192k"]);
        switch (request.Kind)
        {
            case MediaOutputKind.Recording:
                args.AddRange(["-movflags", "+faststart", "-y", request.Destination]);
                break;
            case MediaOutputKind.Rtmp:
                args.AddRange(["-f", "flv", request.Destination]);
                break;
            case MediaOutputKind.Srt:
                args.AddRange(["-f", "mpegts", request.Destination]);
                break;
            case MediaOutputKind.Whip:
                var token = NormalizeWhipToken(request.Authorization);
                if (!string.IsNullOrEmpty(token))
                    args.AddRange(["-authorization", token]);
                args.AddRange(["-f", "whip", request.Destination]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request),
                    "Unsupported media output kind.");
        }
        return args;
    }

    internal static string SelectPreferredH264Encoder(string encoders)
    {
        if (HasToken(encoders, "libx264"))
            return "libx264";
        return HasToken(encoders, "h264_mf")
            ? "h264_mf" : string.Empty;
    }

    internal static MediaOutputCapabilities CreateCapabilities(string path,
        string encoders, string protocols, string muxers)
    {
        var preferredEncoder = SelectPreferredH264Encoder(encoders);
        var h264 = !string.IsNullOrWhiteSpace(preferredEncoder);
        var aac = HasToken(encoders, "aac");
        var opus = HasToken(encoders, "libopus");
        var rtmp = HasToken(protocols, "rtmp") && HasToken(muxers, "flv");
        var srt = HasToken(protocols, "srt") && HasToken(muxers, "mpegts");
        var whip = HasToken(muxers, "whip");
        return new(true, h264, aac, opus, rtmp, srt, whip, preferredEncoder, path,
            $"{Path.GetFileName(path)} / {preferredEncoder}");
    }

    private static void Validate(MediaOutputRequest request, MediaOutputCapabilities capabilities)
    {
        if (!capabilities.FfmpegAvailable || !capabilities.HasH264Encoder)
            throw new InvalidOperationException("A compatible FFmpeg H.264 encoder is unavailable.");
        if (!capabilities.Supports(request.Kind))
            throw new InvalidOperationException("The requested FFmpeg output protocol is unavailable.");
        if (request.Width is < 160 or > 3840 || request.Height is < 160 or > 2160 ||
            (request.Width & 1) != 0 || (request.Height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Output dimensions must be even and at most 3840x2160.");
        if (request.FrameRate is < 10 or > 60 || request.BitrateKbps is < 500 or > 50000)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Destination))
            throw new ArgumentException("An output destination is required.", nameof(request));
        if (request.Kind == MediaOutputKind.Rtmp && (!capabilities.HasRtmp ||
            !HasScheme(request.Destination, "rtmp", "rtmps")))
            throw new ArgumentException("Enter an rtmp:// or rtmps:// address.");
        if (request.Kind == MediaOutputKind.Srt && (!capabilities.HasSrt ||
            !HasScheme(request.Destination, "srt")))
            throw new ArgumentException("Enter an srt:// address.");
        if (request.Kind == MediaOutputKind.Whip && (!capabilities.HasWhip ||
            !HasScheme(request.Destination, "http", "https")))
            throw new ArgumentException("Enter an HTTP(S) WHIP endpoint.");
    }

    private static bool HasScheme(string value, params string[] schemes) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    internal static string NormalizeWhipToken(string value)
    {
        var token = value.Trim();
        const string prefix = "Bearer";
        if (token.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (token.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            token = token[(prefix.Length + 1)..].Trim();
        return token;
    }

    private static bool HasToken(string text, string token) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Contains(token, StringComparer.OrdinalIgnoreCase);

    private async Task ObserveStartupAsync(Process process,
        CancellationToken cancellationToken)
    {
        using var startupCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(StartupObservationWindow);
        try
        {
            await process.WaitForExitAsync(startupCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        process.WaitForExit();
        await Task.Yield();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(_lastError)
            ? $"FFmpeg exited during startup with code {process.ExitCode}."
            : _lastError);
    }

    private static async Task DisposeFailedStartAsync(Process process)
    {
        try
        {
            try { process.StandardInput.Close(); }
            catch (Exception error)
            {
                DiagnosticLogger.ExceptionOnce("media-failed-stdin-close", "media_output",
                    "failed_start_stdin_close_failed", error);
            }
            await KillProcessAsync(process);
        }
        finally { process.Dispose(); }
    }

    private static async Task KillProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            // Cleanup must not replace the original media-output failure.
            DiagnosticLogger.Exception("media_output", "process_kill_failed", error);
        }
    }

    private static bool TryGetExitCode(Process process, out int exitCode)
    {
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
                return true;
            }
        }
        catch (Exception error)
        {
            // The caller already retains any shutdown or pump failure.
            DiagnosticLogger.ExceptionOnce("media-exit-code", "media_output",
                "exit_code_read_failed", error);
        }
        exitCode = 0;
        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException)
        {
            DiagnosticLogger.Exception("media_output", "temporary_file_delete_failed",
                error, ("file", Path.GetFileName(path)));
        }
    }

    private static string? FindFfmpeg()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
        };
        var bundled = candidates.FirstOrDefault(File.Exists);
        if (bundled is not null) return bundled;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "where.exe", Arguments = "ffmpeg.exe",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(start);
            var line = process?.StandardOutput.ReadLine();
            process?.WaitForExit(2000);
            return !string.IsNullOrWhiteSpace(line) && File.Exists(line) ? line : null;
        }
        catch (Exception error)
        {
            DiagnosticLogger.ExceptionOnce("ffmpeg-discovery", "media_output",
                "ffmpeg_discovery_failed", error);
            return null;
        }
    }

    private static async Task<string> RunProbeAsync(string path,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = path, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout + await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(output.Trim());
        return output;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }
}
