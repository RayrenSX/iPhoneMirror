using System.Globalization;
using System.Windows;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Localization;

internal static class LocalizationService
{
    internal const string SystemLanguage = "system";
    internal const string SimplifiedChinese = "zh-CN";
    internal const string TraditionalChineseHongKong = "zh-HK";
    internal const string TraditionalChineseTaiwan = "zh-TW";
    internal const string English = "en-US";
    internal const string Japanese = "ja-JP";

    private const string DictionaryPrefix = "Localization/Strings.";
    private const string AppFontFamilyKey = "AppFontFamily";
    private const string DefaultFontFamily = "Segoe UI, Microsoft YaHei UI";
    private const string JapaneseFontFamily = "Segoe UI, Yu Gothic UI";
    private static string _selectedLanguage = SystemLanguage;
    private static CultureInfo _effectiveCulture = CultureInfo.GetCultureInfo(English);
    private static Func<string, bool>? _savePreference;

    internal static event EventHandler? LanguageChanged;

    internal static string SelectedLanguage => _selectedLanguage;
    internal static CultureInfo EffectiveCulture => _effectiveCulture;
    internal static string StartupCultureName => _selectedLanguage == SystemLanguage
        ? ResolveCultureName(CultureInfo.InstalledUICulture.Name)
        : ResolveCultureName(_selectedLanguage);

    internal static void Initialize(string language, Func<string, bool>? savePreference = null)
    {
        ApplyLanguage(language, persist: false, notify: false);
        _savePreference = savePreference;
    }

    internal static bool SetLanguage(string language) =>
        ApplyLanguage(language, persist: true, notify: true);

    internal static string Get(string key)
    {
        if (Application.Current?.TryFindResource(key) is string value)
            return LocalizedText.Resource(key, value);
        return key;
    }

    internal static string GetOrDefault(string key, string fallback) =>
        Application.Current?.TryFindResource(key) is string value
            ? LocalizedText.Resource(key, value) : fallback;

    internal static string Format(string key, params object?[] arguments) =>
        LocalizedText.Format(key, arguments);

    internal static string RefreshText(string value) => LocalizedText.Refresh(value);

    // Preserve known business refusals, but never display an arbitrary OS/runtime
    // exception as the user-facing recovery instructions. Callers log the exception.
    internal static string ErrorMessage(Exception error, string fallbackKey) =>
        LocalizedText.IsLocalized(error.Message) ? error.Message : Get(fallbackKey);

    internal static string Join(string separator, IEnumerable<string?> values) =>
        LocalizedText.Join(separator, values);

    internal static void RefreshWhenLanguageChanges(Window window, Action refresh)
    {
        var closed = false;
        void Changed(object? sender, EventArgs args)
        {
            if (closed || window.Dispatcher.HasShutdownStarted) return;
            if (window.Dispatcher.CheckAccess()) refresh();
            else window.Dispatcher.BeginInvoke(new Action(() => { if (!closed) refresh(); }));
        }
        LanguageChanged += Changed;
        window.Closed += (_, _) => { closed = true; LanguageChanged -= Changed; };
    }

    private static bool ApplyLanguage(string language, bool persist, bool notify)
    {
        if (language is not (SystemLanguage or SimplifiedChinese or
            TraditionalChineseHongKong or TraditionalChineseTaiwan or English or Japanese))
            language = SystemLanguage;

        var cultureName = language == SystemLanguage
            ? ResolveSystemCulture()
            : language;
        var culture = CultureInfo.GetCultureInfo(cultureName);

        // Keep startup failure dialogs in the requested language.
        if (!persist) _selectedLanguage = language;
        var application = Application.Current;
        ResourceDictionary? replacement = null;
        if (application is not null)
        {
            replacement = new ResourceDictionary
            {
                Source = new Uri(
                    $"/{typeof(LocalizationService).Assembly.GetName().Name};component/" +
                    $"{DictionaryPrefix}{cultureName}.xaml", UriKind.Relative),
            };
        }
        if (persist && _savePreference?.Invoke(language) == false) return false;

        _selectedLanguage = language;
        if (application is not null && replacement is not null)
        {
            var dictionaries = application.Resources.MergedDictionaries;
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
            ApplyInterfaceFontFamily(dictionaries, cultureName);
        }

        _effectiveCulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (notify) LanguageChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }

    // DesignTokens falls back to Microsoft YaHei UI for CJK text, which draws
    // Japanese kana and kanji with Chinese glyph forms. Font setters use
    // dynamic resource references, so replacing this fallback refreshes existing
    // controls and styles that were resolved before the language switch.
    private static void ApplyInterfaceFontFamily(
        System.Collections.ObjectModel.Collection<ResourceDictionary> dictionaries,
        string cultureName)
    {
        var family = cultureName == Japanese ? JapaneseFontFamily : DefaultFontFamily;
        foreach (var dictionary in dictionaries)
        {
            if (!dictionary.Contains(AppFontFamilyKey) ||
                dictionary[AppFontFamilyKey] is System.Windows.Media.FontFamily current &&
                current.Source == family)
                continue;
            dictionary[AppFontFamilyKey] = new System.Windows.Media.FontFamily(family);
        }
    }

    private static string ResolveSystemCulture() =>
        ResolveCultureName(CultureInfo.InstalledUICulture.Name);

    internal static string ResolveCultureName(string cultureName)
    {
        if (cultureName.Equals(TraditionalChineseTaiwan, StringComparison.OrdinalIgnoreCase) ||
            cultureName.Equals("zh-Hant-TW", StringComparison.OrdinalIgnoreCase))
            return TraditionalChineseTaiwan;
        if (IsHongKongTraditionalChinese(cultureName))
            return TraditionalChineseHongKong;
        if (cultureName.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return Japanese;
        return cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChinese
            : English;
    }

    private static bool IsHongKongTraditionalChinese(string cultureName) =>
        cultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals("zh-CHT", StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals(TraditionalChineseHongKong,
            StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals("zh-MO", StringComparison.OrdinalIgnoreCase);

}
