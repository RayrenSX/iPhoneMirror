using System.IO;

namespace IPhoneMirror.App.Services;

internal static class PendingRecordingStore
{
    private const string PartialSuffix = ".partial.mp4";

    internal static string DefaultDirectory => Path.Combine(
        Path.GetTempPath(), "iPhoneMirror", "PendingRecordings");

    internal static string CreatePath()
    {
        Directory.CreateDirectory(DefaultDirectory);
        return Path.Combine(DefaultDirectory,
            $"iPhoneMirror_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mp4");
    }

    internal static string CreateStagingPath(string completedPath)
    {
        var directory = Path.GetDirectoryName(completedPath);
        var name = Path.GetFileNameWithoutExtension(completedPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A complete recording path is required.",
                nameof(completedPath));
        return Path.Combine(directory, name + PartialSuffix);
    }

    internal static string? FindLatest(string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (!Directory.Exists(directory)) return null;
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly)
                .Where(file => file.Length > 0 &&
                    !file.Name.EndsWith(PartialSuffix,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
