using System.Globalization;
using System.IO;
using System.Reflection;
using System.Net;
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
            VerifyTaiwanReleaseDownload(ApplyLanguage, Check, release);
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

    private static void VerifyTaiwanReleaseDownload(Action<string> apply, Action<bool, string> check, ReleaseInfo release)
    {
        var requests = new List<string>();
        var mode = "success";
        var pendingJapanese = new TaskCompletionSource<HttpResponseMessage>();
        using var http = new HttpClient(new TaiwanNotesHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            if (mode == "deferred" && path.EndsWith(".ja-JP.md", StringComparison.Ordinal))
                return pendingJapanese.Task;
            if (mode == "offline" && path.EndsWith(".zh-HK.md", StringComparison.Ordinal))
                throw new HttpRequestException("Simulated inaccessible notes host");
            if (mode == "api-fallback" && path.EndsWith(".zh-HK.md", StringComparison.Ordinal))
            {
                if (request.RequestUri.Host == "raw.githubusercontent.com")
                    throw new HttpRequestException("Simulated inaccessible raw host");
                check(request.RequestUri.Host == "api.github.com" &&
                    request.RequestUri.Query == "?ref=v9.0.0" &&
                    request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github.raw+json"),
                    "Fallback did not request the raw contents of the exact release tag");
            }
            var legacyTaiwan = path.EndsWith("CHANGELOG.zh-TW.md", StringComparison.Ordinal);
            var body = path.Split('/').Last() switch
            {
                "v9.0.0.zh-CN.md" => "### 新增\n\n- 新增简体中文说明。",
                "v9.0.0.zh-HK.md" => "### 改動\n\n- 新增香港版本的說明。",
                "v9.0.0.ja-JP.md" => "### 変更\n\n- 日本語の説明を追加しました。",
                "v9.0.0.zh-TW.md" => null,
                "CHANGELOG.zh-TW.md" => "## [9.0.0]\n\n### 新增\n\n- 新增螢幕鏡像功能。\n\n## [8.0.0]\nOlder release",
                _ => "Original online notes",
            };
            if (legacyTaiwan && mode == "missing") body = null;
            if (legacyTaiwan && mode == "oversized") body = new string('x', 1024 * 1024 + 1);
            var response = new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body ?? string.Empty),
            };
            if (request.RequestUri.Host == "api.github.com")
                response.Content.Headers.ContentType = new("application/vnd.github.raw+json");
            if (legacyTaiwan && mode == "redirect")
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://untrusted.example/notes");
            return Task.FromResult(response);
        }));
        using var client = new GitHubReleaseClient(http);
        var future = release with { TagName = "v9.0.0", Body = "metadata" };
        apply("en-US");
        var english = client.EnrichReleaseNotesAsync(future).GetAwaiter().GetResult();
        check(requests.Count == 1 && english.Body == "metadata" &&
            LocalizedReleaseNotes.Body(english) == "Original online notes" && english.LocalizedNotes.Count == 0,
            "English unexpectedly requested a translation");
        apply("zh-TW");
        var translated = client.EnrichReleaseNotesAsync(future).GetAwaiter().GetResult();
        check(translated.TaggedBody == "Original online notes" &&
            LocalizedReleaseNotes.Body(translated) == "### 新增\n\n- 新增螢幕鏡像功能。",
            "Taiwan online release section was not independently localized");
        apply("en-US");
        var live = new UpdateWindow(english, client, false, false);
        try
        {
            live.Show();
            apply("zh-TW");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            string Text() => new System.Windows.Documents.TextRange(live.ReleaseNotesViewer.Document.ContentStart,
                live.ReleaseNotesViewer.Document.ContentEnd).Text;
            check(Text().Contains("新增螢幕鏡像功能"), "Live language switch did not fetch future Taiwan notes");
            var afterTaiwan = requests.Count;
            apply("en-US");
            check(Text().Contains("Original online notes"), "Future release lost its original body");
            apply("zh-TW");
            check(requests.Count == afterTaiwan, "Live switch fetched the same Taiwan notes repeatedly");
            foreach (var (language, expected) in new[]
            {
                ("zh-CN", "简体中文说明"), ("zh-HK", "香港版本的說明"), ("ja-JP", "日本語の説明")
            })
            {
                apply(language);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                check(Text().Contains(expected) && !Text().Contains("Original online notes"),
                    "Live switch failed to render " + language);
            }
            apply("zh-TW");
        }
        finally { live.Close(); }
        var retryWindow = new UpdateWindow(english, client, false, false);
        try
        {
            apply("en-US");
            retryWindow.Show();
            string Text() => new System.Windows.Documents.TextRange(
                retryWindow.ReleaseNotesViewer.Document.ContentStart,
                retryWindow.ReleaseNotesViewer.Document.ContentEnd).Text;

            mode = "deferred";
            apply("ja-JP");
            check(Text().Contains(LocalizationService.Get("ReleaseNotesLoading")) &&
                !Text().Contains("Original online notes"), "Waiting for a translation briefly exposed English");
            apply("en-US");
            using var originalRequest = new HttpRequestMessage(HttpMethod.Get,
                "https://raw.githubusercontent.com/RayrenSX/iPhoneMirror/v9.0.0/docs/releases/v9.0.0.ja-JP.md");
            pendingJapanese.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = originalRequest,
                Content = new StringContent("日本語の説明を追加しました。")
            });
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            check(Text().Contains("Original online notes"), "A late translation replaced the current English body");
            apply("ja-JP");
            check(Text().Contains("日本語の説明"), "Completed translation was not cached for the next switch");

            mode = "offline";
            apply("zh-HK");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            check(Text().Contains("Original online notes"), "Unavailable localized notes did not fall back to English");
            apply("en-US");
            mode = "api-fallback";
            apply("zh-HK");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            check(Text().Contains("香港版本的說明"), "Transient notes failure was not retried via GitHub Contents");
        }
        finally { retryWindow.Close(); }
        apply("zh-TW");
        foreach (var failure in new[] { "missing", "oversized", "redirect" })
        {
            mode = failure;
            var fallback = client.EnrichReleaseNotesAsync(future).GetAwaiter().GetResult();
            check(fallback.LocalizedNotes.GetValueOrDefault("zh-TW") is null &&
                LocalizedReleaseNotes.Body(fallback) == "Original online notes",
                "Optional Taiwan notes failure corrupted original content: " + failure);
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            client.EnrichReleaseNotesAsync(future, canceled.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Release notes ignored cancellation");
        }
        catch (OperationCanceledException) { }
    }

    private sealed class TaiwanNotesHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request).WaitAsync(cancellationToken);
        }
    }
}
