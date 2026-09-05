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

public enum ApplicationDisplayMode
{
    Complete,
    Lightweight,
}

internal sealed class UpdateSettings
{
    public bool CheckOnStartup { get; set; } = true;
    public bool AutoDownload { get; set; }
    public bool AllowMirrorFallback { get; set; } = true;
    public bool NotifyStableReleases { get; set; } = true;
    public bool NotifyPrereleaseReleases { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public ApplicationDisplayMode ApplicationDisplayMode { get; set; } =
        ApplicationDisplayMode.Complete;
    public WirelessReceiverBackend WirelessReceiverBackend { get; set; } =
        WirelessReceiverBackend.Original;
    public string WirelessDisplayProfileId { get; set; } = "1080p";
    public string WirelessReceiverName { get; set; } = WirelessReceiverConfiguration.DefaultReceiverName;
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
    public int BluetoothControlShortcutVirtualKey { get; set; } = 0x78;
    public int BluetoothControlShortcutModifiers { get; set; }
    public int BluetoothControlShortcutSchema { get; set; }
    public int BluetoothModeShortcutVirtualKey { get; set; }
    public int BluetoothModeShortcutModifiers { get; set; }
    public int WirelessModeShortcutVirtualKey { get; set; }
    public int WirelessModeShortcutModifiers { get; set; }
    public int WiredModeShortcutVirtualKey { get; set; }
    public int WiredModeShortcutModifiers { get; set; }
    public int BluetoothVolumeUpShortcutVirtualKey { get; set; }
    public int BluetoothVolumeUpShortcutModifiers { get; set; }
    public int BluetoothVolumeDownShortcutVirtualKey { get; set; }
    public int BluetoothVolumeDownShortcutModifiers { get; set; }
    public int BluetoothControlCenterShortcutVirtualKey { get; set; }
    public int BluetoothControlCenterShortcutModifiers { get; set; }
    public int BluetoothNotificationCenterShortcutVirtualKey { get; set; }
    public int BluetoothNotificationCenterShortcutModifiers { get; set; }
    public int BluetoothAppSwitcherShortcutVirtualKey { get; set; }
    public int BluetoothAppSwitcherShortcutModifiers { get; set; }
    public int BluetoothHomeShortcutVirtualKey { get; set; }
    public int BluetoothHomeShortcutModifiers { get; set; }
    public int BluetoothBackShortcutVirtualKey { get; set; }
    public int BluetoothBackShortcutModifiers { get; set; }
    public int BluetoothBossShortcutVirtualKey { get; set; } = 0x42;
    public int BluetoothBossShortcutModifiers { get; set; } = 0x0003;
    public int BluetoothLockScreenShortcutVirtualKey { get; set; }
    public int BluetoothLockScreenShortcutModifiers { get; set; }
    public int BluetoothDockShortcutVirtualKey { get; set; }
    public int BluetoothDockShortcutModifiers { get; set; }
    public int BluetoothSearchShortcutVirtualKey { get; set; }
    public int BluetoothSearchShortcutModifiers { get; set; }
    public int BluetoothKeyboardShortcutsShortcutVirtualKey { get; set; }
    public int BluetoothKeyboardShortcutsShortcutModifiers { get; set; }
    public int BluetoothAllWindowsShortcutVirtualKey { get; set; }
    public int BluetoothAllWindowsShortcutModifiers { get; set; }
    public int BluetoothPreviousAppShortcutVirtualKey { get; set; }
    public int BluetoothPreviousAppShortcutModifiers { get; set; }
    public int BluetoothNextAppShortcutVirtualKey { get; set; }
    public int BluetoothNextAppShortcutModifiers { get; set; }
    public int BluetoothFullScreenShortcutVirtualKey { get; set; }
    public int BluetoothFullScreenShortcutModifiers { get; set; }
    public int BluetoothQuickNoteShortcutVirtualKey { get; set; }
    public int BluetoothQuickNoteShortcutModifiers { get; set; }
    public int BluetoothSiriShortcutVirtualKey { get; set; }
    public int BluetoothSiriShortcutModifiers { get; set; }
    public int BluetoothShortcutSchema { get; set; }
    public int BluetoothHidReportMapVersion { get; set; }
    public int BluetoothHidReportMapAcknowledgedVersion { get; set; }
    public Dictionary<string, string> BluetoothControlDeviceBindings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal UpdateSettings Clone() => new()
    {
        CheckOnStartup = CheckOnStartup,
        AutoDownload = AutoDownload,
        AllowMirrorFallback = AllowMirrorFallback,
        NotifyStableReleases = NotifyStableReleases,
        NotifyPrereleaseReleases = NotifyPrereleaseReleases,
        Theme = Theme,
        ApplicationDisplayMode = ApplicationDisplayMode,
        WirelessReceiverBackend = WirelessReceiverBackend,
        WirelessDisplayProfileId = WirelessDisplayProfileId,
        WirelessReceiverName = WirelessReceiverName,
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
        BluetoothControlShortcutVirtualKey = BluetoothControlShortcutVirtualKey,
        BluetoothControlShortcutModifiers = BluetoothControlShortcutModifiers,
        BluetoothControlShortcutSchema = BluetoothControlShortcutSchema,
        BluetoothModeShortcutVirtualKey = BluetoothModeShortcutVirtualKey,
        BluetoothModeShortcutModifiers = BluetoothModeShortcutModifiers,
        WirelessModeShortcutVirtualKey = WirelessModeShortcutVirtualKey,
        WirelessModeShortcutModifiers = WirelessModeShortcutModifiers,
        WiredModeShortcutVirtualKey = WiredModeShortcutVirtualKey,
        WiredModeShortcutModifiers = WiredModeShortcutModifiers,
        BluetoothVolumeUpShortcutVirtualKey = BluetoothVolumeUpShortcutVirtualKey,
        BluetoothVolumeUpShortcutModifiers = BluetoothVolumeUpShortcutModifiers,
        BluetoothVolumeDownShortcutVirtualKey = BluetoothVolumeDownShortcutVirtualKey,
        BluetoothVolumeDownShortcutModifiers = BluetoothVolumeDownShortcutModifiers,
        BluetoothControlCenterShortcutVirtualKey = BluetoothControlCenterShortcutVirtualKey,
        BluetoothControlCenterShortcutModifiers = BluetoothControlCenterShortcutModifiers,
        BluetoothNotificationCenterShortcutVirtualKey = BluetoothNotificationCenterShortcutVirtualKey,
        BluetoothNotificationCenterShortcutModifiers = BluetoothNotificationCenterShortcutModifiers,
        BluetoothAppSwitcherShortcutVirtualKey = BluetoothAppSwitcherShortcutVirtualKey,
        BluetoothAppSwitcherShortcutModifiers = BluetoothAppSwitcherShortcutModifiers,
        BluetoothHomeShortcutVirtualKey = BluetoothHomeShortcutVirtualKey,
        BluetoothHomeShortcutModifiers = BluetoothHomeShortcutModifiers,
        BluetoothBackShortcutVirtualKey = BluetoothBackShortcutVirtualKey,
        BluetoothBackShortcutModifiers = BluetoothBackShortcutModifiers,
        BluetoothBossShortcutVirtualKey = BluetoothBossShortcutVirtualKey,
        BluetoothBossShortcutModifiers = BluetoothBossShortcutModifiers,
        BluetoothLockScreenShortcutVirtualKey = BluetoothLockScreenShortcutVirtualKey,
        BluetoothLockScreenShortcutModifiers = BluetoothLockScreenShortcutModifiers,
        BluetoothDockShortcutVirtualKey = BluetoothDockShortcutVirtualKey,
        BluetoothDockShortcutModifiers = BluetoothDockShortcutModifiers,
        BluetoothSearchShortcutVirtualKey = BluetoothSearchShortcutVirtualKey,
        BluetoothSearchShortcutModifiers = BluetoothSearchShortcutModifiers,
        BluetoothKeyboardShortcutsShortcutVirtualKey = BluetoothKeyboardShortcutsShortcutVirtualKey,
        BluetoothKeyboardShortcutsShortcutModifiers = BluetoothKeyboardShortcutsShortcutModifiers,
        BluetoothAllWindowsShortcutVirtualKey = BluetoothAllWindowsShortcutVirtualKey,
        BluetoothAllWindowsShortcutModifiers = BluetoothAllWindowsShortcutModifiers,
        BluetoothPreviousAppShortcutVirtualKey = BluetoothPreviousAppShortcutVirtualKey,
        BluetoothPreviousAppShortcutModifiers = BluetoothPreviousAppShortcutModifiers,
        BluetoothNextAppShortcutVirtualKey = BluetoothNextAppShortcutVirtualKey,
        BluetoothNextAppShortcutModifiers = BluetoothNextAppShortcutModifiers,
        BluetoothFullScreenShortcutVirtualKey = BluetoothFullScreenShortcutVirtualKey,
        BluetoothFullScreenShortcutModifiers = BluetoothFullScreenShortcutModifiers,
        BluetoothQuickNoteShortcutVirtualKey = BluetoothQuickNoteShortcutVirtualKey,
        BluetoothQuickNoteShortcutModifiers = BluetoothQuickNoteShortcutModifiers,
        BluetoothSiriShortcutVirtualKey = BluetoothSiriShortcutVirtualKey,
        BluetoothSiriShortcutModifiers = BluetoothSiriShortcutModifiers,
        BluetoothShortcutSchema = BluetoothShortcutSchema,
        BluetoothHidReportMapVersion = BluetoothHidReportMapVersion,
        BluetoothHidReportMapAcknowledgedVersion = BluetoothHidReportMapAcknowledgedVersion,
        BluetoothControlDeviceBindings = new(BluetoothControlDeviceBindings,
            StringComparer.OrdinalIgnoreCase),
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
                BluetoothControlShortcutSchema = 1,
                BluetoothShortcutSchema = 6,
                BluetoothHidReportMapVersion = BluetoothHidProtocol.ReportMapVersion,
                BluetoothHidReportMapAcknowledgedVersion = BluetoothHidProtocol.ReportMapVersion,
            };
            var settings = JsonSerializer.Deserialize<UpdateSettings>(
                File.ReadAllText(_path), JsonOptions) ?? new UpdateSettings();
            var migrationChanged = false;
            if (!Enum.IsDefined(settings.ApplicationDisplayMode))
            {
                settings.ApplicationDisplayMode = ApplicationDisplayMode.Complete;
                migrationChanged = true;
            }
            if (!Enum.IsDefined(settings.WirelessReceiverBackend))
            {
                settings.WirelessReceiverBackend = WirelessReceiverBackend.Original;
                migrationChanged = true;
            }
            var normalizedProfileId = settings.WirelessDisplayProfileId?.Trim().ToLowerInvariant();
            if (!WirelessReceiverConfiguration.DisplayProfiles.Any(profile =>
                    string.Equals(profile.Id, normalizedProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                settings.WirelessDisplayProfileId = "1080p";
                migrationChanged = true;
            }
            else if (!string.Equals(settings.WirelessDisplayProfileId, normalizedProfileId,
                StringComparison.Ordinal))
            {
                settings.WirelessDisplayProfileId = normalizedProfileId!;
                migrationChanged = true;
            }
            var normalizedReceiverName = WirelessReceiverConfiguration.SanitizeReceiverName(
                settings.WirelessReceiverName);
            if (!string.Equals(settings.WirelessReceiverName, normalizedReceiverName,
                StringComparison.Ordinal))
            {
                settings.WirelessReceiverName = normalizedReceiverName;
                migrationChanged = true;
            }
            settings.BluetoothControlDeviceBindings = NormalizeBluetoothBindings(
                settings.BluetoothControlDeviceBindings);
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
            if (settings.BluetoothControlShortcutSchema < 1 ||
                !KeyboardShortcut.IsValid(settings.BluetoothControlShortcutModifiers,
                    settings.BluetoothControlShortcutVirtualKey))
            {
                settings.BluetoothControlShortcutVirtualKey = 0x78;
                settings.BluetoothControlShortcutModifiers = 0;
                settings.BluetoothControlShortcutSchema = 1;
            }
            if (settings.BluetoothShortcutSchema < 3)
            {
                // The previous release assigned Ctrl+Alt defaults to these actions.
                // Do not keep those implicit bindings after upgrading.
                settings.BluetoothControlCenterShortcutVirtualKey = 0;
                settings.BluetoothControlCenterShortcutModifiers = 0;
                settings.BluetoothNotificationCenterShortcutVirtualKey = 0;
                settings.BluetoothNotificationCenterShortcutModifiers = 0;
                settings.BluetoothAppSwitcherShortcutVirtualKey = 0;
                settings.BluetoothAppSwitcherShortcutModifiers = 0;
                settings.BluetoothHomeShortcutVirtualKey = 0;
                settings.BluetoothHomeShortcutModifiers = 0;
                settings.BluetoothShortcutSchema = 3;
            }
            if (settings.BluetoothShortcutSchema < 4)
            {
                settings.BluetoothKeyboardShortcutsShortcutVirtualKey = 0;
                settings.BluetoothKeyboardShortcutsShortcutModifiers = 0;
                settings.BluetoothAllWindowsShortcutVirtualKey = 0;
                settings.BluetoothAllWindowsShortcutModifiers = 0;
                settings.BluetoothPreviousAppShortcutVirtualKey = 0;
                settings.BluetoothPreviousAppShortcutModifiers = 0;
                settings.BluetoothNextAppShortcutVirtualKey = 0;
                settings.BluetoothNextAppShortcutModifiers = 0;
                settings.BluetoothFullScreenShortcutVirtualKey = 0;
                settings.BluetoothFullScreenShortcutModifiers = 0;
                settings.BluetoothQuickNoteShortcutVirtualKey = 0;
                settings.BluetoothQuickNoteShortcutModifiers = 0;
                settings.BluetoothSiriShortcutVirtualKey = 0;
                settings.BluetoothSiriShortcutModifiers = 0;
                settings.BluetoothShortcutSchema = 4;
            }
            if (settings.BluetoothShortcutSchema < 5)
            {
                settings.BluetoothBossShortcutVirtualKey = 0x42;
                settings.BluetoothBossShortcutModifiers = 0x0003;
                settings.BluetoothShortcutSchema = 5;
            }
            if (settings.BluetoothShortcutSchema < 6)
            {
                settings.BluetoothBackShortcutVirtualKey = 0;
                settings.BluetoothBackShortcutModifiers = 0;
                settings.BluetoothShortcutSchema = 6;
                migrationChanged = true;
            }
            else if (settings.BluetoothBackShortcutVirtualKey != 0 ||
                     settings.BluetoothBackShortcutModifiers != 0)
            {
                // Schema 6 removed the unsupported Back action. Clear stale
                // values even when a previous build already wrote schema 6.
                settings.BluetoothBackShortcutVirtualKey = 0;
                settings.BluetoothBackShortcutModifiers = 0;
                migrationChanged = true;
            }
            if (settings.BluetoothHidReportMapVersion <
                BluetoothHidProtocol.ReportMapVersion)
            {
                settings.BluetoothHidReportMapVersion =
                    BluetoothHidProtocol.ReportMapVersion;
                migrationChanged = true;
            }
            if (!KeyboardShortcut.IsValid(settings.BluetoothControlCenterShortcutModifiers,
                    settings.BluetoothControlCenterShortcutVirtualKey) ||
                !KeyboardShortcut.IsValid(settings.BluetoothNotificationCenterShortcutModifiers,
                    settings.BluetoothNotificationCenterShortcutVirtualKey) ||
                !KeyboardShortcut.IsValid(settings.BluetoothAppSwitcherShortcutModifiers,
                    settings.BluetoothAppSwitcherShortcutVirtualKey) ||
                !KeyboardShortcut.IsValid(settings.BluetoothHomeShortcutModifiers,
                    settings.BluetoothHomeShortcutVirtualKey) ||
                !KeyboardShortcut.IsValid(settings.BluetoothDockShortcutModifiers,
                    settings.BluetoothDockShortcutVirtualKey) ||
                !KeyboardShortcut.IsValid(settings.BluetoothSiriShortcutModifiers,
                    settings.BluetoothSiriShortcutVirtualKey))
            {
                settings.BluetoothControlCenterShortcutVirtualKey = 0;
                settings.BluetoothControlCenterShortcutModifiers = 0;
                settings.BluetoothNotificationCenterShortcutVirtualKey = 0;
                settings.BluetoothNotificationCenterShortcutModifiers = 0;
                settings.BluetoothAppSwitcherShortcutVirtualKey = 0;
                settings.BluetoothAppSwitcherShortcutModifiers = 0;
                settings.BluetoothHomeShortcutVirtualKey = 0;
                settings.BluetoothHomeShortcutModifiers = 0;
                settings.BluetoothDockShortcutVirtualKey = 0;
                settings.BluetoothDockShortcutModifiers = 0;
                settings.BluetoothSiriShortcutVirtualKey = 0;
                settings.BluetoothSiriShortcutModifiers = 0;
                settings.BluetoothShortcutSchema = 6;
            }
            if (!KeyboardShortcut.IsValid(settings.BluetoothBossShortcutModifiers,
                    settings.BluetoothBossShortcutVirtualKey))
            {
                settings.BluetoothBossShortcutVirtualKey = 0x42;
                settings.BluetoothBossShortcutModifiers = 0x0003;
                settings.BluetoothShortcutSchema = 6;
            }
            settings.BluetoothWheelSensitivity = Math.Clamp(
                double.IsFinite(settings.BluetoothWheelSensitivity)
                    ? settings.BluetoothWheelSensitivity : 100, 10, 1000);
            if (settings.BluetoothLandscapeMouseMode is < 1 or > 4)
                settings.BluetoothLandscapeMouseMode = 1;
            if (migrationChanged) TrySaveMigration(settings);
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

    private void TrySaveMigration(UpdateSettings settings)
    {
        try { Save(settings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Exception("settings", "migration_save_failed", error,
                ("file", Path.GetFileName(_path)));
        }
    }

    private static Dictionary<string, string> NormalizeBluetoothBindings(
        Dictionary<string, string>? bindings)
    {
        var normalized = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        if (bindings is null) return normalized;
        foreach (var pair in bindings) normalized[pair.Key] = pair.Value;
        return normalized;
    }
}
