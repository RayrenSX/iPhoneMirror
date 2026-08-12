using System.Reflection;
using System.Windows;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Updater;
using Wpf.Ui.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfFlowDocumentScrollViewer = System.Windows.Controls.FlowDocumentScrollViewer;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using WpfThumb = System.Windows.Controls.Primitives.Thumb;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace IPhoneMirror.App.Runtime.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            TestUpdateWindowThemeSwitch();
            Console.WriteLine("App runtime tests passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void TestHongKongLocalizationSwitch(Application application,
        Assembly assembly)
    {
        var localizationService = assembly.GetType(
            "IPhoneMirror.App.Localization.LocalizationService", throwOnError: true)!;
        var applyLanguage = localizationService.GetMethod("ApplyLanguage",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(localizationService.FullName, "ApplyLanguage");

        applyLanguage.Invoke(null, ["zh-HK", false, false]);
        if (application.TryFindResource("StartMirroring") is not string start ||
            !start.Contains("螢幕鏡像", StringComparison.Ordinal) ||
            application.TryFindResource("NavigationTextFontFamily") is not FontFamily font ||
            !font.Source.Equals("Microsoft JhengHei UI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Hong Kong localization dictionary did not load at runtime.");
        applyLanguage.Invoke(null, ["system", false, false]);
    }

    private static void TestUpdateWindowThemeSwitch()
    {
        var application = new App();
        application.InitializeComponent();
        var assembly = typeof(App).Assembly;
        TestHongKongLocalizationSwitch(application, assembly);

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
        var owner = new FluentWindow
        {
            Width = 320,
            Height = 240,
            ShowInTaskbar = false,
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = WindowBackdropType.Mica,
        };
        application.MainWindow = owner;
        owner.Show();
        try
        {
            var windowType = assembly.GetType(
                "IPhoneMirror.App.Windows.UpdateWindow", throwOnError: true)!;
            var window = Activator.CreateInstance(windowType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [release, client, false, true], culture: null) as Window ??
                throw new InvalidOperationException("Update window was not constructed.");
            window.Owner = owner;
            ((FluentWindow)window).WindowBackdropType = WindowBackdropType.Acrylic;
            window.Show();
            try
            {
                var updateButton = window.FindName("UpdateActionButton") as WpfButton ??
                    throw new InvalidOperationException("Update action button was not found.");
                var releaseNotesViewer =
                    window.FindName("ReleaseNotesViewer") as WpfFlowDocumentScrollViewer ??
                    throw new InvalidOperationException("Release notes viewer was not found.");
                window.UpdateLayout();
                ApplyTheme(assembly, AppTheme.Light);
                AssertThemeBrush(window, "TextBrush", Color.FromRgb(0x1D, 0x1D, 0x1F));
                AssertThemeBrush(window, "AboutCheckUpdatesTextBrush", Colors.White);
                AssertPrimaryButtonText(updateButton, Colors.White);
                AssertModernScrollBar(releaseNotesViewer);
                AssertBackdropBackground(window);

                ApplyTheme(assembly, AppTheme.Dark);
                AssertThemeBrush(window, "TextBrush", Color.FromRgb(0xF5, 0xF5, 0xF7));
                AssertThemeBrush(window, "AboutCheckUpdatesTextBrush",
                    Color.FromRgb(0x0F, 0x14, 0x19));
                AssertPrimaryButtonText(updateButton, Color.FromRgb(0x0F, 0x14, 0x19));
                AssertBackdropBackground(window);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            ((IDisposable)client).Dispose();
            owner.Close();
            application.Shutdown();
        }
    }

    private static void ApplyTheme(Assembly assembly, AppTheme theme)
    {
        var themeService = assembly.GetType(
            "IPhoneMirror.App.Services.ThemeService", throwOnError: true)!;
        var apply = themeService.GetMethod("Apply",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(themeService.FullName, "Apply");
        apply.Invoke(null, [theme]);
    }

    private static void AssertThemeBrush(Window window, string resourceKey,
        Color expectedColor)
    {
        if (window.TryFindResource(resourceKey) is not SolidColorBrush brush ||
            brush.Color != expectedColor)
            throw new InvalidOperationException(
                $"Child window did not refresh {resourceKey} for the active theme.");
    }

    private static void AssertPrimaryButtonText(WpfButton button, Color expectedColor)
    {
        button.ApplyTemplate();
        if (button.Template.FindName("PrimaryLabel", button) is not WpfTextBlock label ||
            label.Foreground is not SolidColorBrush brush || brush.Color != expectedColor)
            throw new InvalidOperationException(
                $"Primary button text did not use the expected theme color {expectedColor}.");
    }

    private static void AssertModernScrollBar(DependencyObject root)
    {
        var scrollBar = FindVisualDescendant<WpfScrollBar>(root,
            candidate => candidate.Orientation == System.Windows.Controls.Orientation.Vertical) ??
            throw new InvalidOperationException(
                "Release notes did not create a vertical scrollbar.");
        scrollBar.ApplyTemplate();
        if (scrollBar.Width != 6 ||
            scrollBar.RenderTransform is not TranslateTransform { X: 6 } ||
            scrollBar.Template.FindName("ScrollThumb", scrollBar) is not WpfThumb thumb ||
            thumb.Width != 2)
            throw new InvalidOperationException(
                "Release notes did not use the shared modern scrollbar template.");
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && match(candidate)) return candidate;
            var descendant = FindVisualDescendant(child, match);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static void AssertBackdropBackground(Window window)
    {
        if (window is not FluentWindow fluentWindow ||
            fluentWindow.WindowBackdropType == WindowBackdropType.None ||
            !WindowBackdrop.IsSupported(fluentWindow.WindowBackdropType))
            return;
        if (window.Background is not SolidColorBrush brush ||
            brush.Color != Colors.Transparent)
            throw new InvalidOperationException(
                "Child window backdrop kept a stale themed background after switching themes.");
    }
}
