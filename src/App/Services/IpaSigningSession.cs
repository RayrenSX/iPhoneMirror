using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Services;

internal interface IIpaSigningSession : IAsyncDisposable
{
    Task<JsonElement> RequestAsync(object request, CancellationToken cancellation);
}

internal sealed class IpaSigningSession : IIpaSigningSession
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "iPhoneMirror-ipa-" + Guid.NewGuid().ToString("N"));
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private OwnedBridgeProcess? _worker;
    private Task? _errors;

    internal static ProcessStartInfo StartInfo(string workspace)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        start.ArgumentList.Add("--ipa-signing");
        start.ArgumentList.Add("--ipa-workspace");
        start.ArgumentList.Add(workspace);
        start.Environment.Remove("IPHONE_MIRROR_USB_MUX_RESUME");
        return start;
    }

    public async Task<JsonElement> RequestAsync(object request, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        await _gate.WaitAsync(deadline.Token);
        try
        {
            if (_worker is null)
            {
                Directory.CreateDirectory(_workspace);
                var start = StartInfo(_workspace);
                if (!await Task.Run(() => RuntimeBinaryIntegrity.VerifyUsbTouchBridgeRuntime(start.FileName, out _), deadline.Token))
                    throw new AutomationException("IPA_RUNTIME_UNAVAILABLE", LocalizationService.Get("IpaRuntimeUnavailable"));
                _worker = OwnedBridgeProcess.Start(start);
                _errors = DrainErrorsAsync(_worker.Error);
            }
            await _worker.Input.WriteLineAsync(JsonSerializer.Serialize(request)).WaitAsync(deadline.Token);
            var line = await _worker.Output.ReadLineAsync(deadline.Token);
            if (line is null || line.Length > 2 * 1024 * 1024)
                throw new AutomationException("IPA_RUNTIME_UNAVAILABLE", LocalizationService.Get("IpaRuntimeUnavailable"));
            using var response = JsonDocument.Parse(line);
            var root = response.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                return root.GetProperty("result").Clone();
            var code = root.TryGetProperty("code", out var error) ? error.GetString() : null;
            var message = root.TryGetProperty("message", out var detail) ? detail.GetString() : null;
            throw new AutomationException(code ?? "IPA_SIGNING_FAILED", message ?? "");
        }
        catch (OperationCanceledException)
        {
            await StopWorkerAsync();
            if (!cancellation.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                throw new TimeoutException(LocalizationService.Get("IpaOperationTimedOut"));
            throw;
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            await StopWorkerAsync();
            throw new AutomationException("IPA_RUNTIME_UNAVAILABLE", LocalizationService.Get("IpaRuntimeUnavailable"));
        }
        finally { _gate.Release(); }
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer) > 0) { }
    }

    private async Task StopWorkerAsync()
    {
        if (_worker is not { } worker) return;
        _worker = null;
        try
        {
            worker.Input.Close();
            await worker.RetireAsync(TimeSpan.FromMilliseconds(250));
            if (_errors is not null) await _errors;
        }
        finally { worker.Dispose(); _errors = null; }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try
        {
            await StopWorkerAsync();
            try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        finally { _gate.Release(); _lifetime.Dispose(); }
    }
}
