using System.Globalization;
using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Windows;
using IPhoneMirror.App;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunTaiwanLocalizationTests()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.IsUiPreviewMode = true;
        app.InitializeComponent();
        var service = typeof(LocalizationService);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        void ApplyLanguage(string language) => service.GetMethod("ApplyLanguage", flags)!
            .Invoke(null, [language, false, true]);
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        var directory = Path.Combine(Path.GetTempPath(), "iPhoneMirror-Taiwan-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new UpdateSettingsStore(path);
            store.Save(new UpdateSettings { Language = "en-US", WirelessReceiverName = "Keep my receiver" });
            SetKeyboardField(app, "_settingsStore", store);
            foreach (var language in new[] { "zh-TW", "zh-HK", "zh-CN", "en-US", "zh-TW" })
            {
                KeyboardCall(app, "LoadStartupSettings", true);
                app.IsUiPreviewMode = false;
                Check(LocalizationService.SetLanguage(language) && app.UpdateSettings.Language == language,
                    "The language switch must update the same preferences used by other windows.");
                app.UpdateSettings.SourceCardFields = SourceCardFields.Name | SourceCardFields.Model;
                Check(app.SaveUpdateSettings(), "A subsequent source-card save failed.");
                app.IsUiPreviewMode = true;
                // Use the production startup loader and a fresh store, as on restart.
                KeyboardCall(app, "LoadStartupSettings", true);
                var restored = app.UpdateSettings.Language;
                Check(restored == language && LocalizationService.SelectedLanguage == language,
                    "Language preference failed to survive reload: " + language);
                Check(CultureInfo.CurrentUICulture.Name == language &&
                    LocalizationService.EffectiveCulture.Name == language, "Wrong effective culture");
                Check(app.UpdateSettings.WirelessReceiverName == "Keep my receiver" &&
                    app.UpdateSettings.SourceCardFields == (SourceCardFields.Name | SourceCardFields.Model),
                    "Language save overwrote unrelated preferences");
                Check(app.Resources.MergedDictionaries.Count(d =>
                    d.Source?.OriginalString.Contains("Localization/Strings.") == true) == 1,
                    "Language dictionaries accumulated");
            }
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(path, "blocked.json")));
            app.IsUiPreviewMode = false;
            Check(!LocalizationService.SetLanguage("ja-JP") && LocalizationService.SelectedLanguage == "zh-TW" &&
                app.UpdateSettings.Language == "zh-TW" && app.Resources.MergedDictionaries.Count(d =>
                    d.Source?.OriginalString.Contains("Localization/Strings.") == true) == 1,
                "A failed language write must leave the previous preference and dictionary active.");
            app.IsUiPreviewMode = true;
            SetKeyboardField(app, "_settingsStore", store);
            foreach (var culture in new[] { "zh-TW", "zh-Hant-TW", "ZH-tw" })
                Check(LocalizationService.ResolveCultureName(culture) == "zh-TW", "Taiwan fallback: " + culture);
            Check(LocalizationService.StartupCultureName == "zh-TW", "Startup fallback ignored Taiwan");
            Check(LocalizationService.Get("ResolutionLabel") == "解析度", "Wrong Taiwan terminology");
            Check(LocalizedReleaseNotes.ChangelogFileName == "CHANGELOG.zh-TW.md", "Wrong changelog path");
            var release = new ReleaseInfo("v1.8.4-test4", "iPhoneMirror v1.8.4-test4", "Original release content",
                DateTimeOffset.UtcNow, SemanticVersion.Parse("1.8.4-test4"), true, null, null, null);
            var translated = LocalizedReleaseNotes.Body(release);
            Check(translated.Contains("### 修正") && translated.Contains("RemotePairing"),
                "Embedded release notes are missing or incomplete");
            Check(LocalizedReleaseNotes.FindSection("## [1.2.30]\nwrong\n## [1.2.3]\nright", "v1.2.3") == "right",
                "Release matching accepted a version prefix");
            Check(LocalizedReleaseNotes.FindSection("## [1.2.3]\ntext", "v9.0.0") is null,
                "Unknown release must not reuse another version's notes");
            VerifyReleaseNotesSwitch(ApplyLanguage, Check, release);
            using var client = new GitHubReleaseClient();
            var window = new UpdateWindow(release, client, false, false);
            try
            {
                string Notes() => new System.Windows.Documents.TextRange(window.ReleaseNotesViewer.Document.ContentStart,
                    window.ReleaseNotesViewer.Document.ContentEnd).Text;
                Check(Notes().Contains("RemotePairing") && !Notes().Contains("Original release content"),
                    "Taiwan release notes did not render");
                ApplyLanguage("en-US");
                Check(Notes().Contains("Original release content"), "Switching back lost original release notes");
                ApplyLanguage("zh-TW");
                Check(Notes().Contains("RemotePairing"), "Live switch failed to restore Taiwan release notes");
            }
            finally { window.Close(); }
            Console.WriteLine("Taiwan localization: persistence, restart load, unrelated settings, culture, embedded notes and live rendered notes passed.");
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            // Only this test's newly created temporary files.
            File.Delete(Path.Combine(directory, "settings.json"));
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            app.Shutdown();
        }
    }

    private static void VerifyReleaseNotesSwitch(Action<string> apply, Action<bool, string> check,
        ReleaseInfo release)
    {
        var requests = 0;
        using var http = new HttpClient(new ReleaseNotesHandler(() => requests++));
        using var client = new GitHubReleaseClient(http);
        var expectedNotes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en-US"] = "Original English notes",
            ["zh-CN"] = "简体中文说明",
            ["zh-HK"] = "香港版本的說明",
            ["zh-TW"] = "臺灣版本的說明",
            ["ja-JP"] = "日本語の説明",
        };
        var future = release with
        {
            TagName = "v9.0.0",
            Body = expectedNotes["en-US"],
            LocalizedNotes = expectedNotes.Where(note => note.Key != "en-US")
                .ToDictionary(note => note.Key, note => note.Value, StringComparer.Ordinal),
        };
        var window = new UpdateWindow(future, client, false, false);
        try
        {
            window.Show();
            foreach (var (language, expected) in expectedNotes.Reverse())
            {
                apply(language);
                var text = new System.Windows.Documents.TextRange(
                    window.ReleaseNotesViewer.Document.ContentStart,
                    window.ReleaseNotesViewer.Document.ContentEnd).Text;
                check(text.Trim() == expected, "Language switch did not immediately show " + language);
            }
            check(requests == 0, "Opening or switching update notes made an extra network request");
        }
        finally { window.Close(); }
        apply("zh-TW");
    }

    private sealed class ReleaseNotesHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Unexpected notes request"));
        }
    }
}
