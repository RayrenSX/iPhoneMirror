using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal enum WirelessReceiverBackend
{
    Original,
    UxPlay,
}

internal sealed class WirelessReceiverBackendOption(
    WirelessReceiverBackend backend, string resourceKey) : INotifyPropertyChanged
{
    internal WirelessReceiverBackend Backend { get; } = backend;
    public string Label => LocalizationService.Get(resourceKey);
    public override string ToString() => Label;
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this,
        new PropertyChangedEventArgs(nameof(Label)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class WirelessReceiverRuntime(
    WirelessReceiverBackend backend, string executableName, string overrideEnvironmentVariable,
    string relativeDirectory, IReadOnlyList<string> requiredRuntimeFiles,
    bool requiresBundleIntegrity)
{
    internal WirelessReceiverBackend Backend { get; } = backend;
    internal string ExecutableName { get; } = executableName;
    internal string OverrideEnvironmentVariable { get; } = overrideEnvironmentVariable;
    internal string RelativeDirectory { get; } = relativeDirectory;
    internal IReadOnlyList<string> RequiredRuntimeFiles { get; } = requiredRuntimeFiles;
    internal bool RequiresBundleIntegrity { get; } = requiresBundleIntegrity;
}

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
    internal const string UxPlayExecutableName = "iPhoneMirror.UxPlayHost.exe";
    private static readonly string[] OriginalRequiredRuntimeFiles =
    [
        "airplay2dll.dll",
        "avcodec-58.dll",
        "avutil-56.dll",
        "dnssd.dll",
        "swresample-3.dll",
        "swscale-5.dll",
    ];
    private static readonly string[] UxPlayRequiredRuntimeFiles =
    [
        "uxplay.exe",
        "LICENSE",
        "SOURCE.md",
        "bin\\libgstreamer-1.0-0.dll",
        "bin\\libgstbase-1.0-0.dll",
        "bin\\libgstvideo-1.0-0.dll",
        "bin\\libgstaudio-1.0-0.dll",
        "bin\\libgstapp-1.0-0.dll",
        "bin\\libgstpbutils-1.0-0.dll",
        "bin\\libgsttag-1.0-0.dll",
        "bin\\libglib-2.0-0.dll",
        "bin\\libgobject-2.0-0.dll",
        "bin\\libplist-2.0.dll",
        "bin\\libgcc_s_seh-1.dll",
        "bin\\dnssd.dll",
        "lib\\gstreamer-1.0\\libgstapp.dll",
        "lib\\gstreamer-1.0\\libgstcoreelements.dll",
        "lib\\gstreamer-1.0\\libgstaudioconvert.dll",
        "lib\\gstreamer-1.0\\libgstaudioresample.dll",
        "lib\\gstreamer-1.0\\libgstvideoconvertscale.dll",
        "lib\\gstreamer-1.0\\libgsty4m.dll",
        "lib\\gstreamer-1.0\\libgstvideoparsersbad.dll",
        "lib\\gstreamer-1.0\\libgstlibav.dll",
    ];
    private static readonly WirelessReceiverRuntime OriginalRuntime = new(
        WirelessReceiverBackend.Original, ExecutableName, "IPHONE_MIRROR_AIRPLAY_HOST",
        "Wireless", OriginalRequiredRuntimeFiles, requiresBundleIntegrity: true);
    private static readonly WirelessReceiverRuntime UxPlayRuntime = new(
        WirelessReceiverBackend.UxPlay, UxPlayExecutableName, "IPHONE_MIRROR_UXPLAY_HOST",
        Path.Combine("Wireless", "UxPlay"), UxPlayRequiredRuntimeFiles,
        requiresBundleIntegrity: false);
    internal static IReadOnlyList<WirelessReceiverBackendOption> BackendOptions { get; } =
    [
        new(WirelessReceiverBackend.Original, "WirelessBackendOriginal"),
        new(WirelessReceiverBackend.UxPlay, "WirelessBackendUxPlay"),
    ];
    internal static IReadOnlyList<WirelessDisplayProfile> DisplayProfiles { get; } =
    [
        new("maximum", "WirelessProfileMaximum", 5120, 2880, 60),
        new("1080p", "WirelessProfile1080p", 1920, 1080, 60),
        new("720p", "WirelessProfile720p", 1280, 720, 30),
        new("540p", "WirelessProfile540p", 960, 540, 30),
    ];
    internal static WirelessDisplayProfile DefaultDisplayProfile => DisplayProfiles[1];

    internal static WirelessReceiverBackend NormalizeBackend(
        WirelessReceiverBackend backend) => Enum.IsDefined(backend)
        ? backend
        : WirelessReceiverBackend.Original;

    internal static WirelessReceiverRuntime GetRuntime(WirelessReceiverBackend backend) =>
        NormalizeBackend(backend) == WirelessReceiverBackend.UxPlay
            ? UxPlayRuntime
            : OriginalRuntime;

    internal static WirelessReceiverBackendOption GetBackendOption(
        WirelessReceiverBackend backend)
    {
        var normalized = NormalizeBackend(backend);
        return BackendOptions.First(option => option.Backend == normalized);
    }

    internal static bool SupportsMediaCast(WirelessReceiverBackend backend) =>
        NormalizeBackend(backend) == WirelessReceiverBackend.Original;

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

    internal static string? FindExecutable(string baseDirectory, string? overridePath = null) =>
        FindExecutable(WirelessReceiverBackend.Original, baseDirectory, overridePath);

    internal static string? FindExecutable(WirelessReceiverBackend backend,
        string baseDirectory, string? overridePath = null)
    {
        var runtime = GetRuntime(backend);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
            if (File.Exists(expanded)) return Path.GetFullPath(expanded);
        }

        var candidates = runtime.Backend == WirelessReceiverBackend.Original
            ? new[]
            {
                Path.Combine(baseDirectory, runtime.RelativeDirectory, runtime.ExecutableName),
                Path.Combine(baseDirectory, runtime.ExecutableName),
            }
            : new[]
        {
            Path.Combine(baseDirectory, runtime.RelativeDirectory, runtime.ExecutableName),
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
    private readonly object _probeLock = new();
    private readonly Dictionary<WirelessReceiverBackend, WirelessRuntimeProbeResult>
        _successfulProbes = [];

    internal string? ExecutablePath => GetExecutablePath(WirelessReceiverBackend.Original);

    internal string? GetExecutablePath(WirelessReceiverBackend backend)
    {
        var runtime = WirelessReceiverConfiguration.GetRuntime(backend);
        return WirelessReceiverConfiguration.FindExecutable(runtime.Backend,
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable(runtime.OverrideEnvironmentVariable));
    }

    internal string? GetExecutablePath(WirelessReceiverBackend backend,
        string baseDirectory, string? overridePath = null) =>
        WirelessReceiverConfiguration.FindExecutable(backend, baseDirectory, overridePath);

    internal bool IsAvailable => IsBackendAvailable(WirelessReceiverBackend.Original);

    internal bool IsBackendAvailable(WirelessReceiverBackend backend)
    {
        var runtime = WirelessReceiverConfiguration.GetRuntime(backend);
        var executable = GetExecutablePath(runtime.Backend);
        if (executable is null) return false;
        var directory = Path.GetDirectoryName(executable);
        return directory is not null &&
            runtime.RequiredRuntimeFiles.All(file => File.Exists(Path.Combine(directory, file))) &&
            (!runtime.RequiresBundleIntegrity ||
                RuntimeBinaryIntegrity.VerifyWirelessDirectory(directory, out _));
    }

    internal WirelessRuntimeProbeResult ProbeRuntime() =>
        ProbeRuntime(WirelessReceiverBackend.Original);

    internal WirelessRuntimeProbeResult ProbeRuntime(WirelessReceiverBackend backend)
    {
        var runtime = WirelessReceiverConfiguration.GetRuntime(backend);
        lock (_probeLock)
        {
            if (_successfulProbes.TryGetValue(runtime.Backend, out var cached)) return cached;
            var executable = GetExecutablePath(runtime.Backend);
            if (executable is null)
                return new(WirelessRuntimeProbeStatus.LoadFailed, -1);
            var directory = Path.GetDirectoryName(executable);
            var integrityFailure = string.Empty;
            if (directory is null ||
                (runtime.RequiresBundleIntegrity &&
                    !RuntimeBinaryIntegrity.VerifyWirelessDirectory(directory, out integrityFailure)))
            {
                DiagnosticLogger.Info("wireless", "runtime_hash_mismatch",
                    ("backend", runtime.Backend.ToString()),
                    ("detail", integrityFailure ?? "runtime directory is unavailable"));
                return new(WirelessRuntimeProbeStatus.CodeIntegrityBlocked, 40);
            }
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
                if (result.Success) _successfulProbes[runtime.Backend] = result;
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
