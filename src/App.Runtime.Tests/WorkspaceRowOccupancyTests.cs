using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWorkspaceRowOccupancyTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                SetApplicationDisplayMode(app, mode);
                var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                try
                {
                    window.MinWidth = 600; window.MinHeight = 400;
                    var grid = (Grid)window.FindName("MainContentGrid");
                    var left = (FrameworkElement)window.FindName("LeftPanelHost");
                    var settings = (FrameworkElement)window.FindName("ControlPanel");
                    var center = (FrameworkElement)window.FindName("CenterPanel");
                    var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
                    void Check()
                    {
                        InteractionAssert(grid.RowDefinitions.Count == 0 && Grid.GetRow(settings) == 0 && Grid.GetColumn(settings) == 4 &&
                            Grid.GetRow(left) == 0 && Grid.GetColumn(left) == 0 && Grid.GetRowSpan(center) == 1,
                            "A resize or reveal moved sidebars into vertical rows.");
                        if (center.ActualWidth > 0)
                            InteractionAssert(center.ActualWidth >= 155, $"Preview was squeezed to {center.ActualWidth} DIP.");
                        if (left.IsVisible)
                            InteractionAssert(Math.Abs(left.TranslatePoint(new Point(), grid).Y) < 1 && Math.Abs(left.ActualHeight - grid.ActualHeight) < 1,
                                "The source sidebar is no longer aligned with the preview top and bottom.");
                        if (settings.IsVisible)
                            InteractionAssert(Math.Abs(settings.TranslatePoint(new Point(), grid).Y) < 1 && Math.Abs(settings.ActualHeight - grid.ActualHeight) < 1,
                                "Settings moved down or reserved a blank half-window.");
                    }
                    foreach (var width in new[] { 768d, 900d, 1280d, 960d, 1800d, 768d })
                    {
                        window.Width = width; window.Height = 800;
                        KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "Devices"));
                        KeyboardCall(window, "SetSettingsPanelVisible", true);
                        KeyboardCall(window, "ApplyWorkspacePanelState", false);
                        window.UpdateLayout(); DrainDispatcher(); window.UpdateLayout();
                        Check();
                        foreach (var handler in new[] { "OnNavigateDevicesClick", "OnNavigateDevicesClick", "OnNavigateMirroringClick",
                            "OnNavigateSettingsClick", "OnNavigateSettingsClick" })
                        {
                            KeyboardCall(window, handler, window, new RoutedEventArgs());
                            for (var frame = 0; frame < 4; frame++)
                            {
                                AdvanceDispatcher(TimeSpan.FromMilliseconds(35));
                                window.UpdateLayout(); DrainDispatcher(); window.UpdateLayout(); Check();
                            }
                        }
                        FinishWorkspaceReveal(window); DrainDispatcher(); window.UpdateLayout(); Check();
                        SaveWindowRender(window, Path.Combine(output, $"{mode}-{width}.png"));
                    }
                    Console.WriteLine($"PASS {mode}: one horizontal row at six widths, real navigation/reversals, all sampled transition frames and usable preview width.");
                }
                finally { CloseWorkspaceTestWindow(window); }
            }
            TestWorkspaceRevealGeometry(app);
            return 0;
        }
        finally { app.Shutdown(); }
    }
}
