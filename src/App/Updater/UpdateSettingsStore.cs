using System.IO;
using System.Text.Json;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Updater;

public enum AppTheme
{
    System,
    Dark,
    Light,
}

internal sealed class UpdateSettings
{
    public bool CheckOnStartup { get; set; } = true;
    public bool AutoDownload { get; set; }
    public bool NotifyStableReleases { get; set; } = true;
    public bool NotifyPrereleaseReleases { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;

    internal UpdateSettings Clone() => new()
    {
        CheckOnStartup = CheckOnStartup,
        AutoDownload = AutoDownload,
        NotifyStableReleases = NotifyStableReleases,
        NotifyPrereleaseReleases = NotifyPrereleaseReleases,
        Theme = Theme,
    };
}

internal sealed class UpdateSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _path;

    internal static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror");

    internal UpdateSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(UserDataDirectory, "settings.json");
    }

    internal UpdateSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UpdateSettings();
            return JsonSerializer.Deserialize<UpdateSettings>(
                File.ReadAllText(_path), JsonOptions) ?? new UpdateSettings();
        }
        catch (Exception error) when (error is JsonException or IOException or
                                      UnauthorizedAccessException)
        {
            DiagnosticLogger.Exception("settings", "load_failed", error,
                ("file", Path.GetFileName(_path)));
            return new UpdateSettings();
        }
    }

    internal void Save(UpdateSettings settings)
    {
        var directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("Update settings path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or
                                          UnauthorizedAccessException)
            {
                DiagnosticLogger.Exception("settings", "temporary_cleanup_failed",
                    error, ("file", Path.GetFileName(temporary)));
            }
        }
    }
}
