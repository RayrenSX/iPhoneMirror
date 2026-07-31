using System.Reflection;
using System.Windows;
using IPhoneMirror.App;

namespace IPhoneMirror.App.Runtime.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            ConstructUpdateWindow();
            Console.WriteLine("App runtime tests passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void ConstructUpdateWindow()
    {
        var application = new App();
        application.InitializeComponent();
        var assembly = typeof(App).Assembly;

        var parserType = assembly.GetType(
            "IPhoneMirror.App.Updater.ReleaseParser", throwOnError: true)!;
        var parseLatest = parserType.GetMethod("ParseLatest",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(parserType.FullName, "ParseLatest");
        const string releaseJson = """
            [{
              "tag_name": "v99.0.0",
              "name": "Update window runtime test",
              "body": "# Changes\nRuntime XAML construction test",
              "published_at": "2026-07-31T00:00:00Z",
              "draft": false,
              "prerelease": false,
              "assets": [{
                "name": "iPhoneMirror-Setup-v99.0.0-x64.exe",
                "size": 1,
                "browser_download_url": "https://github.com/RayrenSX/iPhoneMirror/releases/download/v99.0.0/iPhoneMirror-Setup-v99.0.0-x64.exe"
              }]
            }]
            """;
        var release = parseLatest.Invoke(null, [releaseJson, true, false]) ??
            throw new InvalidOperationException("Release fixture was not parsed.");

        var clientType = assembly.GetType(
            "IPhoneMirror.App.Updater.GitHubReleaseClient", throwOnError: true)!;
        var client = Activator.CreateInstance(clientType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [null, null], culture: null) ??
            throw new InvalidOperationException("Update client was not constructed.");
        try
        {
            var windowType = assembly.GetType(
                "IPhoneMirror.App.Windows.UpdateWindow", throwOnError: true)!;
            var window = Activator.CreateInstance(windowType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [release, client, false, true], culture: null) as Window ??
                throw new InvalidOperationException("Update window was not constructed.");
            window.Close();
        }
        finally
        {
            ((IDisposable)client).Dispose();
            application.Shutdown();
        }
    }
}
