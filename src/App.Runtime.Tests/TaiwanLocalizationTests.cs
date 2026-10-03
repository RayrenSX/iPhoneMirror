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
            foreach (var language in new[] { "zh-TW", "zh-HK", "zh-CN", "en-US", "zh-TW" })
            {
                service.GetMethod("SaveLanguage", flags)!.Invoke(null, [language, path]);
                // Use the production startup loader and a fresh store, as on restart.
                var restored = (string)service.GetMethod("LoadLanguage", flags)!.Invoke(null, [path])!;
                ApplyLanguage(restored);
                Check(restored == language && LocalizationService.SelectedLanguage == language,
                    "Language preference failed to survive reload: " + language);
                Check(CultureInfo.CurrentUICulture.Name == language &&
                    LocalizationService.EffectiveCulture.Name == language, "Wrong effective culture");
                Check(new UpdateSettingsStore(path).Load().WirelessReceiverName == "Keep my receiver",
                    "Language save overwrote unrelated preferences");
                Check(app.Resources.MergedDictionaries.Count(d =>
                    d.Source?.OriginalString.Contains("Localization/Strings.") == true) == 1,
                    "Language dictionaries accumulated");
            }
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
        using var http = new HttpClient(new TaiwanNotesHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            var taiwan = path.EndsWith("CHANGELOG.zh-TW.md", StringComparison.Ordinal);
            var response = new HttpResponseMessage(taiwan && mode == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(!taiwan ? "Original online notes" : mode == "oversized"
                    ? new string('x', 1024 * 1024 + 1)
                    : "## [9.0.0]\n\n### 新增\n\n- 新增螢幕鏡像功能。\n\n## [8.0.0]\nOlder release"),
            };
            if (taiwan && mode == "redirect")
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://untrusted.example/notes");
            return response;
        }));
        using var client = new GitHubReleaseClient(http);
        var future = release with { TagName = "v9.0.0", Body = "metadata" };
        apply("en-US");
        var english = client.EnrichReleaseNotesAsync(future).GetAwaiter().GetResult();
        check(requests.Count == 1 && english.Body == "Original online notes" && english.TaiwanBody is null,
            "Other locales unexpectedly changed their release-note requests");
        apply("zh-TW");
        var translated = client.EnrichReleaseNotesAsync(future).GetAwaiter().GetResult();
        check(translated.Body == "Original online notes" &&
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
        }
        finally { live.Close(); }
        foreach (var failure in new[] { "missing", "oversized", "redirect" })
        {
            mode = failure;
            var fallback = client.EnrichReleaseNotesAsync(future).GetAwaiter().GetResult();
            check(fallback.TaiwanBody is null && fallback.Body == "Original online notes",
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

    private sealed class TaiwanNotesHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
