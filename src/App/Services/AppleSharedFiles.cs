using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Services;

internal static class AppleSharedFiles
{
    internal static async Task<JsonElement> ExecuteAsync(string udid, bool wireless, object request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
        if (!File.Exists(path)) throw new FileNotFoundException(LocalizationService.Get("DeviceActionRuntimeMissing"));

        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        start.ArgumentList.Add("--shared-files");
        start.ArgumentList.Add("--udid");
        start.ArgumentList.Add(udid);
        start.ArgumentList.Add(wireless ? "--wireless" : "--usb");
        start.Environment.Remove("IPHONE_MIRROR_USB_MUX_RESUME");

        using var process = OwnedBridgeProcess.Start(start);
        var output = process.Output.ReadToEndAsync();
        var errors = process.Error.ReadToEndAsync();
        using var timeout = new CancellationTokenSource();
        // The bridge's operation timeout expires first so it can clean up an incomplete upload.
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        try
        {
            await process.Input.WriteLineAsync(JsonSerializer.Serialize(request)).WaitAsync(timeout.Token).ConfigureAwait(false);
            var exit = process.Process.WaitForExitAsync(timeout.Token);
            try { await exit.WaitAsync(cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                try
                {
                    await process.Input.WriteAsync('\u0003').WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    await exit.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (IOException) when (process.Process.HasExited) { }
                catch (TimeoutException)
                {
                    throw new OperationCanceledException(cancellation);
                }
            }
            var text = await output.WaitAsync(timeout.Token).ConfigureAwait(false);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                JsonDocument report;
                try { report = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (report)
                {
                    var root = report.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var type) ||
                        type.ValueKind != JsonValueKind.String || type.GetString() != "shared_files_result") continue;
                    if (process.Process.ExitCode == 0 && root.TryGetProperty("result", out var result)) return result.Clone();
                    var code = root.TryGetProperty("code", out var error) ? error.GetString() : null;
                    var message = root.TryGetProperty("message", out var detail) ? detail.GetString() : null;
                    if (code == "FILE_TRANSFER_CANCELLED") throw new OperationCanceledException(cancellation);
                    throw new AutomationException(code ?? "FILE_IMPORT_FAILED",
                        message ?? LocalizationService.Get("DeviceHubPanelFileImportFailed"));
                }
            }
            DiagnosticLogger.Warning("shared_files", "bridge_result_missing", ("exit_code", process.Process.ExitCode));
            throw new InvalidOperationException(LocalizationService.Get("PreviewFilesConnectionUnavailable"));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(LocalizationService.Get("PreviewFilesConnectionUnavailable"));
        }
        finally
        {
            try { process.Input.Close(); }
            catch (IOException) { }
            await process.RetireAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
        }
    }
}
