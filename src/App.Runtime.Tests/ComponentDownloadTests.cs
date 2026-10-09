using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;
using IPhoneMirror.App.Localization;
using System.Net;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunComponentDownloadTests(string output)
    {
        Directory.CreateDirectory(output);
        var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(application, true);
        application.InitializeComponent();
        var assembly = typeof(App).Assembly;
        var localization = assembly.GetType("IPhoneMirror.App.Localization.LocalizationService", true)!;
        var languageMethod = localization.GetMethod("ApplyLanguage", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var language in new[] { "zh-CN", "zh-HK", "zh-TW", "en-US" })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            languageMethod.Invoke(null, [language, false, false]);
            ApplyTheme(assembly, theme);
            var window = new ComponentDownloadWindow(true);
            try
            {
                typeof(ComponentDownloadWindow).GetProperty("Percentage")!.SetValue(window, 43.2);
                typeof(ComponentDownloadWindow).GetProperty("IsIndeterminate")!.SetValue(window, false);
                typeof(ComponentDownloadWindow).GetProperty("StatusText")!.SetValue(window,
                    LocalizationService.Format("DownloadProgressFormat", 43.2));
                typeof(ComponentDownloadWindow).GetProperty("DetailText")!.SetValue(window, "30.5 / 70.6 MB   ·   8.2 MB/s");
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                if (window.ActualWidth < 480 || window.ActualHeight < 320)
                    throw new InvalidOperationException("Component progress dialog has invalid dimensions.");
                SaveWindowRender(window, Path.Combine(output, $"uxplay-{language}-{theme}.png"));
                CheckComponentLayout(window);
                window.Width = 480;
                window.Height = 320;
                typeof(ComponentDownloadWindow).GetProperty("StatusText")!.SetValue(window, LocalizationService.Get("UxPlayDownloadFailed"));
                typeof(ComponentDownloadWindow).GetProperty("DetailText")!.SetValue(window, LocalizationService.Get("UxPlayDownloadNotFound"));
                window.UpdateLayout();
                SaveWindowRender(window, Path.Combine(output, $"uxplay-small-error-{language}-{theme}.png"));
                CheckComponentLayout(window);
                IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(application, 1.5, false);
                window.UpdateLayout();
                CheckComponentLayout(window);
                SaveWindowRender(window, Path.Combine(output, $"uxplay-large-text-{language}-{theme}.png"));
                IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(application, 1, false);
            }
            finally { window.Close(); }
        }
        TestComponentDownloadStates(output);
        application.Shutdown();
        Console.WriteLine("PASS component UI: three languages, both themes, minimum size, 150% text, live language switch, error/retry, phase order, duplicate click, cancellation and success races.");
        return 0;
    }

    private static void CheckComponentLayout(ComponentDownloadWindow window)
    {
        var progress = ComponentVisuals<ProgressBar>(window).Single();
        var progressBounds = progress.TransformToAncestor(window).TransformBounds(new Rect(progress.RenderSize));
        var actions = ComponentVisuals<Button>(window).Where(b => b.IsVisible &&
            AutomationProperties.GetAutomationId(b) is "UxPlayDownloadCancel" or "UxPlayDownloadRetry").ToArray();
        if (actions.Length != (window.CanRetry ? 2 : 1))
            throw new Exception("Component dialog visible actions do not match retry state");
        foreach (var action in actions)
        {
            var actionBounds = action.TransformToAncestor(window).TransformBounds(new Rect(action.RenderSize));
            if (progress.ActualWidth <= 100 || progressBounds.Bottom > actionBounds.Top)
                throw new Exception($"Component dialog progress/footer overlap: {progressBounds}, {actionBounds}");
            if (actionBounds.Left < 0 || actionBounds.Right > window.ActualWidth)
                throw new Exception("Component dialog action is clipped horizontally");
            if (actionBounds.Bottom > window.ActualHeight)
            {
                // Short work areas intentionally use the shared dialog's outer scroll.
                var viewport = ComponentVisuals<IPhoneMirror.UI.Controls.DialogViewport>(window).Single();
                viewport.ScrollToEnd(); window.UpdateLayout();
                actionBounds = action.TransformToAncestor(window).TransformBounds(new Rect(action.RenderSize));
                if (actionBounds.Bottom > window.ActualHeight || actionBounds.Top < 0)
                    throw new Exception("Short dialog actions cannot be reached by scrolling");
                viewport.ScrollToTop(); window.UpdateLayout();
            }
        }
        foreach (var text in ComponentVisuals<TextBlock>(window))
            if (!string.IsNullOrWhiteSpace(text.Text) && text.ActualWidth <= 0)
                throw new Exception("Component text has no layout width");
    }

    private static IEnumerable<T> ComponentVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in ComponentVisuals<T>(child)) yield return descendant;
        }
    }

    private static void ComponentPumpUntil(Func<bool> done)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Component UI state did not settle");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void TestComponentDownloadStates(string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var calls = 0;
        IProgress<UpdateDownloadProgress>? oldProgress = null, currentProgress = null;
        Action? installing = null;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var window = new ComponentDownloadWindow(true, (progress, install, token) =>
        {
            calls++;
            if (calls == 1) { oldProgress = progress; return Task.FromException(new HttpRequestException("fixture 404", null, HttpStatusCode.NotFound)); }
            currentProgress = progress; installing = install;
            token.Register(() => { cancelled = true; pending.TrySetCanceled(token); });
            return pending.Task;
        });
        Task Start() => (Task)typeof(ComponentDownloadWindow).GetMethod("DownloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        window.Show();
        if (window.CanRetry) throw new Exception("Retry exposed before failure");
        var first = Start(); ComponentPumpUntil(() => first.IsCompleted);
        if (!window.CanRetry || window.IsIndeterminate || !window.DetailText.StartsWith(LocalizationService.Get("UxPlayDownloadNotFound")) ||
            !window.DetailText.Contains("HTTP: 404") ||
            window.DetailText.Contains(Services.UxPlayComponent.Descriptor!.Url)) throw new Exception("404/retry must not display a stale embedded download URL");
        typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["zh-CN", false, true]);
        if (window.StatusText != LocalizationService.Get("UxPlayDownloadFailed") || !window.DetailText.StartsWith(LocalizationService.Get("UxPlayDownloadNotFound")))
            throw new Exception("Open dialog did not refresh its language");
        typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["en-US", false, true]);
        window.Width = 480; window.Height = 320; window.UpdateLayout();
        CheckComponentLayout(window);
        SaveWindowRender(window, Path.Combine(output, "uxplay-real-failure-retry.png"));
        foreach (var language in new[] { "zh-CN", "zh-HK", "zh-TW", "en-US" })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [language, false, true]);
            ApplyTheme(typeof(App).Assembly, theme);
            IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(Application.Current, 1.5, false);
            window.UpdateLayout();
            CheckComponentLayout(window);
            SaveWindowRender(window, Path.Combine(output, $"uxplay-real-failure-large-text-{language}-{theme}.png"));
            var viewport = ComponentVisuals<IPhoneMirror.UI.Controls.DialogViewport>(window).Single();
            viewport.ScrollToEnd(); window.UpdateLayout();
            SaveWindowRender(window, Path.Combine(output, $"uxplay-real-failure-large-text-actions-{language}-{theme}.png"));
            viewport.ScrollToTop(); window.UpdateLayout();
            IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(Application.Current, 1, false);
        }
        var second = Start(); var duplicate = Start();
        if (calls != 2 || !duplicate.IsCompleted || window.CanRetry) throw new Exception("Duplicate install or retry while busy");
        currentProgress!.Report(new(0, null, 0, UpdateDownloadPhase.ConnectivityTest));
        ComponentPumpUntil(() => window.StatusText == LocalizationService.Get("UxPlayDownloadFindingMirror"));
        currentProgress.Report(new(0, null, 0, UpdateDownloadPhase.ThroughputTest));
        ComponentPumpUntil(() => window.StatusText == LocalizationService.Get("UxPlayDownloadTestingMirror"));
        currentProgress.Report(new(500, 1000, 50));
        ComponentPumpUntil(() => window.Percentage == 50);
        oldProgress!.Report(new(999, 1000, 50));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.Percentage != 50) throw new Exception("Stale attempt overwrote retry progress");
        currentProgress.Report(new(1000, 1000, 0, UpdateDownloadPhase.Verification));
        ComponentPumpUntil(() => window.StatusText == LocalizationService.Get("VerifyingDownload"));
        installing!(); currentProgress.Report(new(1, 1000, 1));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.StatusText != LocalizationService.Get("UxPlayDownloadInstalling")) throw new Exception("Late progress replaced installation state");
        window.Close(); ComponentPumpUntil(() => second.IsCompleted && !window.IsVisible);
        if (!cancelled || window.CanRetry) throw new Exception("Cancel did not terminate install");

        // Real modal success path (DialogResult can only be set on a modal window).
        var success = new ComponentDownloadWindow(false, (_, _, _) => Task.CompletedTask);
        if (success.ShowDialog() != true) throw new Exception("Completed install must accept dialog");
        // User close wins even if the operation finishes successfully after cancel.
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var race = new ComponentDownloadWindow(false, (_, _, _) => finish.Task);
        race.Loaded += (_, _) => race.Dispatcher.BeginInvoke(() => { race.Close(); finish.SetResult(); });
        if (race.ShowDialog() == true) throw new Exception("Close/success race incorrectly accepted dialog");
    }
}
