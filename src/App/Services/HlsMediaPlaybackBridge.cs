using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Text.RegularExpressions;

namespace IPhoneMirror.App.Services;

// WPF MediaElement treats some HLS VOD playlists as a short finite clip and
// raises MediaEnded at the first segment boundary. FFmpeg owns playlist
// refresh, encryption, discontinuities, and segment concatenation here; WPF
// only receives one continuous MPEG-TS HTTP stream.
internal sealed class HlsMediaPlaybackBridge : IDisposable
{
    private readonly Process _process;
    private double _detectedDuration;
    private int _diagnosticOutputReported;
    private bool _disposed;

    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(\d+):([0-5]\d):([0-5]\d(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private HlsMediaPlaybackBridge(Process process, Uri playbackUri)
    {
        _process = process;
        PlaybackUri = playbackUri;
    }

    internal Uri PlaybackUri { get; }

    internal bool IsRunning => !_disposed && !_process.HasExited;

    internal bool ExitedSuccessfully => _disposed ||
        _process.HasExited && _process.ExitCode == 0;

    internal double DetectedDuration => Volatile.Read(ref _detectedDuration);

    internal static HlsMediaPlaybackBridge? TryStart(Uri source,
        Action<string>? diagnostic = null) => TryStart(source, 0, diagnostic);

    internal static HlsMediaPlaybackBridge? TryStart(Uri source,
        double startPosition, Action<string>? diagnostic = null,
        Action<double>? durationAvailable = null)
    {
        var ffmpeg = FindBundledFfmpeg();
        if (ffmpeg is null)
        {
            diagnostic?.Invoke("hls_bridge_unavailable reason=ffmpeg_missing");
            return null;
        }

        var port = ReserveLoopbackPort();
        var playbackUri = new Uri($"http://127.0.0.1:{port}/stream.ts",
            UriKind.Absolute);
        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
        };
        foreach (var argument in BuildArguments(source, playbackUri, startPosition))
            start.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return null;
            var bridge = new HlsMediaPlaybackBridge(process, playbackUri);
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                if (TryParseDuration(args.Data, out var duration) &&
                    Interlocked.CompareExchange(ref bridge._detectedDuration,
                        duration, 0) == 0)
                {
                    diagnostic?.Invoke(
                        $"hls_bridge_duration seconds={duration:F3}");
                    durationAvailable?.Invoke(duration);
                    return;
                }
                if (Interlocked.Exchange(
                        ref bridge._diagnosticOutputReported, 1) == 0)
                    diagnostic?.Invoke("hls_bridge_ffmpeg_diagnostics_active");
            };
            process.BeginErrorReadLine();
            diagnostic?.Invoke($"hls_bridge_started port={port}");
            return bridge;
        }
        catch (Exception error)
        {
            try { process?.Kill(entireProcessTree: true); }
            catch { }
            diagnostic?.Invoke($"hls_bridge_start_failed error={error.GetType().Name}");
            process?.Dispose();
            return null;
        }
    }

    internal static IReadOnlyList<string> BuildArguments(Uri source, Uri output,
        double startPosition = 0)
    {
        // The reconnect flags cover temporary playlist/key/segment failures;
        // do not reconnect at EOF, so EXT-X-ENDLIST remains a genuine VOD EOF.
        var arguments = new List<string>([
            "-hide_banner", "-nostdin", "-loglevel", "info",
            "-reconnect", "1",
            "-reconnect_streamed", "1", "-reconnect_delay_max", "5",
            // Several mobile live-CDN endpoints (including Douyin) reject
            // FFmpeg's default user agent or wait indefinitely on a stalled
            // TLS read. Use a Safari-like identity and bounded I/O waits so
            // the caller can restart the bridge instead of hanging forever.
            "-user_agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
            "-headers", "Accept: */*\\r\\nAccept-Language: zh-CN,zh;q=0.9,en;q=0.8\\r\\n",
            "-rw_timeout", "15000000",
            "-protocol_whitelist", "http,https,tcp,tls,crypto",
            "-i", source.AbsoluteUri,
            "-map", "0:v:0?", "-map", "0:a:0?",
            "-c", "copy", "-avoid_negative_ts", "make_zero",
            // The bridge is consumed by a local MediaElement. Flush each TS
            // packet and disable muxer interleave buffering so the decoded
            // frames reach WPF at the source cadence instead of arriving in
            // short bursts that look like a low or uneven frame rate.
            "-flush_packets", "1", "-muxdelay", "0", "-muxpreload", "0",
            "-f", "mpegts", "-content_type", "video/mp2t", "-listen", "1",
            output.AbsoluteUri,
        ]);
        if (double.IsFinite(startPosition) && startPosition > 0.05)
        {
            // Input seeking lets the local MPEG-TS stream begin at the
            // programme timestamp requested by the controller. The offset is
            // intentionally before -i so FFmpeg seeks the HLS demuxer rather
            // than discarding an already downloaded programme prefix.
            var inputIndex = arguments.IndexOf("-i");
            if (inputIndex >= 0)
            {
                arguments.Insert(inputIndex, startPosition.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                arguments.Insert(inputIndex, "-ss");
            }
            var outputIndex = arguments.IndexOf(output.AbsoluteUri);
            if (outputIndex >= 0)
            {
                // HLS input seeking preserves the programme PTS. A fresh
                // MediaElement must see the restarted HTTP stream begin at
                // zero, otherwise WMF may wait forever for its nominal start
                // timestamp (especially around the first non-zero keyframe).
                arguments.Insert(outputIndex, (-startPosition).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                arguments.Insert(outputIndex, "-output_ts_offset");
            }
        }
        return arguments;
    }

    internal static bool TryParseDuration(string line, out double duration)
    {
        duration = 0;
        var match = DurationPattern.Match(line);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var hours) ||
            !double.TryParse(match.Groups[2].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(match.Groups[3].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var seconds)) return false;
        duration = hours * 3600 + minutes * 60 + seconds;
        return double.IsFinite(duration) && duration > 0;
    }

    internal static string? FindBundledFfmpeg()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
        };
        return candidates.FirstOrDefault(path => File.Exists(path) &&
            RuntimeBinaryIntegrity.IsTrustedFfmpeg(path));
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally
        {
            _process.Dispose();
        }
    }
}
