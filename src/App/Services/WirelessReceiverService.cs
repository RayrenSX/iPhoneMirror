using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal sealed class WirelessDisplayProfile(
    string id, string resourceKey, uint width, uint height, uint frameRate) :
    INotifyPropertyChanged
{
    internal string Id { get; } = id;
    internal uint Width { get; } = width;
    internal uint Height { get; } = height;
    internal uint FrameRate { get; } = frameRate;
    public string Label => LocalizationService.Get(resourceKey);
    public override string ToString() => Label;
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this,
        new PropertyChangedEventArgs(nameof(Label)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal static class WirelessReceiverConfiguration
{
    internal const string DefaultReceiverName = "iPhoneMirror AirPlay";
    internal const string ExecutableName = "iPhoneMirror.WirelessHost.exe";
    internal static IReadOnlyList<WirelessDisplayProfile> DisplayProfiles { get; } =
    [
        new("maximum", "WirelessProfileMaximum", 5120, 2880, 60),
        new("1080p", "WirelessProfile1080p", 1920, 1080, 60),
        new("720p", "WirelessProfile720p", 1280, 720, 30),
        new("540p", "WirelessProfile540p", 960, 540, 30),
    ];
    internal static WirelessDisplayProfile DefaultDisplayProfile => DisplayProfiles[1];

    internal static bool RequiresOriginalQualityWarning(WirelessDisplayProfile profile) =>
        string.Equals(profile.Id, "maximum", StringComparison.Ordinal);

    internal static bool IsSupportedDisplayProfile(uint width, uint height, uint frameRate) =>
        DisplayProfiles.Any(profile => profile.Width == width && profile.Height == height &&
            profile.FrameRate == frameRate);

    internal static string SanitizeReceiverName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultReceiverName;
        var normalized = new string(value.Trim()
            .Where(character => character is >= ' ' and <= '~' &&
                character is not '[' and not ']' and not ';' and not '"')
            .Take(63)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(normalized) ? DefaultReceiverName : normalized;
    }

    internal static string? FindExecutable(string baseDirectory, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
            if (File.Exists(expanded)) return Path.GetFullPath(expanded);
        }

        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Wireless", ExecutableName),
            Path.Combine(baseDirectory, ExecutableName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

internal enum WirelessRuntimeProbeStatus
{
    Ready,
    CodeIntegrityBlocked,
    Incompatible,
    LoadFailed,
    TimedOut,
}

internal readonly record struct WirelessRuntimeProbeResult(
    WirelessRuntimeProbeStatus Status, int ErrorCode)
{
    internal bool Success => Status == WirelessRuntimeProbeStatus.Ready;

    internal static WirelessRuntimeProbeResult FromExitCode(int exitCode) => exitCode switch
    {
        0 => new(WirelessRuntimeProbeStatus.Ready, 0),
        40 => new(WirelessRuntimeProbeStatus.CodeIntegrityBlocked, exitCode),
        42 => new(WirelessRuntimeProbeStatus.Incompatible, exitCode),
        _ => new(WirelessRuntimeProbeStatus.LoadFailed, exitCode),
    };
}

internal sealed class WirelessReceiverService
{
    private static readonly string[] RequiredRuntimeFiles =
    [
        "airplay2dll.dll",
        "avcodec-58.dll",
        "avutil-56.dll",
        "dnssd.dll",
        "swresample-3.dll",
        "swscale-5.dll",
    ];
    private readonly object _probeLock = new();
    private WirelessRuntimeProbeResult? _successfulProbe;

    internal string? ExecutablePath => WirelessReceiverConfiguration.FindExecutable(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("IPHONE_MIRROR_AIRPLAY_HOST"));

    internal bool IsAvailable
    {
        get
        {
            var executable = ExecutablePath;
            if (executable is null) return false;
            var directory = Path.GetDirectoryName(executable);
            return directory is not null && RequiredRuntimeFiles.All(file =>
                File.Exists(Path.Combine(directory, file)));
        }
    }

    internal WirelessRuntimeProbeResult ProbeRuntime()
    {
        lock (_probeLock)
        {
            if (_successfulProbe is { } cached) return cached;
            var executable = ExecutablePath;
            if (executable is null)
                return new(WirelessRuntimeProbeStatus.LoadFailed, -1);
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("--check-runtime");
                using var process = Process.Start(start);
                if (process is null)
                    return new(WirelessRuntimeProbeStatus.LoadFailed, -1);
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception error)
                    {
                        DiagnosticLogger.ExceptionOnce("wireless-probe-kill",
                            "wireless", "runtime_probe_kill_failed", error);
                    }
                    return new(WirelessRuntimeProbeStatus.TimedOut, -1);
                }
                var result = WirelessRuntimeProbeResult.FromExitCode(process.ExitCode);
                if (result.Success) _successfulProbe = result;
                return result;
            }
            catch (Win32Exception error) when (IsCodeIntegrityError(error.NativeErrorCode))
            {
                DiagnosticLogger.ExceptionOnce("wireless-code-integrity", "wireless",
                    "runtime_probe_code_integrity_blocked", error,
                    ("native_error", error.NativeErrorCode));
                return new(WirelessRuntimeProbeStatus.CodeIntegrityBlocked,
                    error.NativeErrorCode);
            }
            catch (Win32Exception error)
            {
                DiagnosticLogger.ExceptionOnce("wireless-load", "wireless",
                    "runtime_probe_load_failed", error,
                    ("native_error", error.NativeErrorCode));
                return new(WirelessRuntimeProbeStatus.LoadFailed, error.NativeErrorCode);
            }
            catch (Exception error)
            {
                DiagnosticLogger.ExceptionOnce("wireless-probe", "wireless",
                    "runtime_probe_failed", error);
                return new(WirelessRuntimeProbeStatus.LoadFailed, -1);
            }
        }
    }

    internal static bool IsCodeIntegrityError(int errorCode) =>
        errorCode is 577 or 1260 ||
        errorCode is >= 4550 and <= 4559 ||
        errorCode is >= 4580 and <= 4583;

    internal static string DescribeProbeFailure(WirelessRuntimeProbeResult result) =>
        result.Status switch
        {
            WirelessRuntimeProbeStatus.CodeIntegrityBlocked =>
                LocalizationService.Get("WirelessRuntimeCodeIntegrityBlocked"),
            WirelessRuntimeProbeStatus.Incompatible =>
                LocalizationService.Get("WirelessRuntimeIncompatible"),
            WirelessRuntimeProbeStatus.TimedOut =>
                LocalizationService.Get("WirelessRuntimeProbeTimedOut"),
            _ => LocalizationService.Format("WirelessRuntimeLoadFailedFormat",
                result.ErrorCode),
        };
}
