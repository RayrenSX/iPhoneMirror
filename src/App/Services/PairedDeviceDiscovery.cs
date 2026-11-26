using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace IPhoneMirror.App.Services;

internal sealed record PairedNetworkDevice(string Udid, string? Name, string? ProductType, string? OsVersion);

// The UI consumes a snapshot. LAN browsing and pair verification never hold
// the native core gate or delay a device refresh/capture status tick.
internal sealed class PairedDeviceDiscovery
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (PairedNetworkDevice Device, long Seen)> _online =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _previousExclusions = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private Task? _pending;
    private long _lastStarted;
    private bool _started;
    internal PairedDeviceDiscovery(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    internal static string Key(string udid) => udid.Replace("-", "").ToLowerInvariant();

    internal void ObserveVerifiedDevices(IEnumerable<PairedNetworkDevice> devices, CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            if (cancellation.IsCancellationRequested) return;
            foreach (var device in devices.Take(128))
                if (!string.IsNullOrWhiteSpace(device.Udid) && device.Udid.Length <= 64 &&
                    device.Udid.All(value => char.IsAsciiHexDigit(value) || value == '-'))
                    _online[Key(device.Udid)] = (device, _clock.GetTimestamp());
        }
    }

    internal IReadOnlyList<PairedNetworkDevice> SnapshotAndRefresh(IEnumerable<string> exclusions,
        CancellationToken cancellation)
    {
        var excluded = exclusions.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
        var excludedKeys = excluded.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_previousExclusions.Any(key => !excludedKeys.Contains(key)))
                _started = false; // Resume immediately, including phones first seen over USB.
            foreach (var key in _online.Keys.ToArray())
            {
                // A USB connection or an owned live session pauses probing;
                // it must not erase the last verified wireless identity.
                if (excludedKeys.Contains(key)) continue;
                if (_previousExclusions.Contains(key))
                {
                    // Hand the existing card back to LAN discovery without a
                    // remove/re-add cycle while the next verification runs.
                    _online[key] = (_online[key].Device, _clock.GetTimestamp());
                }
                else if (_clock.GetElapsedTime(_online[key].Seen) > TimeSpan.FromSeconds(30))
                    _online.Remove(key);
            }
            _previousExclusions.Clear();
            _previousExclusions.UnionWith(excludedKeys);
            if (!cancellation.IsCancellationRequested && (_pending is null || _pending.IsCompleted) &&
                (!_started || _clock.GetElapsedTime(_lastStarted) >= TimeSpan.FromSeconds(10)))
            {
                _started = true;
                _lastStarted = _clock.GetTimestamp();
                _pending = Task.Run(() => RefreshAsync(excluded, cancellation), cancellation);
            }
            return _online.Where(pair => !excludedKeys.Contains(pair.Key)).Select(pair => pair.Value.Device).ToArray();
        }
    }

    private async Task RefreshAsync(string[] exclusions, CancellationToken cancellation)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
        if (!File.Exists(path)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            };
            start.ArgumentList.Add("--discover-paired-devices");
            start.Environment.Remove("IPHONE_MIRROR_USB_MUX_RESUME");
            using var process = OwnedBridgeProcess.Start(start);
            var output = process.Output.ReadToEndAsync(timeout.Token);
            var errors = process.Error.ReadToEndAsync(timeout.Token);
            await process.Input.WriteLineAsync(JsonSerializer.Serialize(new { excludeUdids = exclusions }));
            process.Input.Close();
            await process.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            _ = await errors.ConfigureAwait(false);
            if (process.Process.ExitCode != 0) throw new IOException("Paired LAN discovery failed.");
            using var report = JsonDocument.Parse(text);
            if (report.RootElement.GetProperty("event").GetString() != "paired_devices")
                throw new InvalidDataException("Invalid paired discovery result.");
            var devices = report.RootElement.GetProperty("devices").Deserialize<PairedNetworkDevice[]>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            // One missed multicast round must not make a device card blink.
            ObserveVerifiedDevices(devices, cancellation);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DiagnosticLogger.ExceptionOnce("paired-lan-discovery", "devices", "paired_lan_discovery_failed", error);
        }
    }
}
