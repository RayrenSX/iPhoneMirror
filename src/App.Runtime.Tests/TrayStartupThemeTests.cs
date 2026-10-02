using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunTrayStartupThemeTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", DisplayInstance)!.SetValue(app, true);
        app.InitializeComponent();
        // Match the normal startup theme attachment, including the hidden HWND.
        var attach = typeof(App).Assembly.GetType("IPhoneMirror.App.Services.ThemeService")!
            .GetMethod("Attach", BindingFlags.Static | BindingFlags.NonPublic)!;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => attach.Invoke(null, [sender])));
        var failures = new List<string>();
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var firstMode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                ApplyTheme(typeof(App).Assembly, theme);
                SetApplicationDisplayMode(app, ApplicationDisplayMode.Tray);
                var main = new MainWindow();
                app.MainWindow = main;
                // Keep the actual window lifecycle, but never enumerate devices.
                SetKeyboardField(main, "_startupServicesStarted", true);
                try
                {
                    var context = $"{theme}/{firstMode}";
                    var everVisible = false;
                    main.IsVisibleChanged += (_, _) => everVisible |= main.IsVisible;
                    KeyboardCall(main, "StartInTray");
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                    InteractionAssert(!everVisible && !main.ShowInTaskbar && !main.IsLoaded,
                        "Tray startup must leave the workspace unshown and unloaded.");
                    SelectTrayThemeTestMode(main, firstMode);
                    InteractionAssert(main.IsVisible && main.ShowInTaskbar,
                        $"Selecting {firstMode} in the tray panel must show the workspace.");
                    CheckTrayWorkspaceIconColors(main, $"{context}/first-show", failures,
                        minimumIcons: firstMode == ApplicationDisplayMode.Complete ? 10 : 3);
                    if (firstMode == ApplicationDisplayMode.Lightweight)
                    {
                        main.DataContext.GetType().GetProperty("SelectedApplicationDisplayMode")!
                            .SetValue(main.DataContext, ApplicationDisplayMode.Complete);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(600));
                        CheckTrayWorkspaceIconColors(main, $"{context}/complete", failures);
                    }
                    SaveWindowRender(main, Path.Combine(output, $"{theme}-{firstMode}-first-show.png"));

                    // Reveal the real toolbar without starting a device session.
                    ((FrameworkElement)main.FindName("EnvironmentPanel")).Visibility = Visibility.Visible;
                    var actions = (StackPanel)main.FindName("PreviewQuickActions");
                    actions.Visibility = Visibility.Visible;
                    foreach (var nextTheme in new[] { theme, theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark })
                    {
                        if (nextTheme != theme) ApplyTheme(typeof(App).Assembly, nextTheme);
                        foreach (var enabled in new[] { true, false })
                        {
                            foreach (var button in actions.Children.OfType<Button>()) button.IsEnabled = enabled;
                            foreach (var item in Visuals(main).OfType<NavigationViewItem>()) item.IsActive = enabled;
                            main.UpdateLayout();
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                            CheckTrayWorkspaceIconColors(main, $"{context}/{nextTheme}/enabled={enabled}", failures,
                                minimumIcons: 15);
                        }
                    }
                    main.DataContext.GetType().GetProperty("SelectedApplicationDisplayMode")!
                        .SetValue(main.DataContext, ApplicationDisplayMode.Tray);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                    SelectTrayThemeTestMode(main, ApplicationDisplayMode.Complete);
                    CheckTrayWorkspaceIconColors(main, $"{context}/reopen", failures);
                }
                finally { CloseWorkspaceTestWindow(main); }
            }
        }
        finally { app.Shutdown(); }
        File.WriteAllLines(Path.Combine(output, "failures.txt"), failures);
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"Tray startup theme tests: {failures.Count} failures; dark/light startup, complete/lightweight entry, toolbar states, active navigation, theme changes and reopening checked.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void SelectTrayThemeTestMode(MainWindow main, ApplicationDisplayMode mode)
    {
        KeyboardCall(main, "ShowTrayPanel");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        var panel = (TrayPanelWindow)KeyboardField(main, "_trayPanel");
        InteractionAssert(panel.IsVisible, "The tray panel must be visible before selecting the mode.");
        ((ComboBox)panel.FindName("ModeSelector")).SelectedValue = mode;
        AdvanceDispatcher(TimeSpan.FromMilliseconds(600));
    }

    private static void CheckTrayWorkspaceIconColors(Window window, string context, List<string> failures,
        int minimumIcons = 10)
    {
        var checkedIcons = 0;
        foreach (var icon in Visuals(window).OfType<SymbolIcon>().Where(icon => icon.IsVisible))
        {
            DependencyObject? parent = VisualTreeHelper.GetParent(icon);
            while (parent is not null && parent is not Button && parent is not NavigationViewItem)
                parent = VisualTreeHelper.GetParent(parent);
            if (parent is not Control control) continue;
            checkedIcons++;
            if (!TrayIconColorEquals(icon.Foreground, control.Foreground))
                failures.Add($"{context}: {icon.Symbol} foreground {icon.Foreground}, expected {control.Foreground}.");
            var glyphs = Visuals(icon).OfType<TextBlock>().ToArray();
            InteractionAssert(glyphs.Length > 0, $"{context}: {icon.Symbol} has no rendered glyph.");
            foreach (var glyph in glyphs)
                if (!TrayIconColorEquals(glyph.Foreground, control.Foreground))
                    failures.Add($"{context}: {icon.Symbol} glyph {glyph.Foreground}, expected {control.Foreground}.");
        }
        InteractionAssert(checkedIcons >= minimumIcons, $"{context}: Only {checkedIcons} workspace icons were checked.");
    }

    private static bool TrayIconColorEquals(Brush actual, Brush expected) =>
        actual is SolidColorBrush a && expected is SolidColorBrush b && a.Color == b.Color;
}
