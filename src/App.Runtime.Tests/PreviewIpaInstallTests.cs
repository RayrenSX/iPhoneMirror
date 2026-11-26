using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPreviewIpaWorkerTests(string output)
    {
        Directory.CreateDirectory(output);
        var path = Path.GetFullPath(Path.Combine(output, "inspect-fixture.ipa"));
        using (var file = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var info = new StreamWriter(file.CreateEntry("Payload/Test.app/Info.plist").Open()))
                info.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>CFBundleIdentifier</key><string>com.fixture.inspect</string><key>CFBundleExecutable</key><string>Test</string></dict></plist>");
            using var executable = file.CreateEntry("Payload/Test.app/Test").Open();
            executable.Write(new byte[64]);
        }
        async Task Check()
        {
            await using var signing = new IpaSigningSession();
            var result = await signing.RequestAsync(new { action = "inspect", path }, default);
            InteractionAssert(result.GetProperty("bundle_id").GetString() == "com.fixture.inspect",
                "The managed host could not inspect an IPA through the packaged worker.");
            try
            {
                await signing.RequestAsync(new { action = "login", email = "", password = "" }, default);
                throw new InvalidOperationException("The packaged authentication path accepted missing credentials.");
            }
            catch (AutomationException error)
            {
                InteractionAssert(error.Code == "IPA_ACCOUNT_REQUIRED", "Apple ID dependencies are missing from the packaged worker.");
            }
        }
        try { Check().GetAwaiter().GetResult(); }
        finally { File.Delete(path); }
        Console.WriteLine("PASS managed-to-packaged IPA worker: manifest, stdin/stdout, inspection, auth imports and cleanup.");
        return 0;
    }

    private static int RunPreviewIpaInstallTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "settings.json")));
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        void Drain() => AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PreviewIpaInstallWindow Open(IpaTestSigningSession signing, Func<string, CancellationToken, Task> install,
            Func<bool>? current = null, string[]? files = null)
        {
            var window = new PreviewIpaInstallWindow("Test iPhone", "device-001", files ?? [@"C:\Demo\app.ipa"],
                current ?? (() => true), install, signing) { Owner = owner };
            window.Show(); Drain(); return window;
        }
        var files = new[] { @"C:\Demo\APP.IPA", @"C:\Demo\notes.txt" };
        try
        {
            InteractionAssert(PreviewIpaActionWindow.IpaFiles(files).SequenceEqual([files[0]]),
                "IPA routing must be case-insensitive and exclude other files.");
            foreach (var selected in new[] { PreviewIpaAction.Transfer, PreviewIpaAction.Install })
            {
                var choice = new PreviewIpaActionWindow("Test iPhone", files) { Owner = owner };
                choice.Show(); Drain();
                InteractionAssert(choice.SelectedAction is null && choice.FindName("TransferButton") is Button && choice.FindName("InstallButton") is Button,
                    "A drop must offer both actions without choosing one automatically.");
                SaveWindowRender(choice, Path.Combine(output, "ipa-drop-actions.png"));
                Click(choice, selected == PreviewIpaAction.Transfer ? "TransferButton" : "InstallButton");
                InteractionAssert(choice.SelectedAction == selected, "The IPA action choice was lost.");
            }
            using var sign = new IpaTestSigningSession();
            var installed = new List<string>();
            Task Install(string path, CancellationToken _) { installed.Add(path); return Task.CompletedTask; }
            var original = Open(sign, Install);
            InteractionAssert(sign.Requests.Count == 0 && installed.Count == 0, "Opening the install dialog must not sign or install.");
            Click(original, "InstallButton"); Drain();
            InteractionAssert(installed.SequenceEqual([@"C:\Demo\app.ipa"]) && sign.Requests.Single().GetProperty("action").GetString() == "inspect",
                "Keep-signature mode must inspect and install the original IPA without re-signing.");
            original.Close(); Drain();

            foreach (var mode in new[] { 1, 2, 3 })
            {
                using var signer = new IpaTestSigningSession();
                var window = Open(signer, Install);
                ((ComboBox)window.FindName("SigningMode")).SelectedIndex = mode; Drain();
                if (mode == 1)
                {
                    ((TextBox)window.FindName("AppleId")).Text = "test@example.com";
                    ((PasswordBox)window.FindName("ApplePassword")).Password = "secret-on-pipe";
                    Click(window, "LoginButton"); Drain();
                    InteractionAssert(((FrameworkElement)window.FindName("CodePanel")).IsVisible,
                        "Apple ID login must present the two-factor challenge.");
                    ((TextBox)window.FindName("VerificationCode")).Text = "123456";
                    Click(window, "VerifyButton"); Drain();
                    InteractionAssert(((PasswordBox)window.FindName("ApplePassword")).Password.Length == 0,
                        "The password must be cleared after authentication.");
                }
                Click(window, "InstallButton"); Drain();
                var request = signer.Requests.Single(item => item.GetProperty("action").GetString() == "sign");
                InteractionAssert(request.GetProperty("mode").GetString() == new[] { "apple_id", "p12", "pem" }[mode - 1] &&
                    installed.Last() == @"C:\Demo\signed.ipa", "The selected signing mode did not install its output.");
                window.Close(); Drain();
            }
            using (var signer = new IpaTestSigningSession { FailSigning = true })
            {
                var before = installed.Count;
                var window = Open(signer, Install);
                ((ComboBox)window.FindName("SigningMode")).SelectedIndex = 2;
                Click(window, "InstallButton"); Drain();
                InteractionAssert(installed.Count == before && ((TextBlock)window.FindName("StatusText")).Text == LocalizationService.Get("IpaBadCertificate"),
                    "A signing failure must display guidance and prevent installation.");
                window.Close(); Drain();
            }
            using (var signer = new IpaTestSigningSession())
            {
                var current = true;
                var before = installed.Count;
                var window = Open(signer, (path, _) => { installed.Add(path); current = false; return Task.CompletedTask; },
                    () => current, [@"C:\Demo\first.ipa", @"C:\Demo\must-not-follow.ipa"]);
                Click(window, "InstallButton"); Drain();
                InteractionAssert(installed.Count == before + 1, "A changed target must stop the remaining IPA batch.");
                window.Close(); Drain();
            }
            using (var signer = new IpaTestSigningSession { BlockSigning = true })
            {
                var before = installed.Count;
                var window = Open(signer, Install);
                ((ComboBox)window.FindName("SigningMode")).SelectedIndex = 2;
                Click(window, "InstallButton"); Drain();
                Click(window, "CloseButton"); Drain();
                InteractionAssert(!window.IsVisible && installed.Count == before && signer.Disposed,
                    "Cancelling must stop signing, close the worker and prevent installation.");
            }
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                LocalizationService.SetLanguage(culture);
                using var signer = new IpaTestSigningSession();
                var window = Open(signer, Install);
                foreach (var mode in new[] { 0, 1, 2, 3 })
                {
                    ((ComboBox)window.FindName("SigningMode")).SelectedIndex = mode; Drain();
                    window.UpdateLayout();
                    InteractionAssert(window.ActualWidth <= 580 && window.ActualHeight <= 830,
                        $"The IPA install window exceeds its layout bounds in {culture}.");
                    if (culture == "zh-CN") SaveWindowRender(window, Path.Combine(output, $"ipa-signing-mode-{mode}.png"));
                }
                window.Close(); Drain();
            }
            var start = IpaSigningSession.StartInfo(@"C:\Demo\signing-workspace");
            InteractionAssert(start.ArgumentList.SequenceEqual(["--ipa-signing", "--ipa-workspace", @"C:\Demo\signing-workspace"]),
                "Signing credentials must never appear in process arguments.");
            Console.WriteLine("PASS IPA drop choice, four signing modes, 2FA, sign failures, cancellation, stale target and five languages.");
        }
        finally { CloseWorkspaceTestWindow(owner); app.Shutdown(); }
        return 0;
    }

    private sealed class IpaTestSigningSession : IIpaSigningSession, IDisposable
    {
        internal List<JsonElement> Requests { get; } = [];
        internal bool FailSigning, BlockSigning, Disposed;
        public async Task<JsonElement> RequestAsync(object request, CancellationToken cancellation)
        {
            var json = JsonSerializer.SerializeToElement(request);
            Requests.Add(json);
            var action = json.GetProperty("action").GetString();
            if (action == "sign")
            {
                if (FailSigning) throw new AutomationException("IPA_BAD_CERTIFICATE", "");
                if (BlockSigning) await Task.Delay(Timeout.Infinite, cancellation);
                return JsonSerializer.SerializeToElement(new { path = @"C:\Demo\signed.ipa" });
            }
            return JsonSerializer.SerializeToElement(new { status = action == "login" ? "2fa_required" : "authenticated" });
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Dispose() { }
    }
}
