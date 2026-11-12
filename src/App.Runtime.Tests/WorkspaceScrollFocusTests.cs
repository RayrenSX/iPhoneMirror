using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.UI.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWorkspaceScrollFocusTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        try
        {
            var panelType = typeof(MainWindow).GetNestedType("LeftWorkspacePanel", System.Reflection.BindingFlags.NonPublic)!;
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ApplyTheme(typeof(App).Assembly, theme);
                foreach (var panel in new[] { "Devices", "Mirroring", "Devices" })
                {
                    KeyboardCall(owner, "SetLeftWorkspacePanel", Enum.Parse(panelType, panel));
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(400));
                    var frame = (FrameworkElement)owner.FindName(panel == "Devices" ? "DevicePanel" : "MirroringPanel");
                    var viewer = Visuals(frame).OfType<ScrollViewer>().First();
                    owner.Activate();
                    Keyboard.Focus(viewer);
                    DrainDispatcher();
                    Console.WriteLine($"{theme}/{panel}: focused={viewer.IsKeyboardFocused}, focus visual={viewer.FocusVisualStyle?.TargetType.Name ?? "null"}");
                    InteractionAssert(viewer.FocusVisualStyle is null,
                        $"{theme}/{panel}: a scroll container can draw the unwanted full-panel focus frame.");
                    InteractionAssert(viewer.Focusable,
                        "Scroll containers must retain keyboard scrolling support.");
                    var button = Visuals(frame).OfType<Button>().First(b => b.IsVisible && b.IsEnabled);
                    InteractionAssert(button.FocusVisualStyle is not null,
                        "Removing the container frame removed a button's keyboard focus indicator.");
                    InteractionAssert(button.Focus(), "Child buttons cannot receive keyboard focus.");
                    SaveWindowRender(owner, Path.Combine(output, $"{theme}-{panel}.png"));
                }
                foreach (var category in new[] { "Application", "Projection", "Control" })
                {
                    var viewer = (ScrollViewer)owner.FindName(category + "SettingsScrollViewer");
                    InteractionAssert(viewer.FocusVisualStyle is null,
                        $"{category}: settings scroll containers retain the same unwanted focus frame.");
                }
            }
            Console.WriteLine("PASS: repeated panel switching and focus in both themes; container frames suppressed and button focus retained.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(owner); app.Shutdown(); }
    }
}
