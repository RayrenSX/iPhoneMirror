using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Services;

internal enum DeviceAction { Restart = 1, Shutdown = 2 }

internal static class DeviceActionService
{
    internal static bool CanExecute(DeviceViewModel device) => device.Ready &&
        !device.IsWireless && !device.IsMediaCast &&
        string.Equals(device.ConnectionType, "USB", StringComparison.OrdinalIgnoreCase);

    internal static ProcessStartInfo CreateStartInfo(string bridgePath, string udid, DeviceAction action)
    {
        if (string.IsNullOrWhiteSpace(udid) || udid.Any(c => !char.IsAsciiHexDigit(c) && c != '-') ||
            !udid.Any(char.IsAsciiHexDigit) || !Enum.IsDefined(action))
            throw new ArgumentException("Invalid device action target.");
        var start = new ProcessStartInfo(bridgePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(bridgePath))!,
        };
        foreach (var argument in new[] { "--device-action", action.ToString().ToLowerInvariant(), "--udid", udid, "--usb" })
            start.ArgumentList.Add(argument);
        // A power action never consumes a reverse-control USB recovery checkpoint.
        start.Environment.Remove("IPHONE_MIRROR_USB_MUX_RESUME");
        return start;
    }

    internal static async Task ExecuteAsync(string udid, DeviceAction action, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
        if (!File.Exists(path)) throw new FileNotFoundException(LocalizationService.Get("DeviceActionRuntimeMissing"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = OwnedBridgeProcess.Start(CreateStartInfo(path, udid, action));
        var output = process.Output.ReadToEndAsync();
        var errors = process.Error.ReadToEndAsync();
        try
        {
            await process.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.WaitAsync(timeout.Token).ConfigureAwait(false);
            var diagnostic = await errors.WaitAsync(timeout.Token).ConfigureAwait(false);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                JsonDocument report;
                try { report = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (report)
                {
                    var root = report.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var type) ||
                        type.ValueKind != JsonValueKind.String || type.GetString() != "device_action_result") continue;
                    if (process.Process.ExitCode == 0 && root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True &&
                        root.TryGetProperty("udid", out var target) && target.ValueKind == JsonValueKind.String &&
                        DeviceViewModel.UdidEquals(target.GetString()?.Replace("-", ""), udid.Replace("-", "")) &&
                        root.TryGetProperty("action", out var reportedAction) && reportedAction.ValueKind == JsonValueKind.String &&
                        reportedAction.GetString() == action.ToString().ToLowerInvariant()) return;
                    var detail = root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;
                    throw new InvalidOperationException(detail ?? LocalizationService.Get("DeviceActionNoAcknowledgement"));
                }
            }
            DiagnosticLogger.Warning("device_action", "unacknowledged", ("exit_code", process.Process.ExitCode),
                ("diagnostic", diagnostic.Length > 600 ? diagnostic[..600] : diagnostic));
            throw new InvalidOperationException(LocalizationService.Get("DeviceActionNoAcknowledgement"));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException(LocalizationService.Get("DeviceActionNoAcknowledgement")); }
        finally
        {
            process.Input.Close();
            await process.RetireAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
        }
    }
}
