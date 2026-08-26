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
    public bool AllowMirrorFallback { get; set; } = true;
    public bool NotifyStableReleases { get; set; } = true;
    public bool NotifyPrereleaseReleases { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string Language { get; set; } = "system";
    public double BluetoothMouseSensitivity { get; set; } = 500;
    public int BluetoothMouseSensitivitySchema { get; set; }
    public int BluetoothLandscapeMouseOrientationTurns { get; set; } = 1;
    public int BluetoothMouseOrientationSchema { get; set; }
    public double BluetoothWheelSensitivity { get; set; } = 1000;
    public int BluetoothLandscapeMouseMode { get; set; } = 1;
    public int BluetoothMouseSettingsSchema { get; set; }
    public int BluetoothPortraitMouseDirection { get; set; }
    public int BluetoothLandscapeMouseDirection { get; set; } = 1;
    public bool BluetoothMouseReverseHorizontal { get; set; }
    public bool BluetoothMouseReverseVertical { get; set; }
    public int BluetoothMouseDirectionSchema { get; set; }

    internal UpdateSettings Clone() => new()
    {
        CheckOnStartup = CheckOnStartup,
        AutoDownload = AutoDownload,
        AllowMirrorFallback = AllowMirrorFallback,
        NotifyStableReleases = NotifyStableReleases,
        NotifyPrereleaseReleases = NotifyPrereleaseReleases,
        Theme = Theme,
        Language = Language,
        BluetoothMouseSensitivity = BluetoothMouseSensitivity,
        BluetoothMouseSensitivitySchema = BluetoothMouseSensitivitySchema,
        BluetoothLandscapeMouseOrientationTurns = BluetoothLandscapeMouseOrientationTurns,
        BluetoothMouseOrientationSchema = BluetoothMouseOrientationSchema,
        BluetoothWheelSensitivity = BluetoothWheelSensitivity,
        BluetoothLandscapeMouseMode = BluetoothLandscapeMouseMode,
        BluetoothMouseSettingsSchema = BluetoothMouseSettingsSchema,
        BluetoothPortraitMouseDirection = BluetoothPortraitMouseDirection,
        BluetoothLandscapeMouseDirection = BluetoothLandscapeMouseDirection,
        BluetoothMouseReverseHorizontal = BluetoothMouseReverseHorizontal,
        BluetoothMouseReverseVertical = BluetoothMouseReverseVertical,
        BluetoothMouseDirectionSchema = BluetoothMouseDirectionSchema,
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
            if (!File.Exists(_path)) return new UpdateSettings
            {
                BluetoothMouseSensitivitySchema = 2,
                BluetoothMouseOrientationSchema = 1,
                BluetoothMouseSettingsSchema = 1,
                BluetoothMouseDirectionSchema = 1,
            };
            var settings = JsonSerializer.Deserialize<UpdateSettings>(
                File.ReadAllText(_path), JsonOptions) ?? new UpdateSettings();
            if (settings.BluetoothMouseSensitivitySchema < 1)
            {
                settings.BluetoothMouseSensitivity = 500;
                settings.BluetoothMouseSensitivitySchema = 2;
            }
            if (settings.BluetoothMouseOrientationSchema < 1 ||
                settings.BluetoothLandscapeMouseOrientationTurns is not 1 and not 3)
            {
                settings.BluetoothLandscapeMouseOrientationTurns = 1;
                settings.BluetoothMouseOrientationSchema = 1;
            }
            if (settings.BluetoothMouseSettingsSchema < 1)
            {
                settings.BluetoothWheelSensitivity = 1000;
                settings.BluetoothLandscapeMouseMode =
                    settings.BluetoothLandscapeMouseOrientationTurns == 3 ? 2 : 1;
                settings.BluetoothMouseSettingsSchema = 1;
            }
            if (settings.BluetoothMouseDirectionSchema < 1)
            {
                // Migrate the previous four landscape modes. The old mode
                // encoded a right/left quarter-turn plus one axis reversal.
                (settings.BluetoothLandscapeMouseDirection,
                    settings.BluetoothMouseReverseHorizontal,
                    settings.BluetoothMouseReverseVertical) =
                    settings.BluetoothLandscapeMouseMode switch
                    {
                        2 => (3, false, false),
                        3 => (3, false, true),
                        4 => (1, false, true),
                        _ => (1, false, false),
                    };
                settings.BluetoothPortraitMouseDirection = 0;
                settings.BluetoothMouseDirectionSchema = 1;
            }
            if (settings.BluetoothPortraitMouseDirection is < 0 or > 3)
                settings.BluetoothPortraitMouseDirection = 0;
            if (settings.BluetoothLandscapeMouseDirection is < 0 or > 3)
                settings.BluetoothLandscapeMouseDirection = 1;
            settings.BluetoothWheelSensitivity = Math.Clamp(
                double.IsFinite(settings.BluetoothWheelSensitivity)
                    ? settings.BluetoothWheelSensitivity : 100, 10, 1000);
            if (settings.BluetoothLandscapeMouseMode is < 1 or > 4)
                settings.BluetoothLandscapeMouseMode = 1;
            return settings;
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

    internal void Update(Action<UpdateSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var settings = Load();
        update(settings);
        Save(settings);
    }
}
