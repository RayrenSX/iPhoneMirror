using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunAboutUsabilityTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var fixture = Path.Combine(Path.GetFullPath(output), "native-log.txt");
        File.WriteAllText(fixture, string.Empty);
        var previousLogFile = Environment.GetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE");
        MainWindow owner;
        try
        {
            Environment.SetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE", fixture);
            owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        }
        finally { Environment.SetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE", previousLogFile); }
        var originalClient = KeyboardField(app, "_releaseClient");
        using var handler = new AboutPendingUpdateHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new GitHubReleaseClient(http, Path.Combine(Path.GetFullPath(output), "updates"));
        SetKeyboardField(app, "_releaseClient", client);
        var vm = (MainViewModel)owner.DataContext;
        var about = new AboutWindow(app, vm) { Owner = owner };
        void Publish(string text) => typeof(MainViewModel).GetProperty(nameof(MainViewModel.LogText))!
            .SetValue(vm, text);
        void Click(string name) => ((Button)about.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            about.Show();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
            var log = (TextBox)about.FindName("LiveLogTextBox");
            var timer = (DispatcherTimer)KeyboardField(about, "_logTimer");
            var entries = string.Join('\n', Enumerable.Range(0, 50).Select(index =>
                $"{index:000} [thread={new string('x', 75)}] Capture message that must remain readable."));
            Publish(entries);
            DrainDispatcher();
            log.Select(30, 16);
            var selected = log.SelectedText;
            InteractionAssert(about.IsLogPaused && !timer.IsEnabled,
                "Selecting log text must pause updates before they can replace the selection.");
            Publish("New entry received while the user is reading.");
            DrainDispatcher();
            InteractionAssert(log.Text == entries && log.SelectedText == selected,
                "An incoming log update replaced the paused text or selection.");
            about.Hide(); about.Show();
            DrainDispatcher();
            InteractionAssert(about.IsLogPaused && !timer.IsEnabled && log.Text == entries,
                "Hiding and showing the About window lost the user's paused log.");
            Click("LogPauseButton");
            DrainDispatcher();
            InteractionAssert(!about.IsLogPaused && timer.IsEnabled && log.SelectionLength == 0 &&
                log.Text == vm.LogText && log.Text.Contains("New entry"),
                "Resuming must show the latest entries and release the old selection.");
            Click("LogPauseButton");
            Click("LogClearButton");
            DrainDispatcher();
            InteractionAssert(about.IsLogPaused && log.Text == LocalizationService.Get("LogViewCleared"),
                "Clear view must work while paused without resuming log updates.");
            Publish(entries);
            DrainDispatcher();
            InteractionAssert(log.Text == LocalizationService.Get("LogViewCleared"),
                "A paused, cleared view was repopulated by an incoming update.");
            Click("LogPauseButton");
            DrainDispatcher();
            log.ScrollToEnd();
            log.UpdateLayout();
            InteractionAssert(log.ExtentWidth <= log.ViewportWidth + 2 && log.Text == entries,
                "Long log entries require horizontal scrolling or lose their original text.");
            log.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            DrainDispatcher();
            InteractionAssert(about.IsLogPaused && !timer.IsEnabled,
                "Scrolling back through the log must pause incoming updates.");
            InteractionAssert((string)((Button)about.FindName("LogPauseButton")).Content == LocalizationService.Get("LogResume"),
                "A paused log must offer a visible Resume action.");
            SaveWindowRender(about, Path.Combine(output, "paused-readable-log.png"));

            Click("CheckUpdatesButton");
            Click("CheckUpdatesButton");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            var check = (Button)about.FindName("CheckUpdatesButton");
            InteractionAssert(handler.Requests == 1 && !check.IsEnabled &&
                (string)check.Content == LocalizationService.Get("CheckingForUpdates"),
                "Repeated update clicks started duplicate requests or lacked busy feedback.");
            handler.CompleteEmptyReleaseList();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
            InteractionAssert(check.IsEnabled && about.UpdateStatus == LocalizationService.Get("AlreadyUpToDate"),
                $"An update result did not restore the action and report its outcome (enabled={check.IsEnabled}, status={about.UpdateStatus}, requests={handler.Requests}).");
            Click("CheckUpdatesButton");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            InteractionAssert(handler.Requests == 2, "A completed update check could not be retried.");
            about.Close();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(handler.CanceledRequests == 1 && !timer.IsEnabled && !about.CanCheckForUpdates,
                "Closing About must cancel its pending update check and stop log polling.");
            File.WriteAllText(Path.Combine(output, "usability-results.json"), JsonSerializer.Serialize(new
            {
                selectionPreserved = true, pausedViewStable = true, clearWhilePaused = true,
                resumeShowsLatest = true, wrappedLogTextPreserved = true, scrollBackPauses = true,
                duplicateUpdateRequestsPrevented = true, updateBusyFeedback = true,
                successfulUpdateCheckRetry = true, pendingUpdateCanceledOnClose = true,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS About usability: stable paused log selection, wrap, clear/resume, scroll-back pause, update feedback/deduplication/retry and close cancellation.");
            return 0;
        }
        finally
        {
            about.Close();
            SetKeyboardField(app, "_releaseClient", originalClient);
            CloseWorkspaceTestWindow(owner);
            app.Shutdown();
        }
    }

    private sealed class AboutPendingUpdateHandler : HttpMessageHandler
    {
        private TaskCompletionSource<HttpResponseMessage> _pending = NewPending();
        internal int Requests { get; private set; }
        internal int CanceledRequests { get; private set; }
        private static TaskCompletionSource<HttpResponseMessage> NewPending() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ++Requests;
            try { return await _pending.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { ++CanceledRequests; throw; }
        }

        internal void CompleteEmptyReleaseList()
        {
            var pending = _pending;
            _pending = NewPending();
            pending.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                        "https://api.github.com/repos/RayrenSX/iPhoneMirror/releases?per_page=20"),
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
        }
    }
}
