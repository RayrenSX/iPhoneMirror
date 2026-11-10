using System.Net;
using System.Net.Sockets;
using System.Text;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunRepairStreamingTests()
    {
        var app = new App { IsUiPreviewMode = true,
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            var task = TestRepairStreamingAsync();
            WaitReviewTask(task);
            Console.WriteLine("Repair streaming integration passed: real FFmpeg WHIP 401, RTMP connection failure and token redaction.");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static async Task TestRepairStreamingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var caps = await MediaOutputService.ProbeAsync(timeout.Token);
        InteractionAssert(caps.Supports(MediaOutputKind.Whip) && caps.Supports(MediaOutputKind.Rtmp),
            "Streaming integration requires the bundled FFmpeg with WHIP and RTMP.");
        // Test video only, so a named audio pipe handshake cannot hide the
        // server's response. The video bytes travel through the real pump.
        caps = caps with { HasOpusEncoder = false, HasAacEncoder = false };
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        const string token = "review-token-should-never-appear";
        var received = false;
        var response = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            var bytes = new List<byte>();
            var next = new byte[1];
            while (bytes.Count < 32768 && await stream.ReadAsync(next, timeout.Token) > 0)
            {
                bytes.Add(next[0]);
                if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
            }
            var headers = Encoding.ASCII.GetString(bytes.ToArray());
            received = headers.Contains("POST ") && headers.Contains(token);
            await stream.WriteAsync("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
        }, timeout.Token);
        var authError = await ObserveOutputFailureAsync(new(MediaOutputKind.Whip,
            $"http://127.0.0.1:{port}/whip", 160, 160, 30, 500, token), caps, timeout.Token);
        await response;
        InteractionAssert(received && authError == LocalizationService.Get("StreamingUnauthorized") &&
            !authError.Contains(token), "WHIP 401 was obscured by a pipe error or leaked its token: " + authError);
        server.Stop();
        var refusal = await ObserveOutputFailureAsync(new(MediaOutputKind.Rtmp,
            $"rtmp://127.0.0.1:{port}/live/test", 160, 160, 30, 500), caps, timeout.Token);
        InteractionAssert(refusal == LocalizationService.Get("StreamingConnectionRefused") ||
            refusal == LocalizationService.Get("StreamingConnectionTimeout"),
            "RTMP connection failure was obscured by a pipe error: " + refusal);
    }

    private static async Task<string> ObserveOutputFailureAsync(MediaOutputRequest request,
        MediaOutputCapabilities caps, CancellationToken token)
    {
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pixels = Enumerable.Repeat((byte)128, 160 * 160 * 3 / 2).ToArray();
        await using var output = new MediaOutputService((_, width, height) =>
            new Nv12VideoFrame(width, height, width, DateTime.UtcNow.Ticks, pixels), (_, _) => null);
        output.StatusChanged += (message, error) => { if (error) failed.TrySetResult(message); };
        try { await output.StartAsync(1, request, caps, token); }
        catch (InvalidOperationException error) { return error.Message; }
        return await failed.Task.WaitAsync(token);
    }
}
