using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Fixtures come from the pre-optimization binary, never from the candidate:
    // otherwise a pruned encoder can silently narrow the compatibility test.
    private static int RunComponentAudioFormatTests(string referenceFfmpeg)
    {
        var root = Directory.CreateTempSubdirectory("component-audio-formats-").FullName;
        var failures = new List<string>();
        var reference = Path.GetFullPath(referenceFfmpeg);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(windows, "System32") + ";" + windows;
        try
        {
            var formats = new (string Codec, string Extension)[]
            {
                ("pcm_s16le", "wav"), ("pcm_s24le", "wav"), ("pcm_s32le", "wav"),
                ("pcm_u8", "wav"), ("pcm_f32le", "wav"), ("pcm_f64le", "wav"),
                ("pcm_s24be", "aiff"), ("adpcm_ima_wav", "wav"), ("pcm_alaw", "wav"),
                ("wmav2", "wma"), ("flac", "flac"), ("alac", "m4a"),
                ("aac", "m4a"), ("libmp3lame", "mp3"), ("libopus", "ogg"),
                ("ac3", "ac3"), ("eac3", "eac3"), ("mp2", "mp2"),
            };
            foreach (var (codec, extension) in formats)
            {
                try
                {
                    var file = Path.Combine(root, codec + "." + extension);
                    RunComponentCheck(reference, path, "-hide_banner", "-loglevel", "error", "-nostdin",
                        "-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000", "-t", "0.5",
                        "-c:a", codec, file);
                    TestComponentAudioFormatAsync(file, reference, path).GetAwaiter().GetResult();
                    Console.WriteLine($"PASS production HTTP audio decode {codec}/{extension}: non-silent 48kHz stereo, reference PCM equality, stop cleanup");
                }
                catch (Exception error)
                {
                    failures.Add(codec);
                    Console.WriteLine($"FAIL production HTTP audio decode {codec}/{extension}: {error.GetBaseException().Message}");
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        if (failures.Count != 0) throw new InvalidOperationException("Audio compatibility failures: " + string.Join(", ", failures));
        return 0;
    }

    private static async Task TestComponentAudioFormatAsync(string fixture, string reference, string path)
    {
        var urlPath = "/" + Path.GetFileName(fixture);
        await using var origin = new ComponentMediaOrigin(new Dictionary<string, byte[]>
        { [urlPath] = await File.ReadAllBytesAsync(fixture) });
        var source = new Uri(origin.Playlist, urlPath);
        async Task<byte[]> Decode(string executable)
        {
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PATH"] = path;
            foreach (var argument in MediaCastAudioDecoder.BuildArguments(source)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var bytes = new MemoryStream();
            var output = process.StandardOutput.BaseStream.CopyToAsync(bytes);
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            await output;
            ComponentRequire(process.ExitCode == 0, $"decoder exit={process.ExitCode}: {await error}");
            ComponentRequire(bytes.Length >= 19200 && bytes.ToArray().Any(value => value != 0), "No non-silent PCM produced");
            return bytes.ToArray();
        }
        var expected = await Decode(reference);
        var actual = await Decode(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"));
        ComponentRequire(expected.SequenceEqual(actual), "Candidate PCM differs from pre-optimization reference");
        using var decoder = new MediaCastAudioDecoder();
        decoder.Start(source, 0, 1);
        var watch = Stopwatch.StartNew();
        while (decoder.GetPacket(0) is null && decoder.IsRunning && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        var packet = decoder.GetPacket(0);
        ComponentRequire(packet is not null && packet.SampleRate == 48000 && packet.Channels == 2,
            "Production decoder did not deliver 48 kHz stereo PCM");
        decoder.Stop();
        ComponentRequire(!decoder.IsRunning && decoder.GetPacket(0) is null, "Stopped decoder retained queued audio");
        if (Path.GetFileName(fixture) == "pcm_f32le.wav")
        {
            // Exercise Stop against the stdout producer repeatedly; clearing
            // before awaiting the reader used to allow a late stale packet.
            for (var round = 0; round < 25; round++)
            {
                decoder.Start(source, 0, 2);
                watch.Restart();
                while (decoder.GetPacket(0) is null && decoder.IsRunning && watch.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(1);
                ComponentRequire(decoder.GetPacket(0) is not null, "Restart did not produce audio");
                decoder.Stop();
                ComponentRequire(!decoder.IsRunning && decoder.GetPacket(0) is null,
                    $"Stop race left stale PCM at restart {round}");
            }
        }
    }

    private static int RunComponentRuntimeTests()
    {
        var failures = new List<string>();
        void Test(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures.Add(name); Console.WriteLine($"FAIL {name}: {error.Message}; root: {error.GetBaseException().Message}"); }
        }
        Test("packaged component hashes and Windows-only runtime loading", TestPackagedComponentLoading);
        Test("real HLS transfer, remux and decoded audio across three restarts", () => TestComponentMediaPipelineAsync().GetAwaiter().GetResult());
        Test("bridge rejects leftover unlisted runtime files", () => TestBridgeRuntimeDamage("extra"));
        Test("bridge rejects paths outside its payload", () => TestBridgeRuntimeDamage("outside"));
        Test("bridge verifies the executable actually requested", () => TestBridgeRuntimeDamage("alias"));
        Test("bridge rejects runtime directory junctions", () => TestBridgeRuntimeDamage("junction"));
        Test("bridge bounds oversized runtime manifests", () => TestBridgeRuntimeDamage("oversized-manifest"));
        Test("wireless warm preflight detects runtime changes", TestWirelessWarmPreflight);
        if (failures.Count != 0) throw new InvalidOperationException("Component runtime failures: " + string.Join(", ", failures));
        return 0;
    }

    private static void ComponentRequire(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void TestPackagedComponentLoading()
    {
        StartupDiagnostics.ValidateRequiredRuntime();
        var root = AppContext.BaseDirectory;
        var bridge = Path.Combine(root, "tools", "iUsbBridge.exe");
        ComponentRequire(RuntimeBinaryIntegrity.VerifyUsbTouchBridgeRuntime(bridge, out var bridgeError), bridgeError);
        ComponentRequire(RuntimeBinaryIntegrity.VerifyWirelessDirectory(Path.Combine(root, "Wireless"), out var wirelessError), wirelessError);
        var ffmpeg = Path.Combine(root, "tools", "ffmpeg", "ffmpeg.exe");
        ComponentRequire(RuntimeBinaryIntegrity.IsTrustedFfmpeg(ffmpeg), "Bundled FFmpeg hash mismatch");
        foreach (var name in new[] { "iPhoneMirror.Core.dll", "iPhoneMirror.VirtualCamera.dll", "libusb-1.0.dll", "libusb0.dll" })
        {
            var library = NativeLibrary.Load(Path.Combine(root, name));
            NativeLibrary.Free(library);
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cleanPath = Path.Combine(windows, "System32") + ";" + windows;
        for (var round = 0; round < 3; round++)
        {
            RunComponentCheck(bridge, cleanPath, "--check-runtime");
            RunComponentCheck(Path.Combine(root, "Wireless", "iPhoneMirror.WirelessHost.exe"), cleanPath, "--check-runtime");
            RunComponentCheck(ffmpeg, cleanPath, "-hide_banner", "-version");
            ComponentRequire(RuntimeBinaryIntegrity.VerifyUsbTouchBridgeRuntime(bridge, out bridgeError), bridgeError);
        }
    }

    private static async Task TestComponentMediaPipelineAsync()
    {
        var root = Directory.CreateTempSubdirectory("component-media-review-").FullName;
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(windows, "System32") + ";" + windows;
        try
        {
            var segment = Path.Combine(root, "segment.ts");
            RunComponentCheck(ffmpeg, path, "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "testsrc2=size=128x72:rate=10", "-f", "lavfi", "-i",
                "sine=frequency=1000:sample_rate=48000", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-f", "mpegts", segment);
            var payloads = new Dictionary<string, byte[]>
            {
                ["/segment.ts"] = await File.ReadAllBytesAsync(segment),
                ["/test.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:1.0,\nsegment.ts\n#EXT-X-ENDLIST\n"),
            };
            await using var origin = new ComponentMediaOrigin(payloads);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
            for (var round = 0; round < 3; round++)
            {
                using var bridge = HlsMediaPlaybackBridge.TryStart(origin.Playlist);
                ComponentRequire(bridge is not null, "HLS bridge did not start");
                // Invalid local probes must not consume the one real TS stream.
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, bridge!.PlaybackUri.Port);
                var request = round switch
                {
                    0 => "GET /wrong-path HTTP/1.1\r\nHost: localhost\r\n\r\n",
                    1 => $"GET {bridge.PlaybackUri.AbsolutePath} HTTP/1.1\r\nHost:",
                    _ => $"GET {bridge.PlaybackUri.AbsolutePath} HTTP/1.1\r\nX-Oversized: {new string('a', 5000)}\r\n\r\n",
                };
                await probe.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
                var bytes = await http.GetByteArrayAsync(bridge!.PlaybackUri);
                ComponentRequire(bytes.Length > 188 && bytes.Length % 188 == 0 && bytes[0] == 0x47,
                    "HLS bridge did not produce complete MPEG-TS packets");
                var output = Path.Combine(root, $"round-{round}.ts");
                await File.WriteAllBytesAsync(output, bytes);
                RunComponentCheck(ffmpeg, path, "-hide_banner", "-loglevel", "error", "-nostdin", "-i", output, "-f", "null", "-");
                using var decoder = new MediaCastAudioDecoder();
                decoder.Start(origin.Playlist, 0, round == 1 ? 1.5 : 1);
                var watch = Stopwatch.StartNew();
                while (decoder.GetPacket(0) is null && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
                var packet = decoder.GetPacket(0);
                ComponentRequire(packet is not null && packet.SampleRate == 48000 && packet.Channels == 2,
                    "HLS audio decoder did not deliver 48 kHz stereo PCM");
                decoder.Stop();
                ComponentRequire(!decoder.IsRunning && decoder.GetPacket(0) is null, "Stopped decoder retained queued audio");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ComponentMediaOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<string, byte[]> _payloads;
        private readonly List<Task> _requests = [];
        private readonly Task _accept;
        internal Uri Playlist { get; }
        internal ComponentMediaOrigin(Dictionary<string, byte[]> payloads)
        {
            _payloads = payloads; _listener.Start();
            Playlist = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/test.m3u8");
            _accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _requests.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var line = await reader.ReadLineAsync(_stop.Token);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                var target = line?.Split(' ').ElementAtOrDefault(1) ?? "";
                var found = _payloads.TryGetValue(target, out var data);
                data ??= [];
                var header = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {data.Length}\r\nContent-Type: {(target.EndsWith("m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t")}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                await stream.WriteAsync(data, _stop.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            await _accept; await Task.WhenAll(_requests); _stop.Dispose();
        }
    }

    private static void RunComponentCheck(string executable, string path, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["PATH"] = path;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new TimeoutException(Path.GetFileName(executable)); }
        Task.WaitAll(stdout, stderr);
        ComponentRequire(process.ExitCode == 0, $"{Path.GetFileName(executable)} exited {process.ExitCode}: {stderr.Result}");
    }

    private static void TestBridgeRuntimeDamage(string scenario)
    {
        var root = Directory.CreateTempSubdirectory("component-runtime-review-").FullName;
        var internalPath = Path.Combine(root, "_internal");
        var junction = false;
        try
        {
            Directory.CreateDirectory(internalPath);
            var executable = Path.Combine(root, "iUsbBridge.exe");
            File.WriteAllText(executable, "bridge fixture");
            File.WriteAllText(Path.Combine(internalPath, "dependency.dll"), "runtime fixture");
            var names = new List<string> { "iUsbBridge.exe", "_internal/dependency.dll" };
            void Manifest() => File.WriteAllText(Path.Combine(root, "iUsbBridge.runtime.json"), JsonSerializer.Serialize(new
            {
                schema = 1,
                files = names.Select(name => new { path = name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) }),
            }));
            Manifest();
            ComponentRequire(RuntimeBinaryIntegrity.VerifyUsbTouchBridgeRuntime(executable, out _), "Valid fixture rejected");
            switch (scenario)
            {
                case "extra": File.WriteAllText(Path.Combine(internalPath, "stale-plugin.dll"), "leftover"); break;
                case "outside":
                    File.WriteAllText(Path.Combine(root, "outside.dll"), "unexpected layout");
                    names.Add("outside.dll"); Manifest(); break;
                case "alias":
                    executable = Path.Combine(root, "unverified.exe"); File.WriteAllText(executable, "not in manifest"); break;
                case "oversized-manifest":
                    File.WriteAllText(Path.Combine(root, "iUsbBridge.runtime.json"), new string(' ', 1024 * 1024 + 1)); break;
                case "junction":
                    var target = Path.Combine(root, "saved-runtime");
                    Directory.Move(internalPath, target);
                    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    start.Arguments = $"/c mklink /J \"{internalPath}\" \"{target}\"";
                    using (var process = Process.Start(start)!)
                    { process.WaitForExit(); ComponentRequire(process.ExitCode == 0, "Junction fixture failed"); }
                    junction = true;
                    break;
            }
            ComponentRequire(!RuntimeBinaryIntegrity.VerifyUsbTouchBridgeRuntime(executable, out _), "Invalid runtime was reported ready");
        }
        finally
        {
            if (junction) Directory.Delete(internalPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestWirelessWarmPreflight()
    {
        var root = Directory.CreateTempSubdirectory("wireless-preflight-review-").FullName;
        const string variable = "IPHONE_MIRROR_AIRPLAY_HOST";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Wireless")))
                File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            var executable = Path.Combine(root, "iPhoneMirror.WirelessHost.exe");
            Environment.SetEnvironmentVariable(variable, executable);
            var service = new WirelessReceiverService();
            ComponentRequire(service.ProbeRuntime().Success, "Initial preflight failed");
            var dependency = Path.Combine(root, "avcodec-58.dll");
            using (var stream = new FileStream(dependency, FileMode.Open, FileAccess.Write)) stream.WriteByte(0);
            ComponentRequire(!service.ProbeRuntime().Success, "Cached success concealed a damaged decoder DLL");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Wireless", "avcodec-58.dll"), dependency, overwrite: true);
            ComponentRequire(service.ProbeRuntime().Success, "Restored dependency did not recover");
            File.WriteAllText(executable, "not an executable");
            ComponentRequire(!service.ProbeRuntime().Success, "Cached success concealed a damaged host executable");
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); Directory.Delete(root, recursive: true); }
    }
}
