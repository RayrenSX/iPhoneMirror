using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal sealed record CompactLaunchOptions(bool Enabled, string? DeviceId)
{
    internal static CompactLaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        var enabled = false;
        string? device = null;
        for (var index = 0; index < arguments.Count; ++index)
        {
            if (arguments[index] == "--compact") enabled = true;
            else if (arguments[index] == "--device")
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) ||
                    arguments[index].StartsWith("--", StringComparison.Ordinal) || device is not null)
                    throw new ArgumentException(LocalizationService.Get("CompactLaunchInvalidArguments"));
                device = arguments[index];
                enabled = true;
            }
        }
        return new(enabled, device);
    }

    internal int SelectDevice(IReadOnlyList<string> availableIds)
    {
        if (DeviceId is null) return availableIds.Count == 1 ? 0 : -1;
        for (var index = 0; index < availableIds.Count; ++index)
            if (string.Equals(DeviceId, availableIds[index], StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { ++slashes; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static string CreateDesktopShortcut(string deviceId, string displayName)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop)) throw new DirectoryNotFoundException("Desktop directory is unavailable.");
        var safeName = new string(displayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .Take(60).ToArray()).Trim().TrimEnd('.');
        var stem = "iPhoneMirror - " + (safeName.Length == 0 ? "iPhone" : safeName);
        var destination = Path.Combine(desktop, stem + ".lnk");
        for (var suffix = 2; File.Exists(destination); ++suffix)
            destination = Path.Combine(desktop, $"{stem} ({suffix}).lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)!;
            shortcut = ((dynamic)shell).CreateShortcut(destination);
            dynamic link = shortcut;
            link.TargetPath = executable;
            link.WorkingDirectory = Path.GetDirectoryName(executable)!;
            link.Arguments = "--compact --device " + QuoteArgument(deviceId);
            link.Description = displayName;
            link.IconLocation = executable + ",0";
            link.Save();
            return destination;
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
