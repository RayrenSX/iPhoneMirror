using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunRevealFrameReview(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            foreach (var mode in new[] { ApplicationDisplayMode.Lightweight, ApplicationDisplayMode.Complete })
            {
                SetApplicationDisplayMode(app, mode);
                var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
                try
                {
                    var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!;
                    KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(panelType, "Devices"));
                    KeyboardCall(window, "SetSettingsPanelVisible", false);
                    FinishWorkspaceReveal(window); DrainDispatcher(); window.UpdateLayout();
                    var panel = (FrameworkElement)window.FindName("ControlPanel");
                    var card = (FrameworkElement)window.FindName("ControlPanelContent");
                    var center = (FrameworkElement)window.FindName("CenterPanel");
                    KeyboardCall(window, "SetSettingsPanelVisible", true);
                    for (var frame = 0; frame < 9; frame++)
                    {
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(25)); window.UpdateLayout();
                        var p = panel.TranslatePoint(new Point(), window);
                        var c = card.TranslatePoint(new Point(), window);
                        InteractionAssert(Math.Abs(card.ActualWidth - 362) < 1,
                            $"{mode}/{frame}: the card first appeared with a narrow layout ({card.ActualWidth}) inside a full-width viewport ({panel.ActualWidth}).");
                        InteractionAssert(Math.Abs(p.X - c.X) < 1,
                            "Card content moved relative to its viewport while opening.");
                        Console.WriteLine($"{mode}/{frame}: window={window.ActualWidth:F1}, center={center.ActualWidth:F1}, viewport={p.X:F1}/{panel.ActualWidth:F1}, card={c.X:F1}/{card.ActualWidth:F1}");
                        SaveWindowRender(window, Path.Combine(output, $"{mode}-{frame}.png"));
                    }
                    FinishWorkspaceReveal(window);
                }
                finally { CloseWorkspaceTestWindow(window); }
            }
            return 0;
        }
        finally { app.Shutdown(); }
    }
}
