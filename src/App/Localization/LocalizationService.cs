using System.Globalization;
using System.IO;
using System.Windows;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Localization;

internal static class LocalizationService
{
    internal const string SystemLanguage = "system";
    internal const string SimplifiedChinese = "zh-CN";
    internal const string TraditionalChineseHongKong = "zh-HK";
    internal const string English = "en-US";

    private const string DictionaryPrefix = "Localization/Strings.";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror", "settings.json");

    private static string _selectedLanguage = SystemLanguage;
    private static CultureInfo _effectiveCulture = CultureInfo.GetCultureInfo(English);

    internal static event EventHandler? LanguageChanged;

    internal static string SelectedLanguage => _selectedLanguage;
    internal static CultureInfo EffectiveCulture => _effectiveCulture;
    internal static string StartupCultureName => _selectedLanguage == SystemLanguage
        ? ResolveCultureName(CultureInfo.InstalledUICulture.Name)
        : ResolveCultureName(_selectedLanguage);

    internal static void Initialize()
    {
        var configured = LoadLanguage();
        ApplyLanguage(configured, persist: false, notify: false);
    }

    internal static void SetLanguage(string language) =>
        ApplyLanguage(language, persist: true, notify: true);

    internal static string Get(string key)
    {
        if (Application.Current?.TryFindResource(key) is string value) return value;
        return key;
    }

    internal static string Format(string key, params object?[] arguments) =>
        string.Format(_effectiveCulture, Get(key), arguments);

    private static void ApplyLanguage(string language, bool persist, bool notify)
    {
        if (language is not (SystemLanguage or SimplifiedChinese or
            TraditionalChineseHongKong or English))
            language = SystemLanguage;

        var cultureName = language == SystemLanguage
            ? ResolveSystemCulture()
            : language;
        var culture = CultureInfo.GetCultureInfo(cultureName);

        // Record the requested language before loading its resource dictionary so a
        // startup-failure dialog remains localized even when that load throws.
        _selectedLanguage = language;
        var application = Application.Current;
        if (application is not null)
        {
            var dictionaries = application.Resources.MergedDictionaries;
            var replacement = new ResourceDictionary
            {
                Source = new Uri(
                    $"/{typeof(LocalizationService).Assembly.GetName().Name};component/" +
                    $"{DictionaryPrefix}{cultureName}.xaml", UriKind.Relative),
            };
            var existingIndex = -1;
            for (var index = 0; index < dictionaries.Count; ++index)
            {
                var source = dictionaries[index].Source?.OriginalString;
                if (source?.Contains(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true)
                {
                    existingIndex = index;
                    break;
                }
            }
            if (existingIndex >= 0) dictionaries[existingIndex] = replacement;
            else dictionaries.Insert(0, replacement);
        }

        _effectiveCulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (persist) SaveLanguage(language);
        if (notify) LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    private static string ResolveSystemCulture() =>
        ResolveCultureName(CultureInfo.InstalledUICulture.Name);

    internal static string ResolveCultureName(string cultureName)
    {
        if (IsHongKongTraditionalChinese(cultureName))
            return TraditionalChineseHongKong;
        return cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChinese
            : English;
    }

    private static bool IsHongKongTraditionalChinese(string cultureName) =>
        cultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals("zh-CHT", StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals(TraditionalChineseHongKong,
            StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals("zh-TW", StringComparison.OrdinalIgnoreCase);

    private static string LoadLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return SystemLanguage;
            return new UpdateSettingsStore(SettingsPath).Load().Language;
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("localization", "language_load_failed", error);
            return SystemLanguage;
        }
    }

    private static void SaveLanguage(string language)
    {
        try
        {
            new UpdateSettingsStore(SettingsPath).Update(settings =>
                settings.Language = language);
        }
        catch (Exception error)
        {
            // Language switching must remain usable even if settings cannot be saved.
            DiagnosticLogger.Exception("localization", "language_save_failed", error);
        }
    }
}
