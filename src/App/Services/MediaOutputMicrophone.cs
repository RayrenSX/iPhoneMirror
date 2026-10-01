using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace IPhoneMirror.App.Services;

internal sealed record MicrophoneDevice(string Name, string Id);

internal static class MediaOutputMicrophone
{
    // The phone stream supplies silence when the device has no audio. Its EOF
    // owns the mix lifetime, so stopping output also closes a live microphone.
    internal const string MixFilter =
        "[0:a]aresample=48000,asetpts=PTS-STARTPTS[phone];" +
        "[2:a]aresample=48000:async=1:first_pts=0,asetpts=PTS-STARTPTS[mic];" +
        "[phone][mic]amix=inputs=2:duration=first:dropout_transition=0:normalize=0," +
        "alimiter=limit=0.95:level=0[mixed]";

    internal static async Task<IReadOnlyList<MicrophoneDevice>> EnumerateAsync(
        string ffmpegPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(ffmpegPath) ||
            !RuntimeBinaryIntegrity.IsTrustedFfmpeg(ffmpegPath)) return [];
        var start = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[]
            { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            // DirectShow's list-only command deliberately exits with code 1.
            return ParseDevices(await output + "\n" + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    internal static IReadOnlyList<MicrophoneDevice> ParseDevices(string output)
    {
        var result = new List<MicrophoneDevice>();
        var pendingAudio = -1;
        foreach (var line in output.Split('\n'))
        {
            var device = Regex.Match(line, "\\\"(?<name>.*)\\\" \\((?<kind>audio|video)\\)");
            if (device.Success)
            {
                pendingAudio = -1;
                if (device.Groups["kind"].Value != "audio") continue;
                var name = device.Groups["name"].Value;
                result.Add(new(name, name));
                pendingAudio = result.Count - 1;
                continue;
            }
            var alternative = Regex.Match(line, "Alternative name \"(?<id>.*)\"");
            if (pendingAudio >= 0 && alternative.Success)
            {
                result[pendingAudio] = result[pendingAudio] with { Id = alternative.Groups["id"].Value };
                pendingAudio = -1;
            }
        }
        return result.DistinctBy(device => device.Id, StringComparer.Ordinal).ToArray();
    }
}
