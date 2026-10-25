using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunUpdaterWindowRegressionTests()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror-UpdateWindow-" + Guid.NewGuid().ToString("N"));
        var owner = new Wpf.Ui.Controls.FluentWindow { Width = 200, Height = 150, ShowInTaskbar = false };
        app.MainWindow = owner;
        owner.Show();
        try
        {
            var asset = new ReleaseAsset("iPhoneMirror-Setup-v99.0.0-x64.exe",
                new Uri("https://github.com/RayrenSX/iPhoneMirror/releases/download/v99.0.0/iPhoneMirror-Setup-v99.0.0-x64.exe"),
                1024, new string('A', 64));
            var release = new ReleaseInfo("v99.0.0", "Updater regression", "Test update", DateTimeOffset.UtcNow,
                SemanticVersion.Parse("99.0.0"), false, asset, null, null);
            using var handler = new PendingUpdateHandler();
            using var http = new HttpClient(handler);
            using var client = new GitHubReleaseClient(http, root);
            var readOnly = new UpdateWindow(release, client, false, false, readOnlyPreview: true) { Owner = owner };
            readOnly.Show();
            var task = StartWindowUpdate(readOnly);
            PumpUpdateTask(task);
            InteractionAssert(handler.Requests == 0, "A read-only update preview started a download.");
            readOnly.Close();

            var window = new UpdateWindow(release, client, false, false) { Owner = owner };
            window.Show();
            task = StartWindowUpdate(window);
            var elapsed = Stopwatch.StartNew();
            while (handler.Requests == 0 && elapsed.Elapsed < TimeSpan.FromSeconds(5))
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            InteractionAssert(handler.Requests > 0 && !task.IsCompleted, "The window did not start the controlled download.");

            // A later handler can cancel Closing. The window must remain usable
            // and its live download must not be cancelled or disposed yet.
            CancelEventHandler veto = (_, e) => e.Cancel = true;
            window.Closing += veto;
            window.Close();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            InteractionAssert(window.IsVisible && handler.Cancellations == 0 && !task.IsCompleted,
                "A cancelled window close destroyed an active update operation.");
            window.Closing -= veto;
            window.Close();
            PumpUpdateTask(task);
            InteractionAssert(handler.Cancellations > 0, "Closing the update window did not cancel outstanding requests.");
            var requests = handler.Requests;
            PumpUpdateTask(StartWindowUpdate(window));
            InteractionAssert(handler.Requests == requests, "A closed update window started another download.");
            Console.WriteLine("Updater window regressions passed: read-only preview, cancelled Closing, download cancellation and callbacks after disposal.");
            return 0;
        }
        finally
        {
            owner.Close();
            app.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Task StartWindowUpdate(UpdateWindow window) =>
        (Task)typeof(UpdateWindow).GetMethod("DownloadAndInstallAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    private static void PumpUpdateTask(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        InteractionAssert(task.IsCompleted, "The update UI did not complete after cancellation.");
        task.GetAwaiter().GetResult();
    }

    private sealed class PendingUpdateHandler : HttpMessageHandler
    {
        internal int Requests;
        internal int Cancellations;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancellations); throw; }
            throw new InvalidOperationException("Controlled update request should only end through cancellation.");
        }
    }
}
