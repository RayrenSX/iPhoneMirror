using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using IPhoneMirror.App.Services;

internal static class FcupHlsPlaybackTests
{
    internal static async Task RunAsync(string ffmpeg)
    {
        MediaCastStartupBudgetTests.Run();
        var diagnostic = HlsMediaPlaybackBridge.DescribeFfmpegDiagnostic(
            "Error opening input https://cdn.test/media?signature=secret or 'http://127.0.0.1/private-token/stream.m3u8'");
        if (diagnostic is null || diagnostic.Contains("secret") || diagnostic.Contains("private-token") ||
            !diagnostic.Contains("Error opening input") ||
            HlsMediaPlaybackBridge.DescribeFfmpegDiagnostic("Opening 'https://cdn.test/secret' for reading") is not null)
            throw new Exception("FFmpeg diagnostics must retain errors without resource addresses");
        if (!RuntimeBinaryIntegrity.IsTrustedFfmpeg(ffmpeg))
            throw new Exception("Use the pinned bundled FFmpeg");
        if (!HlsMediaPlaybackBridge.IsDamagedInputDiagnostic("[in#0/hls @ 01234] corrupt input packet in stream 0") ||
            !HlsMediaPlaybackBridge.IsDamagedInputDiagnostic("[hls @ 01234] Segment 5 of playlist 1 failed too many times, skipping") ||
            HlsMediaPlaybackBridge.IsDamagedInputDiagnostic("[hls @ 01234] Failed to open segment 5 of playlist 1") ||
            HlsMediaPlaybackBridge.IsDamagedInputDiagnostic("Opening 'https://example.test/corrupt input packet in stream 0' for reading"))
            throw new Exception("Only irrecoverably damaged input diagnostics may fail a bridge");
        var root = Path.Combine(Path.GetTempPath(), "fcup-hls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        async Task<(int Exit, byte[] Data, string Error)> Run(IEnumerable<string> args)
        {
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            try
            {
                using var bytes = new MemoryStream();
                var copy = process.StandardOutput.BaseStream.CopyToAsync(bytes, timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await copy;
                return (process.ExitCode, bytes.ToArray(), await errors);
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var prefix = "/" + new string('a', 48);
        var segments = new byte[3][];
        var reusedRequests = 0;
        var seenConnections = new HashSet<int>();
        var checkConnections = false;
        var liveEnded = false;
        var faultRequests = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var splitAssets = new Dictionary<string, byte[]>();
        var videoOnly = Array.Empty<byte>();
        var movingRequests = 0;
        TaskCompletionSource? hangingRequest = null;
        TaskCompletionSource? releaseHangingRequest = null;
        var responses = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                    // The two demuxers can open the next input while a large
                    // response from the first is still being consumed.
                    responses.Add(Task.Run(async () =>
                    {
                    try
                    {
                    var response = context.Response;
                    lock (seenConnections)
                        if (checkConnections && !seenConnections.Add(context.Request.RemoteEndPoint.Port))
                            Interlocked.Increment(ref reusedRequests);
                    var path = context.Request.Url!.AbsolutePath;
                    byte[] body;
                    if (splitAssets.TryGetValue(path, out var splitBody))
                        body = splitBody;
                    else if (path == prefix + "/moving/stream.m3u8")
                    {
                        var first = Interlocked.Increment(ref movingRequests) == 1 ? 0 : 1;
                        body = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:" + first + "\n" +
                            string.Join("", Enumerable.Range(first, 3 - first).Select(i => "#EXTINF:2,\n" + prefix + "/segment/stream.m3u8?n=" + i + "\n")));
                    }
                    else if (path.Contains("/master/"))
                    {
                        if (context.Request.QueryString["hang"] == "1")
                        {
                            hangingRequest!.TrySetResult();
                            await releaseHangingRequest!.Task.WaitAsync(timeout.Token);
                            response.Abort();
                            return;
                        }
                        if (int.TryParse(context.Request.QueryString["delay"], out var seconds))
                            await Task.Delay(TimeSpan.FromSeconds(seconds), timeout.Token);
                        var count = context.Request.QueryString["long"] == "1" ? 300 :
                            context.Request.QueryString["shortmany"] == "1" ? 15 : 3;
                        var fault = context.Request.QueryString["fault"];
                        body = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n" +
                            string.Join("", Enumerable.Range(0, count).Select(i =>
                                (i > 0 && i % 3 == 0 ? "#EXT-X-DISCONTINUITY\n" : "") +
                                "#EXTINF:2,\n" + prefix + "/segment/stream.m3u8?n=" + i % 3 +
                                (fault is null ? "" : "&fault=" + fault) + "\n")) +
                            (context.Request.QueryString["live"] != "1" || Volatile.Read(ref liveEnded) ? "#EXT-X-ENDLIST\n" : ""));
                        response.ContentType = "application/vnd.apple.mpegurl";
                    }
                    else if (path == "/live/video.ts")
                    {
                        response.ContentType = "video/mp2t";
                        body = segments[0];
                    }
                    else if (path == "/video-only.m3u8")
                        body = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n/video-only.ts\n#EXT-X-ENDLIST\n");
                    else if (path == "/video-only.ts") body = videoOnly;
                    else if (path.Contains("/segment/"))
                    {
                        response.StatusCode = 302;
                        response.RedirectLocation = "/actual.ts?n=" + context.Request.QueryString["n"] +
                            (context.Request.QueryString["fault"] is { } fault ? "&fault=" + fault : "");
                        response.Close();
                        return;
                    }
                    else
                    {
                        var index = int.TryParse(context.Request.QueryString["n"], out var parsed) ? parsed : 0;
                        body = segments[index];
                        if (context.Request.QueryString["fault"] == "slow")
                            await Task.Delay(250, timeout.Token);
                        if (index == 1 && context.Request.QueryString["fault"] is { } fault)
                        {
                            var attempts = faultRequests.AddOrUpdate(fault, 1, (_, previous) => previous + 1);
                            if ((fault is "once" or "old") && attempts <= 2 || fault == "permanent")
                            {
                                response.StatusCode = 503;
                                response.ContentLength64 = 0;
                                response.Close();
                                return;
                            }
                            if (fault == "truncated" && attempts == 1)
                            {
                                response.ContentLength64 = body.Length;
                                await response.OutputStream.WriteAsync(body.AsMemory(0, body.Length / 3), timeout.Token);
                                response.Abort();
                                return;
                            }
                        }
                    }
                    response.ContentLength64 = body.Length;
                    await response.OutputStream.WriteAsync(body, timeout.Token);
                    response.Close();
                    }
                    catch (Exception error) when (error is IOException or HttpListenerException or
                        ObjectDisposedException or OperationCanceledException) { }
                    }));
                }
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        });
        try
        {
            var continuousGenerated = await Run(["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "6",
                "-c:v", "libx264", "-crf", "0", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0",
                "-c:a", "aac", "-hls_time", "2", "-hls_list_size", "0",
                "-hls_segment_filename", Path.Combine(root, "continuous-%d.ts"),
                "-f", "hls", Path.Combine(root, "continuous.m3u8")]);
            if (continuousGenerated.Exit != 0) throw new Exception(continuousGenerated.Error);
            for (var part = 0; part < 3; part++)
                segments[part] = await File.ReadAllBytesAsync(Path.Combine(root, $"continuous-{part}.ts"), timeout.Token);
            var generatedVideoOnly = await Run(["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25",
                "-t", "2", "-c:v", "libx264", "-f", "mpegts", "pipe:1"]);
            if (generatedVideoOnly.Exit != 0) throw new Exception(generatedVideoOnly.Error);
            videoOnly = generatedVideoOnly.Data;
            var uri = new Uri($"http://127.0.0.1:{port}{prefix}/master/stream.m3u8");
            var videoArgs = HlsMediaPlaybackBridge.BuildArguments(uri, new Uri("http://127.0.0.1/output.ts")).ToList();
            var oldArgs = videoArgs.ToList();
            oldArgs.RemoveRange(oldArgs.IndexOf("-extension_picky"), 2);
            var before = await Run(oldArgs);
            if (before.Exit == 0 || !before.Error.Contains("mismatches allowed extensions"))
                throw new Exception("Fixture must reproduce the old suffix rejection: " + before.Error);
            checkConnections = true;
            var after = await Run(videoArgs);
            if (after.Exit != 0 || after.Data.Length == 0) throw new Exception(after.Error);
            checkConnections = false;
            if (reusedRequests != 0) throw new Exception("FCUP segments reused a previous HTTP connection");
            var output = Path.Combine(root, "result.ts");
            await File.WriteAllBytesAsync(output, after.Data, timeout.Token);
            var decoded = await Run(["-v", "error", "-i", output, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"]);
            if (decoded.Exit != 0) throw new Exception(decoded.Error);
            // Four six-second periods reset separate fMP4 audio/video clocks.
            // The old 10s interleave limit emits video about eight seconds
            // ahead of audio, despite each track's own timestamps increasing.
            // That fills WMF's queue and freezes it at the first boundary.
            foreach (var track in new[] { "video", "audio" })
            {
                var directory = Path.Combine(root, track);
                Directory.CreateDirectory(directory);
                var generate = track == "video"
                    ? new[] { "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-an",
                        "-c:v", "libx264", "-preset", "ultrafast", "-g", "50" }
                    : new[] { "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-vn", "-c:a", "aac" };
                var generated = await Run(new[] { "-v", "error", "-y" }.Concat(generate).Concat(new[] {
                    "-t", "6", "-f", "hls", "-hls_time", "2", "-hls_list_size", "0",
                    "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", Path.Combine(directory, "init.mp4"),
                    "-hls_segment_filename", Path.Combine(directory, "part-%03d.m4s"), Path.Combine(directory, "stream.m3u8") }));
                if (generated.Exit != 0) throw new Exception(generated.Error);
                var presentation = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:3\n#EXT-X-PLAYLIST-TYPE:VOD\n");
                for (var period = 0; period < 4; period++)
                {
                    if (period > 0) presentation.AppendLine("#EXT-X-DISCONTINUITY");
                    foreach (var line in File.ReadAllLines(Path.Combine(directory, "stream.m3u8")))
                    {
                        if (line.StartsWith("#EXT-X-MAP:"))
                            presentation.AppendLine($"#EXT-X-MAP:URI=\"{prefix}/split/{period}/{track}/init.mp4\"");
                        else if (line.StartsWith("#EXTINF:")) presentation.AppendLine(line);
                        else if (line.Length > 0 && !line.StartsWith('#'))
                            presentation.AppendLine($"{prefix}/split/{period}/{track}/{line}");
                    }
                    foreach (var file in Directory.GetFiles(directory).Where(file => !file.EndsWith(".m3u8")))
                        splitAssets[$"{prefix}/split/{period}/{track}/{Path.GetFileName(file)}"] = await File.ReadAllBytesAsync(file, timeout.Token);
                }
                presentation.AppendLine("#EXT-X-ENDLIST");
                splitAssets[$"{prefix}/split/{track}.m3u8"] = Encoding.UTF8.GetBytes(presentation.ToString());
            }
            splitAssets[$"{prefix}/split/stream.m3u8"] = Encoding.UTF8.GetBytes(
                "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"English\",DEFAULT=YES,URI=\"audio.m3u8\"\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=6000000,CODECS=\"avc1.42c01e,mp4a.40.2\",AUDIO=\"audio\"\nvideo.m3u8\n");
            var splitSource = new Uri($"http://127.0.0.1:{port}{prefix}/split/stream.m3u8");
            var splitArguments = HlsMediaPlaybackBridge.BuildArguments(splitSource, uri, 0.064).ToList();
            var oldInterleave = splitArguments.ToList();
            var secondInput = oldInterleave.IndexOf("-reconnect", oldInterleave.IndexOf("-i") + 1);
            var secondInputEnd = oldInterleave.IndexOf("-i", secondInput) + 2;
            oldInterleave.RemoveRange(secondInput, secondInputEnd - secondInput);
            oldInterleave.RemoveRange(oldInterleave.IndexOf("-an"), 3);
            oldInterleave[oldInterleave.IndexOf("1:a:0?")] = "0:a:0?";
            var oldSplit = await Run(oldInterleave);
            var newSplit = await Run(splitArguments);
            if (oldSplit.Exit != 0 || newSplit.Exit != 0 ||
                MaximumPacketSkew(oldSplit.Data) < 3 || MaximumPacketSkew(newSplit.Data) > 1)
                throw new Exception($"Separate fMP4 HLS interleaving regression: old={MaximumPacketSkew(oldSplit.Data):F3}, new={MaximumPacketSkew(newSplit.Data):F3}; " + newSplit.Error);
            Console.WriteLine($"PASS split fMP4 HLS packet ordering: old A/V lead={MaximumPacketSkew(oldSplit.Data):F3}s, corrected={MaximumPacketSkew(newSplit.Data):F3}s.");
            await File.WriteAllBytesAsync(output, newSplit.Data, timeout.Token);
            var splitDecoded = await Run(["-v", "error", "-i", output, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"]);
            if (splitDecoded.Exit != 0) throw new Exception(splitDecoded.Error);
            var movingSource = new Uri($"http://127.0.0.1:{port}{prefix}/moving/stream.m3u8");
            var movingArguments = HlsMediaPlaybackBridge.BuildArguments(movingSource, uri).ToList();
            // The second live open sees the next two-second window. Do not
            // independently normalize it to zero and overlay its later audio
            // on the first window's earlier video.
            movingArguments.InsertRange(movingArguments.IndexOf("pipe:1"), ["-t", "3"]);
            var oldMovingArguments = movingArguments.ToList();
            oldMovingArguments.RemoveRange(oldMovingArguments.IndexOf("-isync"), 2);
            var oldMoving = await Run(oldMovingArguments);
            Interlocked.Exchange(ref movingRequests, 0);
            var moving = await Run(movingArguments);
            if (oldMoving.Exit != 0 || moving.Exit != 0)
                throw new Exception("Moving HLS fixture failed to remux: " + oldMoving.Error + moving.Error);
            var oldOrigins = FirstPacketOrigins(oldMoving.Data);
            var origins = FirstPacketOrigins(moving.Data);
            if (oldMoving.Exit != 0 || moving.Exit != 0 ||
                Math.Abs(oldOrigins.Audio - oldOrigins.Video) > 0.5 ||
                origins.Audio - origins.Video < 1.5 || origins.Audio - origins.Video > 2.5)
                throw new Exception($"Moving live windows lost their shared clock origin: old={oldOrigins}, new={origins}; " + moving.Error);
            Console.WriteLine($"PASS moving HLS windows: later audio retains {origins.Audio - origins.Video:F3}s source offset.");
            var opaqueTransport = new Uri($"http://127.0.0.1:{port}/live/video.ts");
            var nonHls = await Run(HlsMediaPlaybackBridge.BuildArguments(opaqueTransport, uri));
            if (nonHls.Exit != 0 || nonHls.Data.Length == 0 ||
                HlsMediaPlaybackBridge.BuildArguments(opaqueTransport, uri).Contains("-seg_max_retry"))
                throw new Exception("HLS private retry options must not break generic live TS inputs: " + nonHls.Error);
            var singleTrack = await Run(HlsMediaPlaybackBridge.BuildArguments(new Uri($"http://127.0.0.1:{port}/video-only.m3u8"), uri));
            if (singleTrack.Exit != 0 || singleTrack.Data.Length == 0)
                throw new Exception("Independent HLS inputs must retain video-only playback: " + singleTrack.Error);
            async Task<int> CountFrames(byte[] data)
            {
                await File.WriteAllBytesAsync(output, data, timeout.Token);
                var frames = await Run(["-v", "error", "-i", output, "-map", "0:v:0",
                    "-fps_mode", "passthrough", "-f", "framemd5", "-"]);
                if (frames.Exit != 0) throw new Exception(frames.Error);
                return Encoding.UTF8.GetString(frames.Data).Split('\n')
                    .Count(line => line.Length > 0 && !line.StartsWith('#'));
            }
            if (await CountFrames(singleTrack.Data) != 50 || await CountFrames(nonHls.Data) != 50)
                throw new Exception("HLS/non-HLS single-track compatibility lost decoded frames");
            List<string> FaultArguments(string fault)
            {
                var args = HlsMediaPlaybackBridge.BuildArguments(new Uri(uri.AbsoluteUri + "?fault=" + fault), uri).ToList();
                args.InsertRange(args.IndexOf("-i"), ["-http_multiple", "0"]);
                return args;
            }
            var noRetry = FaultArguments("old");
            var extraInput = noRetry.IndexOf("-reconnect", noRetry.IndexOf("-i") + 1);
            var extraInputEnd = noRetry.IndexOf("-i", extraInput) + 2;
            noRetry.RemoveRange(extraInput, extraInputEnd - extraInput);
            noRetry.RemoveRange(noRetry.IndexOf("-an"), 3);
            noRetry[noRetry.IndexOf("1:a:0?")] = "0:a:0?";
            noRetry.RemoveRange(noRetry.IndexOf("-seg_max_retry"), 2);
            var skipped = await Run(noRetry);
            if (skipped.Exit != 0 || await CountFrames(skipped.Data) >= 150)
                throw new Exception("Fault fixture must reproduce silent segment loss with old HLS arguments");
            var retried = await Run(FaultArguments("once"));
            if (retried.Exit != 0 || await CountFrames(retried.Data) != 150)
                throw new Exception("A temporarily unavailable HLS segment lost frames after bounded retry: " + retried.Error);
            foreach (var fault in new[] { "truncated", "permanent" })
            {
                using var damaged = HlsMediaPlaybackBridge.TryStart(new Uri(uri.AbsoluteUri + "?fault=" + fault), 0,
                    ffmpegPath: ffmpeg) ?? throw new Exception("Could not start damaged-source bridge");
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                try
                {
                    using var response = await http.GetAsync(damaged.PlaybackUri, timeout.Token);
                    _ = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                }
                catch (HttpRequestException) { }
                using var errorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (damaged.IsRunning) await Task.Delay(20, errorTimeout.Token);
                if (damaged.ExitedSuccessfully)
                    throw new Exception("Truncated/skipped input must not be a clean bridge EOF");
            }
            using (var bridge = HlsMediaPlaybackBridge.TryStart(uri, 0,
                diagnostic: line => Console.WriteLine("probe_fixture " + line), ffmpegPath: ffmpeg)
                ?? throw new Exception("Could not start the actual HLS bridge"))
            {
                async Task<string> Probe(string request)
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, bridge.PlaybackUri.Port, timeout.Token);
                    using var stream = client.GetStream();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
                    using var response = new StreamReader(stream, Encoding.ASCII);
                    return await response.ReadToEndAsync(timeout.Token);
                }
                // WMF can probe before opening its real playback connection.
                // These requests must leave the forward-only stdout untouched.
                using (var partial = new TcpClient())
                {
                    await partial.ConnectAsync(IPAddress.Loopback, bridge.PlaybackUri.Port, timeout.Token);
                    await partial.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                        $"GET {bridge.PlaybackUri.AbsolutePath} HTTP/1.1\r\n"), timeout.Token);
                }
                var missing = await Probe("GET /missing HTTP/1.1\r\nHost: localhost\r\n\r\n");
                if (!missing.StartsWith("HTTP/1.1 404 ")) throw new Exception("Wrong bridge path was accepted");
                var head = await Probe($"HEAD {bridge.PlaybackUri.AbsolutePath} HTTP/1.1\r\nHost: localhost\r\n\r\n");
                if (!head.StartsWith("HTTP/1.1 200 ") || !head.EndsWith("\r\n\r\n"))
                    throw new Exception("Bridge HEAD probe must return headers without consuming TS output");
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var response = await http.GetAsync(bridge.PlaybackUri, timeout.Token);
                var streamed = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                if (streamed.Length <= 512 * 1024 || response.Content.Headers.ContentLength != streamed.Length)
                    throw new Exception("A finite clip beyond the RAM prefix must be spooled and advertise its exact byte length for WMF");
                await File.WriteAllBytesAsync(output, streamed, timeout.Token);
                var frames = await Run(["-v", "error", "-i", output, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-"]);
                var count = Encoding.UTF8.GetString(frames.Data).Split('\n')
                    .Count(line => line.Length > 0 && !line.StartsWith('#'));
                if (frames.Exit != 0 || count != 150)
                    throw new Exception($"Bridge dropped/truncated frames after probes: {count}; " + frames.Error);
                using (var replay = await http.GetAsync(bridge.PlaybackUri, timeout.Token))
                    if (!(await replay.Content.ReadAsByteArrayAsync(timeout.Token)).AsSpan().SequenceEqual(streamed))
                        throw new Exception("A completed finite response could not be replayed byte for byte");
                async Task CheckRange(string range, int expectedStart, int expectedLength,
                    HttpStatusCode status = HttpStatusCode.PartialContent)
                {
                    using var message = new HttpRequestMessage(HttpMethod.Get, bridge.PlaybackUri);
                    message.Headers.TryAddWithoutValidation("Range", range);
                    using var ranged = await http.SendAsync(message, timeout.Token);
                    var data = await ranged.Content.ReadAsByteArrayAsync(timeout.Token);
                    if (ranged.StatusCode != status || ranged.Content.Headers.ContentLength != expectedLength ||
                        !data.AsSpan().SequenceEqual(streamed.AsSpan(expectedStart, expectedLength)))
                        throw new Exception("Finite response returned the wrong bytes for " + range);
                    if (status == HttpStatusCode.PartialContent &&
                        (ranged.Content.Headers.ContentRange?.From != expectedStart ||
                         ranged.Content.Headers.ContentRange?.To != expectedStart + expectedLength - 1 ||
                         ranged.Content.Headers.ContentRange?.Length != streamed.Length))
                        throw new Exception("Finite response returned an incorrect Content-Range");
                }
                await CheckRange("bytes=0-", 0, streamed.Length);
                await CheckRange("bytes=163840-", 163840, streamed.Length - 163840);
                await CheckRange("bytes=188-375", 188, 188);
                await CheckRange("bytes=-188", streamed.Length - 188, 188);
                await CheckRange("bytes=0-99999999999999999999", 0, streamed.Length);
                await CheckRange("bytes=-99999999999999999999", 0, streamed.Length);
                await CheckRange($"bytes={streamed.Length - 1}-", streamed.Length - 1, 1);
                await CheckRange($"bytes={streamed.Length}-", 0, 0, HttpStatusCode.RequestedRangeNotSatisfiable);
                await CheckRange("bytes=375-188", 0, 0, HttpStatusCode.RequestedRangeNotSatisfiable);
                await CheckRange("bytes=-0", 0, 0, HttpStatusCode.RequestedRangeNotSatisfiable);
                using (var abandoned = new TcpClient())
                {
                    abandoned.Client.LingerState = new LingerOption(true, 0);
                    await abandoned.ConnectAsync(IPAddress.Loopback, bridge.PlaybackUri.Port, timeout.Token);
                    await abandoned.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                        $"GET {bridge.PlaybackUri.AbsolutePath} HTTP/1.1\r\nHost: localhost\r\nRange: bytes=0-\r\n\r\n"), timeout.Token);
                    var probeBytes = new byte[32768];
                    await abandoned.GetStream().ReadExactlyAsync(probeBytes, timeout.Token);
                }
                using (var replay = await http.GetAsync(bridge.PlaybackUri, timeout.Token))
                    if (!(await replay.Content.ReadAsByteArrayAsync(timeout.Token)).AsSpan().SequenceEqual(streamed))
                        throw new Exception("An aborted finite probe damaged a later complete GET");
            }
            var slowWatch = Stopwatch.StartNew();
            var slowProgress = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using (var bridge = HlsMediaPlaybackBridge.TryStart(
                new Uri($"http://127.0.0.1:{port}{prefix}/master/stream.m3u8?delay=8"), 0,
                diagnostic: slowProgress.Enqueue, ffmpegPath: ffmpeg)
                ?? throw new Exception("Could not start delayed finite bridge"))
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var response = await http.GetAsync(bridge.PlaybackUri, timeout.Token);
                var streamed = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                if (slowWatch.Elapsed < TimeSpan.FromSeconds(15) ||
                    !slowProgress.Contains("hls_bridge_startup_progress stage=input_0_ready") ||
                    !slowProgress.Contains("hls_bridge_startup_progress stage=input_1_ready") ||
                    bridge.HasStartupTimedOut)
                    throw new Exception("Two delayed probes must progress beyond the former fifteen-second deadline");
                if (response.Content.Headers.ContentLength != streamed.Length)
                    throw new Exception("Slow initial network/probe work lost the finite response length");
                await File.WriteAllBytesAsync(output, streamed, timeout.Token);
                var frames = await Run(["-v", "error", "-i", output, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-"]);
                var count = Encoding.UTF8.GetString(frames.Data).Split('\n')
                    .Count(line => line.Length > 0 && !line.StartsWith('#'));
                if (frames.Exit != 0 || count != 150)
                    throw new Exception($"Delayed finite startup truncated TS output: {count}; " + frames.Error);
            }
            var liveDiagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using (var bridge = HlsMediaPlaybackBridge.TryStart(
                new Uri($"http://127.0.0.1:{port}{prefix}/master/stream.m3u8?live=1"), 0,
                liveDiagnostics.Enqueue, ffmpegPath: ffmpeg) ?? throw new Exception("Could not start live startup bridge"))
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, bridge.PlaybackUri.Port, timeout.Token);
                using var stream = client.GetStream();
                var watch = Stopwatch.StartNew();
                using var headerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"GET {bridge.PlaybackUri.AbsolutePath} HTTP/1.1\r\nHost: localhost\r\n\r\n"), headerTimeout.Token);
                async Task<string> ReadLine(CancellationToken cancellationToken)
                {
                    var text = new StringBuilder();
                    var one = new byte[1];
                    while (await stream.ReadAsync(one, cancellationToken) == 1)
                    {
                        if (one[0] == '\n') return text.ToString();
                        if (one[0] != '\r') text.Append((char)one[0]);
                    }
                    throw new Exception("Chunked live response ended before a complete line");
                }
                var headers = new List<string>();
                string line;
                while ((line = await ReadLine(headerTimeout.Token)).Length != 0) headers.Add(line);
                if (watch.Elapsed > TimeSpan.FromSeconds(2) ||
                    !headers.Contains("Transfer-Encoding: chunked") || headers.Any(value => value.StartsWith("Content-Length:")))
                    throw new Exception("An unknown/live source waited for a full startup buffer instead of opening promptly");
                Volatile.Write(ref liveEnded, true);
                using var payload = new MemoryStream();
                var maximumChunk = 0;
                var chunks = 0;
                while (true)
                {
                    var size = int.Parse(await ReadLine(timeout.Token), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (size == 0)
                    {
                        if ((await ReadLine(timeout.Token)).Length != 0)
                            throw new Exception("Chunked EOF must end with an empty trailer");
                        break;
                    }
                    if (size > 32 * 1024)
                        throw new Exception($"HTTP startup/stream chunk exceeded 32KiB: {size}");
                    maximumChunk = Math.Max(maximumChunk, size);
                    chunks++;
                    var bytes = new byte[size];
                    await stream.ReadExactlyAsync(bytes, timeout.Token);
                    await payload.WriteAsync(bytes, timeout.Token);
                    if ((await ReadLine(timeout.Token)).Length != 0)
                        throw new Exception("HTTP chunk payload did not end at its declared boundary");
                }
                var streamed = payload.ToArray();
                if (maximumChunk != 32 * 1024 || chunks < 17 || streamed.Length <= 512 * 1024 ||
                    !liveDiagnostics.Any(value => value == "hls_bridge_response finite=False startup_bytes=524288"))
                    throw new Exception("Raw chunk fixture must cover both the full startup prefix and later stdout");
                await File.WriteAllBytesAsync(output, streamed, timeout.Token);
                var frames = await Run(["-v", "error", "-i", output, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-"]);
                var count = Encoding.UTF8.GetString(frames.Data).Split('\n')
                    .Count(line => line.Length > 0 && !line.StartsWith('#'));
                if (frames.Exit != 0 || count != 150)
                    throw new Exception($"Timed startup prefetch dropped or duplicated live TS bytes: {count}; " + frames.Error);
                Console.WriteLine($"PASS raw chunked live transport: startup=524288 bytes, maximum_chunk={maximumChunk}, decoded_frames={count}.");
            }
            using (var bridge = HlsMediaPlaybackBridge.TryStart(
                new Uri($"http://127.0.0.1:{port}{prefix}/master/stream.m3u8?long=1"), 0,
                ffmpegPath: ffmpeg) ?? throw new Exception("Could not start disconnect bridge"))
            {
                using (var abandoned = new TcpClient())
                {
                    abandoned.Client.LingerState = new LingerOption(true, 0);
                    await abandoned.ConnectAsync(IPAddress.Loopback, bridge.PlaybackUri.Port, timeout.Token);
                    using var stream = abandoned.GetStream();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"GET {bridge.PlaybackUri.AbsolutePath} HTTP/1.1\r\nHost: localhost\r\n\r\n"), timeout.Token);
                    var received = new byte[32768];
                    await stream.ReadExactlyAsync(received, timeout.Token);
                }
                using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (bridge.IsRunning) await Task.Delay(20, disconnectTimeout.Token);
                if (bridge.ExitedSuccessfully)
                    throw new Exception("A client that drops streamed TS must fail instead of reporting natural EOF");
            }
            foreach (var seek in new[] { 2.0, 4.0 })
            {
                var sought = await Run(HlsMediaPlaybackBridge.BuildArguments(uri,
                    new Uri("http://127.0.0.1/output.ts"), seek));
                if (sought.Exit != 0 || sought.Data.Length == 0) throw new Exception("Seek output empty: " + sought.Error);
                await File.WriteAllBytesAsync(output, sought.Data, timeout.Token);
                var timestamps = await Run(["-v", "info", "-copyts", "-i", output,
                    "-map", "0:v:0", "-vf", "showinfo", "-frames:v", "2", "-f", "null", "-"]);
                var match = System.Text.RegularExpressions.Regex.Match(timestamps.Error, @"pts_time:([\d.\-]+)");
                if (!match.Success || !double.TryParse(match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture, out var first) || Math.Abs(first) > 1)
                    throw new Exception($"Seek {seek} did not produce near-zero timestamps: " + timestamps.Error);
            }
            var audio = await Run(MediaCastAudioDecoder.BuildArguments(uri));
            if (audio.Exit != 0 || audio.Data.Length < 5 * 48000 * 4) throw new Exception("Audio did not decode all segments: " + audio.Error);
            foreach (var source in new[] { "https://example.test/a.m3u8", "http://127.0.0.1/a.m3u8", "https://example.test/a.mp4" })
                if (HlsMediaPlaybackBridge.BuildArguments(new Uri(source), uri).Contains("-extension_picky") ||
                    MediaCastAudioDecoder.BuildArguments(new Uri(source)).Contains("-extension_picky") ||
                    HlsMediaPlaybackBridge.BuildArguments(new Uri(source), uri).Contains("-http_persistent"))
                    throw new Exception("Compatibility option leaked outside FCUP loopback URLs");
            async Task CheckHungSource(bool cancel)
            {
                hangingRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
                releaseHangingRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
                using var bridge = HlsMediaPlaybackBridge.TryStart(
                    new Uri(uri.AbsoluteUri + "?hang=1"), 0, messages.Enqueue, ffmpegPath: ffmpeg)
                    ?? throw new Exception("Could not start held-source bridge");
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                var watch = Stopwatch.StartNew();
                var request = http.GetAsync(bridge.PlaybackUri, timeout.Token);
                try
                {
                    await hangingRequest.Task.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
                    if (cancel)
                    {
                        watch.Restart();
                        bridge.Dispose();
                        if (watch.Elapsed > TimeSpan.FromSeconds(2))
                            throw new Exception("Canceling a blocked startup did not promptly settle its pipe read");
                    }
                    try
                    {
                        using var response = await request.WaitAsync(TimeSpan.FromSeconds(19), timeout.Token);
                        _ = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                    }
                    catch (HttpRequestException) { }
                    if (!cancel && (watch.Elapsed < TimeSpan.FromSeconds(14) ||
                        watch.Elapsed > TimeSpan.FromSeconds(19) || !bridge.HasStartupTimedOut ||
                        !messages.Any(message => message.StartsWith("hls_bridge_startup_timeout stage=", StringComparison.Ordinal))))
                        throw new Exception("A source without a probe or output milestone must fail within its idle budget");
                }
                finally { releaseHangingRequest.TrySetResult(); }
            }
            await CheckHungSource(cancel: false);
            await CheckHungSource(cancel: true);
            string[] OwnSpools() => Directory.GetFiles(Path.GetTempPath(),
                $"iPhoneMirror-hls-{Environment.ProcessId}-*.tmp");
            if (OwnSpools().Length != 0)
                throw new Exception("Completed or failed finite playback retained its temporary spool");
            using (var bridge = HlsMediaPlaybackBridge.TryStart(uri, 0,
                diagnostic: value => Console.WriteLine("unrequested_prewarm " + value), ffmpegPath: ffmpeg)
                ?? throw new Exception("Could not start an unrequested prewarm"))
            {
                await bridge.WaitUntilReadyAsync(timeout.Token);
                if (OwnSpools().Length == 0 || bridge.StartupStage == "player_open")
                    throw new Exception("Prewarm must retain its finite spool without counting a player response");
            }
            if (OwnSpools().Length != 0)
                throw new Exception("Disposing a prewarmed item without any GET leaked its spool");
            using (var bridge = HlsMediaPlaybackBridge.TryStart(
                new Uri(uri.AbsoluteUri + "?shortmany=1&fault=slow"), 0, ffmpegPath: ffmpeg)
                ?? throw new Exception("Could not start canceled-spool bridge"))
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                var request = http.GetAsync(bridge.PlaybackUri, timeout.Token);
                var watch = Stopwatch.StartNew();
                while (OwnSpools().Length == 0 && watch.Elapsed < TimeSpan.FromSeconds(12))
                    await Task.Delay(20, timeout.Token);
                if (OwnSpools().Length == 0)
                    throw new Exception("Cancellation fixture never reached disk spooling");
                bridge.Dispose();
                try { using var response = await request; }
                catch (HttpRequestException) { }
                if (OwnSpools().Length != 0)
                    throw new Exception("Canceling finite spooling left a temporary file behind");
            }
            Console.WriteLine("PASS: FCUP redirect/seek video and audio; retries preserve all150 frames; probes, >15s staged finite startup, stalled-source timeout, cancellation, live buffering and disconnect recovery pass.");
        }
        finally
        {
            listener.Close();
            await serving;
            await Task.WhenAll(responses);
            Directory.Delete(root, recursive: true);
        }
    }

    private static double MaximumPacketSkew(byte[] transport)
    {
        double? video = null, audio = null;
        var maximum = 0d;
        for (var offset = 0; offset + 188 <= transport.Length; offset += 188)
        {
            if (transport[offset] != 0x47 || (transport[offset + 1] & 0x40) == 0) continue;
            var payload = offset + 4;
            if ((transport[offset + 3] & 0x20) != 0) payload += 1 + transport[offset + 4];
            if (payload + 14 >= offset + 188 || transport[payload] != 0 || transport[payload + 1] != 0 || transport[payload + 2] != 1)
                continue;
            var stream = transport[payload + 3];
            if (stream is < 0xc0 or > 0xef || (transport[payload + 7] & 0x80) == 0) continue;
            var p = payload + 9;
            var ticks = ((long)(transport[p] >> 1 & 7) << 30) |
                ((long)((transport[p + 1] << 8 | transport[p + 2]) >> 1) << 15) |
                (long)((uint)(transport[p + 3] << 8 | transport[p + 4]) >> 1);
            if (stream >= 0xe0) video = ticks / 90000d;
            else audio = ticks / 90000d;
            if (video.HasValue && audio.HasValue)
                maximum = Math.Max(maximum, Math.Abs(video.Value - audio.Value));
        }
        return maximum;
    }

    private static (double Video, double Audio) FirstPacketOrigins(byte[] transport)
    {
        double? video = null, audio = null;
        for (var offset = 0; offset + 188 <= transport.Length; offset += 188)
        {
            if (transport[offset] != 0x47 || (transport[offset + 1] & 0x40) == 0) continue;
            var payload = offset + 4;
            if ((transport[offset + 3] & 0x20) != 0) payload += 1 + transport[offset + 4];
            if (payload + 14 >= offset + 188 || transport[payload] != 0 || transport[payload + 1] != 0 || transport[payload + 2] != 1) continue;
            var stream = transport[payload + 3];
            if (stream is < 0xc0 or > 0xef || (transport[payload + 7] & 0x80) == 0) continue;
            var p = payload + 9;
            var ticks = ((long)(transport[p] >> 1 & 7) << 30) |
                ((long)((transport[p + 1] << 8 | transport[p + 2]) >> 1) << 15) |
                (long)((uint)(transport[p + 3] << 8 | transport[p + 4]) >> 1);
            if (stream >= 0xe0) video ??= ticks / 90000d;
            else audio ??= ticks / 90000d;
            if (video.HasValue && audio.HasValue) return (video.Value, audio.Value);
        }
        throw new Exception("Moving HLS fixture must contain both video and audio packets");
    }
}
