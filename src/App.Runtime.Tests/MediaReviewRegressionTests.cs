using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using IPhoneMirror.App;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static Type MediaReviewType(string name) =>
        typeof(App).Assembly.GetType("IPhoneMirror.App." + name, true)!;

    private static void TestMediaReviewRegressions()
    {
        var request = CreateLiveRecordingRequest(typeof(App).Assembly,
            "unused-review-recording.mp4", 640, 360, 30, 2000);
        InteractionAssert(ReadProperty<string>(request, "Destination") == "unused-review-recording.mp4" &&
            ReadProperty<uint>(request, "Width") == 640 &&
            ReadProperty<uint>(request, "Height") == 360 &&
            ReadProperty<int>(request, "FrameRate") == 30 &&
            ReadProperty<int>(request, "BitrateKbps") == 2000 &&
            request.GetType().GetProperty("MicrophoneDevice")!.GetValue(request) is null,
            "Live recording request did not retain its arguments and default microphone.");
        TestHlsRequestHeadersAsync().GetAwaiter().GetResult();
        foreach (var extension in new[] { "m3u8", "mp4" })
            TestMediaSourceReplacement(extension);
        foreach (var target in new[] { 55d, 65d })
            TestExplicitHlsSeek(target);
        TestHlsStartupSeekCoalescing();
        Console.WriteLine("Media review regressions passed: HTTP headers, source replacement, explicit seeks, startup synchronization and recording request.");
    }

    private static void TestMediaSourceReplacement(string extension)
    {
        using var fixture = new MediaReviewFixture();
        var request = Activator.CreateInstance(MediaReviewType("Interop.MediaCastRequest"),
            KeyboardTestMembers, null,
            [1UL, Enum.Parse(MediaReviewType("Interop.MediaCastCommand"), "Play"),
                Enum.ToObject(MediaReviewType("Interop.MediaCastFlags"), 0),
                $"http://127.0.0.1:0/review.{extension}", 120d, 0d, 1d], null)!;
        KeyboardCall(fixture.Window, "PlayMediaCast", request);
        fixture.AssertOldBridgeReleased();
        var replacement = KeyboardField(fixture.Window, "_mediaHlsBridge");
        InteractionAssert(extension == "m3u8" ? replacement is not null : replacement is null,
            "Source replacement retained the wrong playback backend.");
    }

    private static void TestExplicitHlsSeek(double target)
    {
        using var fixture = new MediaReviewFixture();
        KeyboardCall(fixture.Window, "SeekMediaCastLocally", target);
        fixture.AssertOldBridgeReleased();
        InteractionAssert(KeyboardField(fixture.Window, "_mediaHlsBridge") is { } bridge &&
            !ReferenceEquals(bridge, fixture.Bridge), "Explicit seek did not rebuild the HLS stream.");
        InteractionAssert((double)KeyboardField(fixture.Window, "_mediaBridgeOffset") == target &&
            (double?)KeyboardField(fixture.Window, "_mediaPendingHlsSeekPosition") == target &&
            (double)KeyboardCall(fixture.Window, "ReadMediaCastTimelinePosition")! == target,
            "Explicit seek changed the display without moving the stream to the requested position.");
        InteractionAssert(!(bool)KeyboardField(fixture.Window, "_mediaShouldPlay"),
            "Seeking a paused stream unexpectedly resumed playback.");
    }

    private static void TestHlsStartupSeekCoalescing()
    {
        using var fixture = new MediaReviewFixture();
        KeyboardCall(fixture.Window, "SeekMediaCastToPosition", 62d, true);
        InteractionAssert(ReferenceEquals(KeyboardField(fixture.Window, "_mediaHlsBridge"), fixture.Bridge) &&
            !(bool)KeyboardField(fixture.Bridge, "_disposed"),
            "Sender startup synchronization unnecessarily restarted the HLS stream.");
    }

    private static async Task TestHlsRequestHeadersAsync()
    {
        var type = MediaReviewType("Services.HlsMediaPlaybackBridge");
        var ffmpeg = (string?)type.GetMethod("FindBundledFfmpeg",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        InteractionAssert(ffmpeg is not null, "HTTP header regression requires bundled FFmpeg.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/review.m3u8");
        var arguments = (IReadOnlyList<string>)type.GetMethod("BuildArguments",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [source, new Uri("http://127.0.0.1/unused.ts"), 0d])!;
        var start = new ProcessStartInfo(ffmpeg!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(cancellation.Token);
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellation.Token);
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            var received = new StringBuilder();
            var buffer = new byte[1];
            while (received.Length < 32768)
            {
                if (await stream.ReadAsync(buffer, cancellation.Token).ConfigureAwait(false) == 0) break;
                received.Append((char)buffer[0]);
                if (received.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
                cancellation.Token).ConfigureAwait(false);
            client.Close();
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            await outputTask.ConfigureAwait(false);
            var errors = await errorTask.ConfigureAwait(false);
            var headers = received.ToString().Split("\r\n");
            InteractionAssert(headers.Contains("Accept: */*") &&
                headers.Contains("Accept-Language: zh-CN,zh;q=0.9,en;q=0.8") &&
                !errors.Contains("No trailing CRLF", StringComparison.OrdinalIgnoreCase),
                "FFmpeg did not send separate, correctly terminated HTTP request headers.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
    }

    private sealed class MediaReviewFixture : IDisposable
    {
        internal readonly MainWindow Window = new() { ShowInTaskbar = false };
        internal readonly object Bridge;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Process _observer;

        internal MediaReviewFixture()
        {
            _listener.Start();
            var playback = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/review.ts");
            var executable = Path.Combine(AppContext.BaseDirectory, "IPhoneMirror.App.Runtime.Tests.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("--media-bridge-idle");
            var process = Process.Start(start)!;
            _observer = Process.GetProcessById(process.Id);
            Bridge = MediaReviewType("Services.HlsMediaPlaybackBridge")
                .GetConstructors(KeyboardTestMembers).Single().Invoke([process, _listener, playback]);
            SetKeyboardField(Window, "_mediaHlsBridge", Bridge);
            SetKeyboardField(Window, "_mediaCastActive", true);
            SetKeyboardField(Window, "_mediaOpened", true);
            SetKeyboardField(Window, "_mediaUsesHlsBridge", true);
            SetKeyboardField(Window, "_mediaIsLive", false);
            SetKeyboardField(Window, "_mediaShouldPlay", false);
            SetKeyboardField(Window, "_mediaWaitingForFirstFrame", false);
            SetKeyboardField(Window, "_mediaProgramDuration", 120d);
            SetKeyboardField(Window, "_mediaBridgeOffset", 60d);
            SetKeyboardField(Window, "_mediaSource", new Uri("http://127.0.0.1:0/review.m3u8"));
            SetKeyboardField(Window, "_mediaOpenedAtUtc", DateTime.UtcNow.AddMinutes(-1));
            KeyboardCall(Window, "SetMediaCastTimelinePosition", 60d, false);
            KeyboardCall(KeyboardField(Window, "_mediaCastEvents"), "BeginGeneration");
        }

        internal void AssertOldBridgeReleased()
        {
            InteractionAssert((bool)KeyboardField(Bridge, "_disposed"), "Replacement leaked the old HLS bridge.");
            InteractionAssert(_observer.WaitForExit(3000), "Replacement left the old encoder process running.");
            InteractionAssert(_listener.Server is null || !_listener.Server.IsBound,
                "Replacement left the old HLS listener bound.");
        }

        public void Dispose()
        {
            try
            {
                KeyboardCall(Window, "StopMediaCastPlayback", "review_cleanup");
                ((IDisposable)Bridge).Dispose();
                CloseWorkspaceTestWindow(Window);
                WaitReviewTask((Task)KeyboardCall(KeyboardField(Window, "_viewModel"), "ShutdownAsync")!);
            }
            finally
            {
                if (!_observer.HasExited)
                {
                    _observer.Kill(entireProcessTree: true);
                    _observer.WaitForExit(3000);
                }
                _observer.Dispose();
                _listener.Stop();
            }
        }
    }
}
