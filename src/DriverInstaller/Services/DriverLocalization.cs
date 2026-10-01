using System.Globalization;
using System.Text.Json;
using System.Windows;

namespace IPhoneMirror.DriverInstaller.Services;

internal static class DriverLocalization
{
    internal const string Chinese = "zh-CN";
    internal const string TraditionalChineseHongKong = "zh-HK";
    internal const string English = "en-US";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror", "settings.json");

    internal static string Language { get; private set; } = English;
    internal static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(English);

    internal static void Initialize(IReadOnlyList<string> arguments)
    {
        var requested = ReadArgument(arguments) ?? LoadConfiguredLanguage();
        Language = ResolveLanguage(requested);
        Culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    internal static string Get(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    internal static string GetOrDefault(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    internal static string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);

    internal static ResourceDictionary CreateDictionary() => new()
    {
        Source = new Uri($"Localization/Strings.{Language}.xaml", UriKind.Relative),
    };


    internal static string LocalizeOperationResult(string message)
    {
        if (message is "ParentBindingComplete" or "ParentBindingRestartRequired" or
            "ParentResetRestartRequired" or "ParentResetComplete" or "ParentChangeRejected" or
            "ParentChangeRolledBack" or "ParentRollbackRestartRequired" or "ParentChangeRecoveryNeeded")
            return Get(message);
        // Translate only known application messages, never arbitrary device names,
        // paths, native diagnostics, or protocol fields.
        (string Prefix, string Key)[] messages =
        [
            ("The incorrect Apple parent device was removed. Reconnect the iPhone to rebind usbccgp.", "DriverParentRemoved"),
            ("Selected-device capture filter removed. Reconnect the device to complete unload.", "DriverFilterRemovedReconnect"),
            ("Selected-device capture filter installed. Reconnect the device to complete activation.", "DriverFilterInstalledReconnect"),
            ("Parent driver repair stopped after the removal request began. Reconnect the iPhone and review the operation log. ", "DriverParentRepairStopped"),
            ("Parent driver repair was rejected before any system change. ", "DriverParentRepairRejected"),
            ("Driver operation failed and all captured state was restored. ", "DriverOperationRolledBack"),
            ("Driver operation failed and rollback was incomplete. Review the operation log. ", "DriverOperationRollbackIncomplete"),
        ];
        foreach (var (prefix, key) in messages)
        {
            if (!message.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var translated = Get(key);
            if (translated == key) return message;
            var detail = message[prefix.Length..];
            return detail.Length == 0 ? translated
                : translated + "\n" + Format("DriverOperationDetailsFormat", detail);
        }
        return message;
    }

    private static string? ReadArgument(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index + 1 < arguments.Count; ++index)
            if (string.Equals(arguments[index], "--language", StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1];
        return null;
    }

    private static string LoadConfiguredLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ResolveSystemLanguage();
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath));
            return ResolveLanguage(settings?.Language);
        }
        catch { return ResolveSystemLanguage(); }
    }

    private static string ResolveLanguage(string? value)
    {
        if (string.Equals(value, Chinese, StringComparison.OrdinalIgnoreCase))
            return Chinese;
        if (value is not null && IsTraditionalChinese(value))
            return TraditionalChineseHongKong;
        return string.Equals(value, English, StringComparison.OrdinalIgnoreCase)
            ? English
            : ResolveSystemLanguage();
    }

    private static string ResolveSystemLanguage() =>
        ResolveCultureName(CultureInfo.InstalledUICulture.Name);

    internal static string ResolveCultureName(string cultureName)
    {
        if (IsTraditionalChinese(cultureName))
            return TraditionalChineseHongKong;
        return cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese : English;
    }

    private static bool IsTraditionalChinese(string cultureName) =>
        cultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals("zh-CHT", StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals(TraditionalChineseHongKong,
            StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals("zh-TW", StringComparison.OrdinalIgnoreCase);

    private sealed record UserSettings(string Language);
}
