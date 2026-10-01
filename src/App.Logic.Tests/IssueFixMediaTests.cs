using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;

internal static class IssueFixMediaTests
{
    internal static async Task RunAsync(MediaOutputCapabilities capabilities)
    {
        var root = Path.Combine(Path.GetTempPath(), "iphoneMirror-issue-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // A slow first GPU read must not postpone the audio clock by a second.
            var path = Path.Combine(root, "slow-first-frame.mp4");
            var frames = 0;
            await using (var output = new MediaOutputService((_, width, height) =>
            {
                if (Interlocked.Increment(ref frames) == 1) Thread.Sleep(1100);
                return new Nv12VideoFrame(width, height, width, 1,
                    new byte[checked((int)(width * height * 3 / 2))]);
            }, (_, _) => null))
            {
                await output.StartAsync(1, new(MediaOutputKind.Recording, path, 160, 160, 20, 500), capabilities);
                await Task.Delay(3200);
                await output.StopAsync();
            }
            var durations = TrackDurations(path);
            Require(durations["vide"] > 2.8, "slow read shortened video");
            Require(Math.Abs(durations["vide"] - durations["soun"]) < 0.18,
                $"slow-first-frame A/V duration mismatch: {durations["vide"]:F3}/{durations["soun"]:F3}");
            Console.WriteLine($"Slow first frame: video={durations["vide"]:F3}s audio={durations["soun"]:F3}s.");

            // Feed the production mixer with two known tones; replace only the
            // hardware input with a synthetic source, keeping all maps/filters.
            var phone = Path.Combine(root, "phone.pcm");
            var pcm = new byte[48000 * 6 * 4];
            for (var sample = 0; sample < 48000 * 6; ++sample)
            {
                var value = (short)(4000 * Math.Sin(2 * Math.PI * 440 * sample / 48000));
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 4, 2), value);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 4 + 2, 2), value);
            }
            await File.WriteAllBytesAsync(phone, pcm);
            var mixed = Path.Combine(root, "mixed.mp4");
            var args = MediaOutputService.BuildArguments(new(MediaOutputKind.Recording, mixed, 160, 160, 10, 500,
                MicrophoneDevice: "test microphone"), capabilities, phone).ToList();
            var directShow = args.IndexOf("dshow") - 3;
            args.RemoveRange(directShow, 8);
            args.InsertRange(directShow, ["-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=6"]);
            await RunFfmpegAsync(capabilities.FfmpegPath, args, async stream =>
            {
                var frame = new byte[160 * 160 * 3 / 2];
                long written = 0;
                // Model a six-second encoder stall; only real writes count.
                while (written < 60)
                {
                    var plan = MediaOutputService.CalculateVideoWritePlan(TimeSpan.FromSeconds(6), 10, written);
                    Require(plan.FramesWrittenBaseline == written, "unwritten video duration was discarded");
                    for (var i = 0; i < plan.FramesToWrite; ++i)
                    {
                        await stream.WriteAsync(frame);
                        ++written;
                    }
                }
            });
            durations = TrackDurations(mixed);
            Console.WriteLine($"Mixed durations: video={durations["vide"]:F3}s audio={durations["soun"]:F3}s encoder={capabilities.PreferredH264Encoder}.");
            Require(Math.Abs(durations["vide"] - 6) < 0.1, "catch-up lost encoded video time");
            Require(Math.Abs(durations["vide"] - durations["soun"]) < 0.18, "mixed A/V duration mismatch");
            var decoded = Path.Combine(root, "mixed.pcm");
            await RunFfmpegAsync(capabilities.FfmpegPath,
                ["-v", "error", "-i", mixed, "-map", "0:a", "-ac", "1", "-ar", "48000", "-f", "s16le", "-y", decoded]);
            var samples = await File.ReadAllBytesAsync(decoded);
            Require(ToneLevel(samples, 440) > 500, "phone tone missing from mixed recording");
            Require(ToneLevel(samples, 880) > 500, "microphone tone missing from mixed recording");
            Console.WriteLine($"Microphone mix: both tones present; video={durations["vide"]:F3}s audio={durations["soun"]:F3}s; EOF stopped FFmpeg.");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    private static double ToneLevel(byte[] pcm, int frequency)
    {
        double sine = 0, cosine = 0;
        const int count = 48000;
        Require(pcm.Length >= count * 4, "decoded recording is too short");
        for (var i = 0; i < count; ++i)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan((count + i) * 2, 2));
            var angle = 2 * Math.PI * frequency * i / count;
            sine += value * Math.Sin(angle);
            cosine += value * Math.Cos(angle);
        }
        return 2 * Math.Sqrt(sine * sine + cosine * cosine) / count;
    }

    private static async Task RunFfmpegAsync(string path, IReadOnlyList<string> args, Func<Stream, Task>? write = null)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (write is not null) await write(process.StandardInput.BaseStream).WaitAsync(timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Require(process.ExitCode == 0, await error);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private static Dictionary<string, double> TrackDurations(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var result = new Dictionary<string, double>();
        foreach (var movie in Boxes(bytes, 0, bytes.Length).Where(b => b.Type == "moov"))
        {
        var movieHeader = Boxes(bytes, movie.Start, movie.End).Single(b => b.Type == "mvhd").Start;
        var movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(
            movieHeader + (bytes[movieHeader] == 1 ? 20 : 12), 4));
        foreach (var track in Boxes(bytes, movie.Start, movie.End).Where(b => b.Type == "trak"))
        foreach (var media in Boxes(bytes, track.Start, track.End).Where(b => b.Type == "mdia"))
        {
            var boxes = Boxes(bytes, media.Start, media.End).ToArray();
            var header = Boxes(bytes, track.Start, track.End).Single(b => b.Type == "tkhd").Start;
            var handler = boxes.Single(b => b.Type == "hdlr").Start;
            var version1 = bytes[header] == 1;
            // tkhd measures presentation duration after the edit list, while
            // mdhd may include NVENC's B-frame decoder preroll.
            var duration = version1 ? BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(header + 28, 8))
                : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(header + 20, 4));
            result[Encoding.ASCII.GetString(bytes, handler + 8, 4)] = (double)duration / movieTimescale;
        }
        }
        return result;
    }

    private static IEnumerable<(string Type, int Start, int End)> Boxes(byte[] bytes, int start, int end)
    {
        while (start + 8 <= end)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start, 4)));
            Require(size >= 8 && start + size <= end, "invalid MP4 test fixture");
            yield return (Encoding.ASCII.GetString(bytes, start + 4, 4), start + 8, start + size);
            start += size;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
