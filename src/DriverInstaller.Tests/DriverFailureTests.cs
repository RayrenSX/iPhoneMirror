using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.DriverInstaller.Services;
using IPhoneMirror.Shared.Networking;

internal static class DriverFailureTests
{
    private static readonly ParentDriverChoice Composite = new(@"C:\Windows\INF\usb.inf",
        "Composite.Dev", "Composite", "Microsoft", 0x000A000000010002, 1);
    private static AppleDeviceRecord Healthy => new(@"USB\VID_05AC&PID_12A8\0000810100044D600A22001E",
        "0000810100044D600A22001E", "Test", "", "iPhone", "", "", 1, "usbccgp", true, true,
        ["libusb0"], 0, true, "usb.inf", Composite.Section, Composite.VersionText);

    internal static void Run()
    {
        Check(!DriverOperationSafety.NeedsHealthCheck(DriverOperationKind.Uninstall, false), "offline uninstall");
        Check(DriverOperationSafety.NeedsHealthCheck(DriverOperationKind.Uninstall, true), "online uninstall");
        foreach (var problem in new uint[] { 10, 28, 31, 43 })
        foreach (var kind in Enum.GetValues<DriverOperationKind>())
            Check(!DriverOperationSafety.IsReconnectComplete(Healthy with { ProblemCode = problem }, kind), "unhealthy reconnect");
        Check(DriverOperationSafety.IsReconnectComplete(Healthy, DriverOperationKind.Install), "healthy install");
        Check(!DriverOperationSafety.IsReconnectComplete(Healthy, DriverOperationKind.Uninstall), "filter remains");
        Check(DriverOperationSafety.IsReconnectComplete(Healthy with { HasLibUsb0Filter = false }, DriverOperationKind.Uninstall), "removed filter");
        Check(!DriverOperationSafety.IsReconnectComplete(Healthy with { DriverInf = "oem42.inf" }, DriverOperationKind.ParentRepair, Composite), "binding mismatch");
        Check(!DriverOperationSafety.RemoveDeviceArguments(Healthy.InstanceId, 19041).Contains("/force"), "Windows 10 arguments");
        Check(!DriverOperationSafety.RemoveDeviceArguments(Healthy.InstanceId, 22000).Contains("/force"), "Windows 11 21H2 arguments");
        Check(DriverOperationSafety.RemoveDeviceArguments(Healthy.InstanceId, 22621).Contains("/force"), "Windows 11 22H2 arguments");

        var partial = ParentDriverNative.CollectSources(new (string, Func<IReadOnlyList<ParentDriverChoice>>)[]
        {
            ("broken", () => throw new Win32Exception(5)),
            ("healthy", () => [Composite]),
            ("duplicate", () => [Composite]),
        });
        Check(partial.Choices.Count == 1 && partial.Errors.Count == 1, "partial candidates preserved");
        Check(DeviceCatalog.ReadIsolated("bad", () => throw new UnauthorizedAccessException()) is null, "bad registry node isolated");
        var healthy = Healthy;
        Check(ReferenceEquals(DeviceCatalog.ReadIsolated("good", () => healthy), healthy), "good registry node preserved");
        Check(new DeviceCatalog().FindExact(@"USB\VID_05AC&PID_12A8\..\Services", Healthy.Serial) is null, "direct lookup rejects traversal");

        foreach (var exitCode in new[] { 3010, 1641 })
        {
            var restart = AppleSupportInstaller.RestartResult(exitCode, "test.log");
            Check(restart is { Success: true, RequiresRestart: true }, "MSI restart requirement");
        }
        Check(AppleSupportInstaller.RestartResult(0, "test.log") is null, "normal MSI completion");

        using (var embedded = typeof(DriverCleanupHost).Assembly.GetManifestResourceStream("DriverCleanup.Script.ps1")!)
        using (var reader = new StreamReader(embedded))
        {
            var canonical = reader.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n");
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
            var expected = (string)typeof(DriverCleanupHost).GetField("ScriptHash",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetRawConstantValue()!;
            Check(actual == expected, "embedded cleanup script matches protected hash");
        }

        using var client = DriverOperationCancellation.Create(Guid.NewGuid().ToString("N"));
        client.Checkpoint();
        client.Request();
        var state = Healthy with { Service = "AppleUSB", DriverInf = "oem42.inf" };
        var original = new ParentDriverChoice(@"C:\Windows\INF\oem42.inf", state.DriverSection,
            "Apple", "Apple", Composite.Version, 1);
        var consent = ParentDriverConsent.Create(state, ParentDriverAction.Bind, Composite);
        var calls = 0;
        var cancelled = ParentDriverChange.Apply(consent, original, () => state,
            _ => { calls++; return false; }, () => { }, _ => { }, client.Checkpoint);
        Check(!cancelled.Success && calls == 0, "cancel before mutation");

        var id = Guid.NewGuid().ToString("N");
        using var request = DriverOperationCancellation.Create(id);
        using var host = DriverOperationCancellation.Open(id);
        var initial = state;
        var recovered = ParentDriverChange.Apply(consent, original, () => state, choice =>
        {
            calls++;
            if (choice == Composite) { state = Healthy; request.Request(); }
            else state = initial;
            return false;
        }, () => { }, _ => { }, host.Checkpoint);
        Check(!recovered.Success && recovered.Message == "ParentChangeRolledBack" && calls == 2,
            "cancellation after mutation restores original binding");

        WaitForSafeExit().GetAwaiter().GetResult();
        DownloadDeadlines().GetAwaiter().GetResult();
    }

    private static async Task WaitForSafeExit()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::ReadLine() | Out-Null" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var timeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = DriverOperationSafety.WaitForExitAsync(process, TimeSpan.FromMilliseconds(100), () => timeout.SetResult());
        try
        {
            await timeout.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!wait.IsCompleted && !process.HasExited, "timeout must retain pending operation and running process");
        }
        finally { await process.StandardInput.WriteLineAsync(); process.StandardInput.Close(); }
        await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Check(process.ExitCode == 0, "child exited normally");
    }

    private static async Task DownloadDeadlines()
    {
        foreach (var segmented in new[] { false, true })
        {
            using var http = new HttpClient(new StalledBodyHandler(segmented)) { Timeout = TimeSpan.FromMilliseconds(30) };
            var path = Path.Combine(Path.GetTempPath(), "driver-download-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                await ExpectCancellation(AppleSupportInstaller.DownloadApplePackageAsync(http,
                    new Uri("https://apple.com/test"), path,
                    new SegmentedDownloadOptions(8192, MaximumConcurrency: 2, MinimumSegmentBytes: 4096),
                    timeout: TimeSpan.FromMilliseconds(200)));
                Check(!File.Exists(path), "cancelled package removed");
            }
            finally { File.Delete(path); }
        }
        using var catalogHttp = new HttpClient(new StalledBodyHandler(false));
        await ExpectCancellation(AppleSupportInstaller.DownloadAppleUpdateCatalogAsync(catalogHttp,
            TimeSpan.FromMilliseconds(200)));
    }

    private static async Task ExpectCancellation(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Stalled body did not cancel.");
    }

    private sealed class StalledBodyHandler(bool segmented) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            var response = new HttpResponseMessage(segmented ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                RequestMessage = request, Content = new StreamContent(new StalledStream()),
            };
            response.Content.Headers.ContentLength = segmented ? range!.To!.Value - range.From!.Value + 1 : 8192;
            if (segmented) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(range!.From!.Value, range.To!.Value, 8192);
            return Task.FromResult(response);
        }
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
