using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Models;

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
    Tray,
}

[Flags]
internal enum SourceCardFields
{
    Icon = 1,
    Name = 2,
    Model = 4,
    SystemVersion = 8,
    Status = 16,
    Transport = 32,
    Default = Icon | Name | Status | Transport,
    All = Default | Model | SystemVersion,
}

internal sealed class UpdateSettings
{
    public bool PhoneFrameEnabled { get; set; }
    public Dictionary<string, bool> DeviceFrameEnabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DeviceFrameColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public IndependentWindowLayoutMode? IndependentWindowStartupMode { get; set; } = IndependentWindowLayoutMode.Last;
    public int IndependentWindowLayoutSchema { get; set; }
    public Dictionary<string, IndependentWindowLayout> IndependentWindowLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IndependentWindowLayoutHistory> IndependentWindowLayoutHistoryByDevice { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DeviceControlPreferences> DeviceControlPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string SettingsCategory { get; set; } = "projection";
    public AppProxySettings NetworkProxy { get; set; } = new();
    public List<SettingShortcutBinding> SettingShortcuts { get; set; } = [];
    public string PreviewWindowsHotkeyAllowList { get; set; } = "";
    public string PreviewWindowsHotkeyBlockList { get; set; } = "";
    public ReverseControlMode MirroringAutoControlMode { get; set; } = ReverseControlMode.None;
    public bool ShowNativeKeyboard { get; set; } = true;
    public Services.Automation.AutomationSettings AutomationApi { get; set; } = new();
    public Dictionary<string, DeviceVideoPreferences> DeviceVideoPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public KeyboardMappingSettings KeyboardMapping { get; set; } = new();
    public bool CheckOnStartup { get; set; } = true;
    public bool AutoDownload { get; set; }
    public bool AllowMirrorFallback { get; set; } = true;
    public bool NotifyStableReleases { get; set; } = true;
    public bool NotifyPrereleaseReleases { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public ApplicationDisplayMode ApplicationDisplayMode { get; set; } =
        ApplicationDisplayMode.Complete;
    public bool CloseMainWindowToTray { get; set; }
    public bool HideInterfaceDescriptions { get; set; } = true;
    public bool HidePreviewToolbar { get; set; }
    public bool HidePreviewStatusBar { get; set; }
    public bool MainPreviewRoundedCorners { get; set; } = true;
    public bool MainPreviewPhoneFrame { get; set; }
    public SourceCardFields SourceCardFields { get; set; } = SourceCardFields.Default;
    public PreviewToolbarSettings? PreviewToolbar { get; set; }
    public WirelessReceiverBackend WirelessReceiverBackend { get; set; } =
        WirelessReceiverBackend.Original;
    public string WirelessDisplayProfileId { get; set; } = "1080p";
    public string WirelessReceiverName { get; set; } = WirelessReceiverConfiguration.DefaultReceiverName;
    public int WirelessRaopPort { get; set; } = WirelessReceiverConfiguration.DefaultRaopPort;
    public string Language { get; set; } = "system";
    public bool BluetoothWindowMouseEnabled { get; set; }
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
    public int BluetoothModeShortcutVirtualKey { get; set; } = 0x78;
    public int BluetoothModeShortcutModifiers { get; set; }
    public int BluetoothModeShortcutSchema { get; set; }
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
    public int BluetoothAppSwitcherShortcutVirtualKey { get; set; } = (int)KeyboardShortcut.MouseMiddle;
    public int BluetoothAppSwitcherShortcutModifiers { get; set; }
    public int BluetoothHomeShortcutVirtualKey { get; set; } = (int)KeyboardShortcut.MouseRight;
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
        DeviceControlPreferences = CopyDeviceSettings(DeviceControlPreferences),
        SettingsCategory = SettingsCategory,
        NetworkProxy = (NetworkProxy ?? new()).Clone(),
        SettingShortcuts = (SettingShortcuts ?? []).Where(binding => binding is not null && binding.IsValid)
            .Select(binding => binding with { }).ToList(),
        PreviewWindowsHotkeyAllowList = PreviewWindowsHotkeyAllowList ?? "",
        PreviewWindowsHotkeyBlockList = PreviewWindowsHotkeyBlockList ?? "",
        MirroringAutoControlMode = MirroringAutoControlMode,
        ShowNativeKeyboard = ShowNativeKeyboard,
        AutomationApi = (AutomationApi ?? new()).Clone(),
        PhoneFrameEnabled = PhoneFrameEnabled,
        DeviceFrameEnabled = CopyDeviceSettings(DeviceFrameEnabled),
        DeviceFrameColors = CopyDeviceSettings(DeviceFrameColors),
        IndependentWindowStartupMode = IndependentWindowStartupMode,
        IndependentWindowLayoutSchema = IndependentWindowLayoutSchema,
        IndependentWindowLayouts = CopyDeviceSettings(IndependentWindowLayouts),
        IndependentWindowLayoutHistoryByDevice = CopyDeviceSettings(IndependentWindowLayoutHistoryByDevice),
        DeviceVideoPreferences = CopyDeviceSettings(DeviceVideoPreferences),
        KeyboardMapping = KeyboardMapping.Clone(),
        CheckOnStartup = CheckOnStartup,
        AutoDownload = AutoDownload,
        AllowMirrorFallback = AllowMirrorFallback,
        NotifyStableReleases = NotifyStableReleases,
        NotifyPrereleaseReleases = NotifyPrereleaseReleases,
        Theme = Theme,
        ApplicationDisplayMode = ApplicationDisplayMode,
        CloseMainWindowToTray = CloseMainWindowToTray,
        HideInterfaceDescriptions = HideInterfaceDescriptions,
        HidePreviewToolbar = HidePreviewToolbar,
        HidePreviewStatusBar = HidePreviewStatusBar,
        MainPreviewRoundedCorners = MainPreviewRoundedCorners,
        MainPreviewPhoneFrame = MainPreviewRoundedCorners && MainPreviewPhoneFrame,
        SourceCardFields = SourceCardFields,
        PreviewToolbar = PreviewToolbar?.Clone(),
        WirelessReceiverBackend = WirelessReceiverBackend,
        WirelessDisplayProfileId = WirelessDisplayProfileId,
        WirelessReceiverName = WirelessReceiverName,
        WirelessRaopPort = WirelessRaopPort,
        Language = Language,
        BluetoothWindowMouseEnabled = BluetoothWindowMouseEnabled,
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
        BluetoothModeShortcutSchema = BluetoothModeShortcutSchema,
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
        BluetoothControlDeviceBindings = CopyDeviceSettings(BluetoothControlDeviceBindings),
    };

    internal static Dictionary<string, T> CopyDeviceSettings<T>(Dictionary<string, T>? settings)
    {
        var copy = new Dictionary<string, T>(settings?.Count ?? 0, StringComparer.OrdinalIgnoreCase);
        if (settings is null) return copy;
        // Older files can list a device under different casings. Keep the
        // last value in file order, as when an exact JSON key is repeated.
        foreach (var (device, value) in settings)
            if (value is not null) copy[device] = value;
        return copy;
    }
}

internal sealed class UpdateSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new DeviceSettingsConverter<bool>(),
            new DeviceSettingsConverter<string>(),
            new DeviceSettingsConverter<DeviceVideoPreferences>(),
            new DeviceSettingsConverter<DeviceControlPreferences>(),
            new DeviceSettingsConverter<IndependentWindowLayout>(),
            new DeviceSettingsConverter<IndependentWindowLayoutHistory>(),
        },
    };
    private readonly string _path;

    internal static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror");

    internal UpdateSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(UserDataDirectory, "settings.json");
    }

    internal UpdateSettings Load(bool persistMigrations = true) => Load(out _, persistMigrations);

    internal UpdateSettings Load(out bool readFailed, bool persistMigrations = true)
    {
        readFailed = false;
        try
        {
            var settingsJson = ReadSettingsJson(_path);
            if (settingsJson is null) return new UpdateSettings
            {
                BluetoothMouseSensitivitySchema = 2,
                BluetoothMouseOrientationSchema = 1,
                BluetoothMouseSettingsSchema = 1,
                BluetoothMouseDirectionSchema = 1,
                BluetoothControlShortcutSchema = 1,
                BluetoothModeShortcutSchema = 1,
                BluetoothShortcutSchema = 6,
                BluetoothHidReportMapVersion = BluetoothHidProtocol.ReportMapVersion,
                BluetoothHidReportMapAcknowledgedVersion = BluetoothHidProtocol.ReportMapVersion,
            };
            var settings = DeserializeSettings(settingsJson);
            var migrationChanged = false;
            if (!AppProxyPolicy.TryNormalize(settings.NetworkProxy, out var proxySettings))
            {
                settings.NetworkProxy = new();
                migrationChanged = true;
            }
            else
            {
                migrationChanged = settings.NetworkProxy.Mode != proxySettings.Mode ||
                    settings.NetworkProxy.Address != proxySettings.Address;
                settings.NetworkProxy = proxySettings;
            }
            using var settingsDocument = JsonDocument.Parse(settingsJson);
            // The retired C protocol (2) is normalized by DeviceVideoPreferences
            // during deserialization. Persist that migration once, retaining all
            // of the device's other video/audio preferences and stable 0/1 IDs.
            foreach (var property in settingsDocument.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals(nameof(UpdateSettings.DeviceVideoPreferences),
                        StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var device in property.Value.EnumerateObject())
                {
                    if (device.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var preference in device.Value.EnumerateObject())
                        if (preference.Name.Equals(nameof(Models.DeviceVideoPreferences.UsbProjectionMode),
                                StringComparison.OrdinalIgnoreCase) &&
                            preference.Value.ValueKind == JsonValueKind.Number &&
                            preference.Value.TryGetUInt32(out var protocol) && protocol > 1)
                            migrationChanged = true;
                }
            }
            if (settingsDocument.RootElement.ValueKind == JsonValueKind.Object &&
                !settingsDocument.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals(nameof(UpdateSettings.PhoneFrameEnabled), StringComparison.OrdinalIgnoreCase)))
            {
                // Preserve an existing opt-in when moving device preferences to one global switch.
                settings.PhoneFrameEnabled = settings.DeviceFrameEnabled?.Values.Any(enabled => enabled) == true;
                migrationChanged = true;
            }
            if (!Enum.IsDefined(settings.ApplicationDisplayMode))
            {
                settings.ApplicationDisplayMode = ApplicationDisplayMode.Complete;
                migrationChanged = true;
            }
            var sourceCardFields = settings.SourceCardFields & SourceCardFields.All;
            if (sourceCardFields == 0) sourceCardFields = SourceCardFields.Default;
            if (settings.SourceCardFields != sourceCardFields)
            {
                settings.SourceCardFields = sourceCardFields;
                migrationChanged = true;
            }
            if (!Enum.IsDefined(settings.WirelessReceiverBackend))
            {
                settings.WirelessReceiverBackend = WirelessReceiverBackend.Original;
                migrationChanged = true;
            }
            var normalizedProfileId = WirelessReceiverConfiguration.ProfileForSavedId(
                settings.WirelessDisplayProfileId).Id;
            if (!string.Equals(settings.WirelessDisplayProfileId, normalizedProfileId,
                StringComparison.Ordinal))
            {
                settings.WirelessDisplayProfileId = normalizedProfileId;
                migrationChanged = true;
            }
            var normalizedReceiverName = WirelessReceiverConfiguration.SanitizeReceiverName(
                settings.WirelessReceiverName);
            var normalizedRaopPort = WirelessReceiverConfiguration.NormalizeRaopPort(settings.WirelessRaopPort);
            if (settings.WirelessRaopPort != normalizedRaopPort)
            {
                settings.WirelessRaopPort = normalizedRaopPort;
                migrationChanged = true;
            }
            if (!string.Equals(settings.WirelessReceiverName, normalizedReceiverName,
                StringComparison.Ordinal))
            {
                settings.WirelessReceiverName = normalizedReceiverName;
                migrationChanged = true;
            }
            migrationChanged |= WasRepaired(settings.DeviceControlPreferences) ||
                WasRepaired(settings.IndependentWindowLayouts) ||
                WasRepaired(settings.IndependentWindowLayoutHistoryByDevice) ||
                WasRepaired(settings.DeviceVideoPreferences) ||
                WasRepaired(settings.DeviceFrameEnabled) ||
                WasRepaired(settings.DeviceFrameColors) ||
                WasRepaired(settings.BluetoothControlDeviceBindings);
            if (!settingsDocument.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals(nameof(UpdateSettings.IndependentWindowStartupMode), StringComparison.OrdinalIgnoreCase)))
                settings.IndependentWindowStartupMode = null;
            migrationChanged |= MigrateIndependentWindowStartupMode(settings);
            if (settings.IndependentWindowLayoutSchema < 1)
            {
                // Replace the old automatic default once; explicit choices made
                // after this upgrade remain unchanged on subsequent launches.
                if (settings.IndependentWindowStartupMode == IndependentWindowLayoutMode.Automatic)
                    settings.IndependentWindowStartupMode = IndependentWindowLayoutMode.Last;
                settings.IndependentWindowLayoutHistoryByDevice = settings.IndependentWindowLayoutHistoryByDevice
                    .ToDictionary(pair => pair.Key, pair => pair.Value with { AutoFix = null }, StringComparer.OrdinalIgnoreCase);
                settings.IndependentWindowLayoutSchema = 1;
                migrationChanged = true;
            }
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
            if (settings.BluetoothModeShortcutSchema < 1)
            {
                // Older builds persisted this field as zero even though the
                // intended default shortcut is F9. Preserve valid custom
                // shortcuts, but migrate the legacy zero value once.
                if (!KeyboardShortcut.IsValid(settings.BluetoothModeShortcutModifiers,
                        settings.BluetoothModeShortcutVirtualKey) ||
                    settings.BluetoothModeShortcutVirtualKey == 0 &&
                    settings.BluetoothModeShortcutModifiers == 0)
                {
                    settings.BluetoothModeShortcutVirtualKey = 0x78;
                    settings.BluetoothModeShortcutModifiers = 0;
                }
                settings.BluetoothModeShortcutSchema = 1;
                migrationChanged = true;
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
            var bossKey = KeyboardShortcut.FromSettings(settings, BluetoothShortcutAction.BossKey);
            if (settings.BluetoothBossShortcutModifiers != (int)bossKey.Modifiers ||
                settings.BluetoothBossShortcutVirtualKey != (int)bossKey.VirtualKey)
            {
                settings.BluetoothBossShortcutVirtualKey = (int)bossKey.VirtualKey;
                settings.BluetoothBossShortcutModifiers = (int)bossKey.Modifiers;
                settings.BluetoothShortcutSchema = 6;
                migrationChanged = true;
            }
            settings.BluetoothWheelSensitivity = Math.Clamp(
                double.IsFinite(settings.BluetoothWheelSensitivity)
                    ? settings.BluetoothWheelSensitivity : 100, 10, 1000);
            if (settings.BluetoothLandscapeMouseMode is < 1 or > 4)
                settings.BluetoothLandscapeMouseMode = 1;
            var settingIds = new HashSet<Guid>();
            var settingKeys = Enum.GetValues<BluetoothShortcutAction>()
                .Select(action => KeyboardShortcut.FromSettings(settings, action))
                .Where(shortcut => shortcut.IsBound).ToHashSet();
            settings.SettingShortcuts = (settings.SettingShortcuts ?? [])
                .Where(binding => binding is not null && binding.IsValid && settingIds.Add(binding.Id) &&
                    (!binding.Shortcut.IsBound || settingKeys.Add(binding.Shortcut))).Take(16).ToList();
            if (migrationChanged && persistMigrations) TrySaveMigration(settings);
            return settings;
        }
        catch (Exception error) when (error is JsonException or IOException or
                                      UnauthorizedAccessException)
        {
            readFailed = true;
            DiagnosticLogger.Exception("settings", "load_failed", error,
                ("file", Path.GetFileName(_path)));
            return new UpdateSettings();
        }
    }

    internal static UpdateSettings DeserializeSettings(string json) =>
        JsonSerializer.Deserialize<UpdateSettings>(json, JsonOptions) ?? throw new JsonException("Empty settings document.");

    internal static string? ReadSettingsJson(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool MigrateIndependentWindowStartupMode(UpdateSettings settings)
    {
        if (settings.IndependentWindowStartupMode is { } mode && Enum.IsDefined(mode)) return false;

        // Existing installs can carry different policies for different devices.
        // Adopt an unambiguous policy and otherwise keep all bounds for manual selection.
        var legacyModes = settings.IndependentWindowLayoutHistoryByDevice.Keys
            .Union(settings.IndependentWindowLayouts.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(device =>
            {
                var history = settings.IndependentWindowLayoutHistoryByDevice.GetValueOrDefault(device);
                if (history?.Mode is { } oldMode && Enum.IsDefined(oldMode)) return oldMode;
                if (history?.UseLastLayout == true) return IndependentWindowLayoutMode.Last;
                if (settings.IndependentWindowLayouts.GetValueOrDefault(device) is { IsValid: true, IsEmpty: false })
                    return IndependentWindowLayoutMode.Preset;
                return (IndependentWindowLayoutMode?)null;
            })
            .OfType<IndependentWindowLayoutMode>()
            .Distinct()
            .Take(2)
            .ToArray();
        settings.IndependentWindowStartupMode = legacyModes.Length == 1
            ? legacyModes[0] : IndependentWindowLayoutMode.Last;
        return true;
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
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, _path, overwrite: true);
                    break;
                }
                catch (IOException error) when (attempt < 3 && error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
                {
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException) when (attempt < 3 && File.Exists(_path))
                {
                    // A competing reader can briefly deny Windows an atomic replacement.
                    Thread.Sleep(25);
                }
            }
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
        var settings = Load(out var readFailed, persistMigrations: false);
        if (readFailed) throw new IOException("Existing settings could not be read.");
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

    private static bool WasRepaired<T>(Dictionary<string, T>? settings) =>
        settings is LoadedDeviceSettings<T> { Repaired: true };

    private sealed class LoadedDeviceSettings<T>() : Dictionary<string, T>(StringComparer.OrdinalIgnoreCase)
    {
        internal bool Repaired { get; set; }
    }

    private sealed class DeviceSettingsConverter<T> : JsonConverter<Dictionary<string, T>>
    {
        public override bool HandleNull => true;

        public override Dictionary<string, T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var result = new LoadedDeviceSettings<T>();
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                result.Repaired = true;
                return result;
            }

            // Read each entry in JSON order so exact duplicates and case
            // variants follow the same rule. A damaged later entry doesn't
            // replace a usable earlier entry for that device.
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Null)
                {
                    try
                    {
                        if (property.Value.Deserialize<T>(options) is { } value)
                        {
                            if (!result.TryAdd(property.Name, value))
                            {
                                result.Repaired = true;
                                result[property.Name] = value;
                            }
                            continue;
                        }
                    }
                    catch (JsonException) { /* Keep the other devices and settings. */ }
                }
                result.Repaired = true;
            }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value is not null)
            {
                var entries = value.Comparer.Equals(StringComparer.OrdinalIgnoreCase)
                    ? value : UpdateSettings.CopyDeviceSettings(value);
                foreach (var (device, setting) in entries)
                {
                    if (setting is null) continue;
                    writer.WritePropertyName(device);
                    JsonSerializer.Serialize(writer, setting, options);
                }
            }
            writer.WriteEndObject();
        }
    }
}
