using System.Diagnostics;
using System.Buffers.Binary;
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
    internal IReadOnlyList<string> H264EncoderCandidates { get; init; } = [];

    // Audio is optional: the capture pipeline can explicitly pass -an when
    // no PCM packet is available. Keep video-only output available with a
    // minimal FFmpeg build that has H.264 but no AAC/Opus encoder.
    internal bool CanRecord => FfmpegAvailable && HasH264Encoder;

    internal bool Supports(MediaOutputKind kind) => kind switch
    {
        MediaOutputKind.Recording => CanRecord,
        MediaOutputKind.Rtmp => CanRecord && HasRtmp,
        MediaOutputKind.Srt => CanRecord && HasSrt,
        MediaOutputKind.Whip => FfmpegAvailable && HasH264Encoder && HasWhip,
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
    private const uint DefaultAudioSampleRate = 48000;
    private const ushort DefaultAudioChannels = 2;
    private const int MaximumAudioDrainPackets = 512;
    private const int MaximumVideoCatchUpSeconds = 2;
    private readonly Func<ulong, uint, uint, Nv12VideoFrame?> _frameProvider;
    private readonly Func<ulong, ulong, AudioPacket?> _audioProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Process? _process;
    private Task? _runTask;
    private string _lastError = string.Empty;

    internal event Action<string, bool>? StatusChanged;
    internal bool IsRunning => _runTask is { IsCompleted: false };
    internal ulong SessionHandle { get; private set; }

    internal MediaOutputService(Func<ulong, uint, uint, Nv12VideoFrame?> frameProvider,
        Func<ulong, ulong, AudioPacket?> audioProvider)
    {
        _frameProvider = frameProvider;
        _audioProvider = audioProvider;
    }

    internal static async Task<MediaOutputCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = await FindFfmpegCandidatesAsync(cancellationToken);
        if (candidates.Count == 0)
            return MissingFfmpegCapabilities();

        Exception? lastError = null;
        MediaOutputCapabilities? best = null;
        foreach (var path in candidates)
        {
            try
            {
                var encoders = await RunProbeAsync(path,
                    ["-hide_banner", "-encoders"], cancellationToken);
                var protocols = await RunProbeAsync(path,
                    ["-hide_banner", "-protocols"], cancellationToken);
                var muxers = await RunProbeAsync(path,
                    ["-hide_banner", "-muxers"], cancellationToken);
                var capabilities = CreateCapabilities(path, encoders, protocols,
                    muxers);
                capabilities = await ResolveWorkingEncoderAsync(capabilities,
                    640, 360, cancellationToken);
                best = SelectBestCapabilities([best, capabilities]);
                if (capabilities.HasH264Encoder && CapabilityScore(capabilities) == 34 &&
                    EncoderPreferenceScore(capabilities.PreferredH264Encoder) == 5)
                    break;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                lastError = error;
                DiagnosticLogger.Exception("media_output", "capability_probe_failed",
                    error, ("file", Path.GetFileName(path)));
            }
        }

        if (best is not null) return best;

        return new(false, false, false, false, false, false, false, string.Empty,
            candidates[0], lastError?.Message ?? "FFmpeg capability probing failed.");
    }

    private static MediaOutputCapabilities MissingFfmpegCapabilities() =>
        new(false, false, false, false, false, false, false, string.Empty,
            string.Empty,
            "FFmpeg was not found. Install FFmpeg 8 or place it in the application directory.");

    internal static int CapabilityScore(MediaOutputCapabilities capabilities) =>
        (capabilities.HasH264Encoder ? 16 : 0) +
        (capabilities.HasAacEncoder ? 8 : 0) +
        (capabilities.HasOpusEncoder ? 4 : 0) +
        (capabilities.HasRtmp ? 2 : 0) +
        (capabilities.HasSrt ? 2 : 0) +
        (capabilities.HasWhip ? 2 : 0);

    internal static MediaOutputCapabilities? SelectBestCapabilities(
        IEnumerable<MediaOutputCapabilities?> candidates)
    {
        MediaOutputCapabilities? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate is null) continue;
            if (best is null || IsBetterCandidate(candidate, best))
                best = candidate;
        }
        return best;
    }

    private static bool IsBetterCandidate(MediaOutputCapabilities candidate,
        MediaOutputCapabilities current)
    {
        // H.264 is the hard requirement for every output mode. A build with
        // many protocols but no H.264 must never displace a usable encoder.
        if (candidate.HasH264Encoder != current.HasH264Encoder)
            return candidate.HasH264Encoder;
        if (candidate.FfmpegAvailable != current.FfmpegAvailable)
            return candidate.FfmpegAvailable;
        var candidateScore = CapabilityScore(candidate);
        var currentScore = CapabilityScore(current);
        if (candidateScore != currentScore)
            return candidateScore > currentScore;
        return EncoderPreferenceScore(candidate.PreferredH264Encoder) >
            EncoderPreferenceScore(current.PreferredH264Encoder);
    }

    private static int EncoderPreferenceScore(string encoder)
    {
        if (encoder.Equals("h264_nvenc", StringComparison.OrdinalIgnoreCase)) return 5;
        if (encoder.Equals("h264_amf", StringComparison.OrdinalIgnoreCase)) return 4;
        if (encoder.Equals("h264_qsv", StringComparison.OrdinalIgnoreCase)) return 3;
        if (encoder.Equals("h264_mf", StringComparison.OrdinalIgnoreCase)) return 2;
        if (encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
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
            capabilities = await ResolveWorkingEncoderAsync(capabilities,
                request.Width, request.Height, cancellationToken);
            if (!capabilities.HasH264Encoder)
                throw new InvalidOperationException(
                    "No installed FFmpeg H.264 encoder could encode at the requested size.");
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
            var includeAudio = request.Kind == MediaOutputKind.Whip
                ? capabilities.HasOpusEncoder
                : capabilities.HasAacEncoder;
            var audioSampleRate = firstAudio?.SampleRate ?? DefaultAudioSampleRate;
            var audioChannels = firstAudio?.Channels ?? DefaultAudioChannels;
            var audioPipeName = includeAudio
                ? $"iphoneMirror-audio-{Environment.ProcessId}-{Guid.NewGuid():N}"
                : null;
            NamedPipeServerStream? audioPipe = null;
            Process? process = null;
            try
            {
                audioPipe = audioPipeName is null ? null : new NamedPipeServerStream(
                    audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
                process = CreateProcess(capabilities.FfmpegPath,
                    BuildArguments(processRequest, capabilities,
                        audioPipeName is null ? null : $@"\\.\pipe\{audioPipeName}",
                        audioSampleRate,
                        audioChannels,
                        includeAudio: includeAudio));
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data)) _lastError = args.Data;
                };
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
                    var processExit = process.WaitForExitAsync(cancellationToken);
                    var completed = await Task.WhenAny(pipeConnection, processExit);
                    if (completed == processExit)
                    {
                        await processExit;
                        await ThrowStartupExitAsync(process, cancellationToken);
                    }

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
                var activeProcess = process;
                var activeAudioPipe = audioPipe;
                var activeRecordingStagingPath = recordingStagingPath;
                _process = activeProcess;
                SessionHandle = sessionHandle;
                _runCancellation = runCancellation;
                _runTask = Task.Run(() => PumpAsync(activeProcess,
                    activeAudioPipe, sessionHandle, request, firstAudio,
                    audioSampleRate, audioChannels, activeRecordingStagingPath,
                    runCancellation.Token),
                    CancellationToken.None);
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
                throw;
            }
            finally
            {
                if (process is not null) await DisposeFailedStartAsync(process);
                audioPipe?.Dispose();
                if (recordingStagingPath is not null)
                    TryDeleteFile(recordingStagingPath);
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
        uint audioSampleRate, ushort audioChannels, string? recordingStagingPath,
        CancellationToken cancellationToken)
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
            audioTask = audioPipe is not null
                ? PumpAudioAsync(process, audioPipe, sessionHandle,
                    firstAudio, audioSampleRate, audioChannels,
                    pumpCancellation.Token)
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
        var firstFrameWait = Stopwatch.StartNew();
        var outputClock = Stopwatch.StartNew();
        long framesWritten = 0;
        ReadOnlyMemory<byte> lastFrame = default;
        using var timer = new PeriodicTimer(frameInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (process.HasExited)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(_lastError)
                    ? $"FFmpeg exited with code {process.ExitCode}." : _lastError);
            // The rawvideo input has a fixed frame rate and therefore assigns
            // one frame interval to every frame received. A slow frame copy,
            // resize, encode, or pipe write can make a timer tick miss its
            // deadline; writing only one frame in that case would shorten the
            // resulting recording. Keep the frame count aligned with elapsed
            // wall time and repeat the newest frame to cover missed slots.
            var dueBeforeRead = CalculateDueVideoFrames(outputClock.Elapsed,
                request.FrameRate, framesWritten);
            if (dueBeforeRead <= 0) continue;
            var frame = _frameProvider(sessionHandle, request.Width, request.Height);
            if (frame is not null)
            {
                lastFrame = GetNv12FramePayload(frame, request.Width, request.Height);
            }
            else if (lastFrame.IsEmpty)
            {
                if (firstFrameWait.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("No projection frame was received for 5 seconds.");
                continue;
            }
            var schedule = CalculateVideoWritePlan(outputClock.Elapsed,
                request.FrameRate, framesWritten);
            framesWritten = schedule.FramesWrittenBaseline;
            for (long index = 0; index < schedule.FramesToWrite; ++index)
            {
                await process.StandardInput.BaseStream.WriteAsync(lastFrame,
                    cancellationToken);
                ++framesWritten;
            }
        }
    }

    internal static long CalculateDueVideoFrames(TimeSpan elapsed,
        int frameRate, long framesWritten)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        ArgumentOutOfRangeException.ThrowIfNegative(framesWritten);
        if (elapsed <= TimeSpan.Zero) return 0;
        var due = (long)Math.Round(elapsed.TotalSeconds * frameRate,
            MidpointRounding.AwayFromZero);
        return Math.Max(0, due - framesWritten);
    }

    internal static (long FramesToWrite, long FramesWrittenBaseline)
        CalculateVideoWritePlan(TimeSpan elapsed, int frameRate,
            long framesWritten)
    {
        var framesToWrite = CalculateDueVideoFrames(elapsed, frameRate,
            framesWritten);
        var maximumCatchUpFrames = checked((long)frameRate *
            MaximumVideoCatchUpSeconds);
        if (framesToWrite <= maximumCatchUpFrames)
            return (framesToWrite, framesWritten);

        // Raw video has no timestamps. Drop the oldest backlog and advance the
        // logical schedule so the next tick follows current wall time instead
        // of repeatedly trying to drain an unbounded historical queue.
        return (maximumCatchUpFrames,
            checked(framesWritten + framesToWrite - maximumCatchUpFrames));
    }

    private async Task PumpAudioAsync(Process process, Stream output,
        ulong sessionHandle, AudioPacket? firstAudio, uint outputSampleRate,
        ushort outputChannels, CancellationToken cancellationToken)
    {
        var blockAlign = checked(outputChannels * sizeof(short));
        var bytesPerSecond = checked((long)outputSampleRate * blockAlign);
        var normalizer = new Pcm16AudioNormalizer(outputSampleRate, outputChannels);
        var sequence = firstAudio?.Sequence ?? 0;
        long emittedBytes = 0;
        long initialBytes = 0;
        if (firstAudio is not null)
        {
            var pcm = normalizer.Convert(firstAudio);
            if (pcm.Length != 0)
            {
                await output.WriteAsync(pcm, cancellationToken);
                emittedBytes = initialBytes = pcm.Length;
            }
        }
        var audioClock = Stopwatch.StartNew();
        var lastRealPacket = Stopwatch.StartNew();
        var silenceChunkBytes = checked((int)Math.Max(blockAlign,
            bytesPerSecond / 50 / blockAlign * blockAlign));
        var silence = new byte[silenceChunkBytes];
        var insertedSilence = firstAudio is null;
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
                if (firstAudio is null || lastRealPacket.Elapsed >= AudioSilenceGrace)
                {
                    var targetBytes = checked(initialBytes +
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
            if (packet.Sequence <= sequence)
                throw new InvalidDataException(
                    "The projection audio sequence did not advance.");
            var normalized = normalizer.Convert(packet);
            if (normalized.Length != 0)
            {
                await output.WriteAsync(normalized, cancellationToken);
                emittedBytes = checked(emittedBytes + normalized.Length);
            }
            sequence = packet.Sequence;
            lastRealPacket.Restart();
            insertedSilence = false;
        }
    }

    internal sealed class Pcm16AudioNormalizer
    {
        private readonly uint _targetSampleRate;
        private readonly ushort _targetChannels;
        private uint _sourceSampleRate;
        private ushort _sourceChannels;
        private long _rateRemainder;

        internal Pcm16AudioNormalizer(uint targetSampleRate, ushort targetChannels)
        {
            if (targetSampleRate is < 8000 or > 192000)
                throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
            if (targetChannels is < 1 or > 8)
                throw new ArgumentOutOfRangeException(nameof(targetChannels));
            _targetSampleRate = targetSampleRate;
            _targetChannels = targetChannels;
        }

        internal byte[] Convert(AudioPacket packet)
        {
            if (packet.SampleRate is < 8000 or > 192000 ||
                packet.Channels is < 1 or > 8 || packet.BitsPerSample != 16)
                throw new InvalidDataException(
                    "The projection audio packet has an unsupported PCM format.");
            var sourceBlockAlign = checked(packet.Channels * sizeof(short));
            if (packet.Pcm.Length == 0 || packet.Pcm.Length % sourceBlockAlign != 0)
                throw new InvalidDataException(
                    "The projection audio packet has an invalid PCM layout.");
            if (packet.SampleRate == _targetSampleRate &&
                packet.Channels == _targetChannels)
                return packet.Pcm;

            if (_sourceSampleRate != packet.SampleRate ||
                _sourceChannels != packet.Channels)
            {
                _sourceSampleRate = packet.SampleRate;
                _sourceChannels = packet.Channels;
                _rateRemainder = 0;
            }

            var sourceFrames = packet.Pcm.Length / sourceBlockAlign;
            var scaledFrames = checked((long)sourceFrames * _targetSampleRate +
                _rateRemainder);
            var targetFrames = checked((int)(scaledFrames / packet.SampleRate));
            _rateRemainder = scaledFrames % packet.SampleRate;
            if (targetFrames == 0) return [];

            var targetBlockAlign = checked(_targetChannels * sizeof(short));
            var output = new byte[checked(targetFrames * targetBlockAlign)];
            var source = packet.Pcm.AsSpan();
            for (var targetFrame = 0; targetFrame < targetFrames; ++targetFrame)
            {
                var position = targetFrames == 1 || sourceFrames == 1
                    ? 0
                    : (double)targetFrame * (sourceFrames - 1) / (targetFrames - 1);
                var lowerFrame = (int)position;
                var upperFrame = Math.Min(lowerFrame + 1, sourceFrames - 1);
                var fraction = position - lowerFrame;
                for (var targetChannel = 0;
                     targetChannel < _targetChannels; ++targetChannel)
                {
                    var sample = ReadMappedSample(source, lowerFrame, upperFrame,
                        fraction, packet.Channels, targetChannel, _targetChannels);
                    BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(
                        (targetFrame * _targetChannels + targetChannel) * sizeof(short),
                        sizeof(short)), sample);
                }
            }
            return output;
        }

        private static short ReadMappedSample(ReadOnlySpan<byte> source,
            int lowerFrame, int upperFrame, double fraction,
            ushort sourceChannels, int targetChannel, ushort targetChannels)
        {
            if (targetChannels == 1 && sourceChannels > 1)
            {
                double mixed = 0;
                for (var channel = 0; channel < sourceChannels; ++channel)
                    mixed += Interpolate(source, lowerFrame, upperFrame,
                        fraction, sourceChannels, channel);
                return ClampSample(mixed / sourceChannels);
            }
            var sourceChannel = sourceChannels == 1 ? 0 :
                Math.Min(targetChannel, sourceChannels - 1);
            return ClampSample(Interpolate(source, lowerFrame, upperFrame,
                fraction, sourceChannels, sourceChannel));
        }

        private static double Interpolate(ReadOnlySpan<byte> source,
            int lowerFrame, int upperFrame, double fraction,
            ushort channels, int channel)
        {
            var lower = ReadSample(source, lowerFrame, channels, channel);
            var upper = ReadSample(source, upperFrame, channels, channel);
            return lower + (upper - lower) * fraction;
        }

        private static short ReadSample(ReadOnlySpan<byte> source, int frame,
            ushort channels, int channel) => BinaryPrimitives.ReadInt16LittleEndian(
                source.Slice((frame * channels + channel) * sizeof(short),
                    sizeof(short)));

        private static short ClampSample(double value) => checked((short)Math.Clamp(
            Math.Round(value, MidpointRounding.AwayFromZero), short.MinValue,
            short.MaxValue));
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

    internal static ReadOnlyMemory<byte> GetNv12FramePayload(Nv12VideoFrame frame,
        uint width, uint height)
    {
        var targetBytes = checked((int)((ulong)width * height * 3U / 2U));
        if (width == 0 || height == 0 || (width & 1U) != 0 ||
            (height & 1U) != 0 || frame.Width != width ||
            frame.Height != height || frame.Stride != width ||
            frame.Pixels.Length < targetBytes)
            throw new InvalidDataException("The native output frame has an invalid layout.");
        return frame.Pixels.AsMemory(0, targetBytes);
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
            "-f", "rawvideo", "-pixel_format", "nv12",
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
        AddEncoderOptions(args, capabilities.PreferredH264Encoder, request.Kind);
        args.AddRange([
            "-pix_fmt", EncoderPixelFormat(capabilities.PreferredH264Encoder),
            "-g", (request.FrameRate * 2).ToString(),
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
        return FindH264EncoderCandidates(encoders).FirstOrDefault() ?? string.Empty;
    }

    internal static IReadOnlyList<string> FindH264EncoderCandidates(string encoders)
    {
        string[] preference =
        [
            "h264_nvenc",
            "h264_amf",
            "h264_qsv",
            "h264_mf",
            "libx264",
        ];
        return preference.Where(candidate => HasToken(encoders, candidate)).ToArray();
    }

    internal static MediaOutputCapabilities CreateCapabilities(string path,
        string encoders, string protocols, string muxers)
    {
        var preferredEncoder = SelectPreferredH264Encoder(encoders);
        var encoderCandidates = FindH264EncoderCandidates(encoders);
        var h264 = !string.IsNullOrWhiteSpace(preferredEncoder);
        var aac = HasToken(encoders, "aac");
        var opus = HasToken(encoders, "libopus");
        var rtmp = HasToken(protocols, "rtmp") && HasToken(muxers, "flv");
        var srt = HasToken(protocols, "srt") && HasToken(muxers, "mpegts");
        var whip = HasToken(muxers, "whip");
        return new(true, h264, aac, opus, rtmp, srt, whip, preferredEncoder, path,
            $"{Path.GetFileName(path)} / {preferredEncoder}")
        {
            H264EncoderCandidates = encoderCandidates,
        };
    }

    private static async Task<MediaOutputCapabilities> ResolveWorkingEncoderAsync(
        MediaOutputCapabilities capabilities, uint width, uint height,
        CancellationToken cancellationToken)
    {
        var candidates = capabilities.H264EncoderCandidates.Count == 0
            ? [capabilities.PreferredH264Encoder]
            : capabilities.H264EncoderCandidates;
        var ordered = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .OrderByDescending(candidate => string.Equals(candidate,
                capabilities.PreferredH264Encoder, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var encoder in ordered)
        {
            var usable = await CanEncodeNv12FrameAsync(capabilities.FfmpegPath,
                encoder, width, height, cancellationToken);
            DiagnosticLogger.Info("media_output", "encoder_probe",
                ("file", Path.GetFileName(capabilities.FfmpegPath)),
                ("encoder", encoder), ("width", width), ("height", height),
                ("usable", usable));
            if (usable)
            {
                return capabilities with
                {
                    HasH264Encoder = true,
                    PreferredH264Encoder = encoder,
                    Detail = $"{Path.GetFileName(capabilities.FfmpegPath)} / {encoder}",
                };
            }
        }
        return capabilities with
        {
            HasH264Encoder = false,
            PreferredH264Encoder = string.Empty,
            Detail = $"{Path.GetFileName(capabilities.FfmpegPath)} / no usable H.264 encoder",
        };
    }

    private static async Task<bool> CanEncodeNv12FrameAsync(string path,
        string encoder, uint width, uint height, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(encoder) ||
            width == 0 || height == 0 || (width & 1U) != 0 || (height & 1U) != 0)
            return false;
        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "rawvideo", "-pixel_format", "nv12",
            "-video_size", $"{width}x{height}", "-framerate", "30",
            "-i", "pipe:0", "-frames:v", "1", "-an", "-c:v", encoder,
        };
        AddEncoderOptions(arguments, encoder, MediaOutputKind.Recording);
        arguments.AddRange([
            "-pix_fmt", EncoderPixelFormat(encoder), "-f", "null", "-",
        ]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            if (!process.Start()) return false;
            started = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var errorOutput = process.StandardError.ReadToEndAsync(timeout.Token);
            var yBytes64 = CalculateLumaPlaneSize(width, height);
            if (yBytes64 > int.MaxValue * 2L / 3L) return false;
            var yBytes = checked((int)yBytes64);
            var frame = new byte[checked(yBytes + yBytes / 2)];
            Array.Fill(frame, (byte)16, 0, yBytes);
            Array.Fill(frame, (byte)128, yBytes, frame.Length - yBytes);
            await process.StandardInput.BaseStream.WriteAsync(frame, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await errorOutput;
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or
                                              System.ComponentModel.Win32Exception) { }
            }
        }

        static long CalculateLumaPlaneSize(uint frameWidth, uint frameHeight) =>
            checked((long)frameWidth * frameHeight);
    }

    private static void AddEncoderOptions(List<string> arguments, string encoder,
        MediaOutputKind kind)
    {
        if (string.Equals(encoder, "libx264", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-preset", "veryfast", "-tune", "zerolatency"]);
        else if (string.Equals(encoder, "h264_nvenc", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-preset", "p4", "-tune",
                kind == MediaOutputKind.Recording ? "hq" : "ll"]);
        else if (string.Equals(encoder, "h264_amf", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-usage", kind == MediaOutputKind.Recording
                ? "high_quality" : "lowlatency"]);
        else if (string.Equals(encoder, "h264_qsv", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-preset", kind == MediaOutputKind.Recording
                ? "medium" : "veryfast"]);
        else if (string.Equals(encoder, "h264_mf", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-hw_encoding", "1", "-scenario",
                kind == MediaOutputKind.Recording ? "archive" : "live_streaming"]);
    }

    private static string EncoderPixelFormat(string encoder) =>
        string.Equals(encoder, "libx264", StringComparison.OrdinalIgnoreCase)
            ? "yuv420p" : "nv12";

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
        await ThrowStartupExitAsync(process, cancellationToken);
    }

    private async Task ThrowStartupExitAsync(Process process,
        CancellationToken cancellationToken)
    {
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

    internal static IReadOnlyList<string> ParseFfmpegLocations(string output)
    {
        var paths = new List<string>();
        foreach (var line in output.Split(['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(line) || paths.Contains(line,
                    StringComparer.OrdinalIgnoreCase)) continue;
            paths.Add(line);
        }
        return paths;
    }

    private static async Task<IReadOnlyList<string>> FindFfmpegCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
        };
        candidates = candidates.Where(path => File.Exists(path) &&
            RuntimeBinaryIntegrity.IsTrustedFfmpeg(path)).ToList();
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "where.exe",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("ffmpeg.exe");
            process = Process.Start(start);
            if (process is null) return candidates;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask + await errorTask;
            foreach (var path in ParseFfmpegLocations(output))
            {
                if (RuntimeBinaryIntegrity.IsTrustedFfmpeg(path) &&
                    !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(path);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            DiagnosticLogger.Info("media_output", "ffmpeg_discovery_timeout");
        }
        catch (Exception error)
        {
            TryKillProcess(process);
            DiagnosticLogger.ExceptionOnce("ffmpeg-discovery", "media_output",
                "ffmpeg_discovery_failed", error);
        }
        finally
        {
            process?.Dispose();
        }
        return candidates;
    }

    private static void TryKillProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or
                                      System.ComponentModel.Win32Exception or
                                      NotSupportedException)
        {
            DiagnosticLogger.ExceptionOnce("ffmpeg-discovery-kill", "media_output",
                "ffmpeg_discovery_kill_failed", error);
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
