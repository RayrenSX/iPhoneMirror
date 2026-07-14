using System.IO;
using System.ComponentModel;
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
}
