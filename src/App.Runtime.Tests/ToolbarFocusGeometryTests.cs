using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;
using IPhoneMirror.SharedUI.Services;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunToolbarFocusGeometryTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        // Production installs this from OnStartup; preview tests normally
        // bypass startup and therefore missed its sizing of the focus stage.
        WindowWorkAreaController.EnableForApplication();
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        KeyboardCall(main, "SetSettingsPanelVisible", true);
        FinishWorkspaceReveal(main); DrainDispatcher(); main.UpdateLayout();
        ((RadioButton)main.FindName("ApplicationSettingsTab")).IsChecked = true;
        var toolbar = (FrameworkElement)main.FindName("EnvironmentPanel");
        var catalog = ((Panel)main.FindName("PreviewQuickActions")).Children.OfType<Button>().Select(b =>
            new ToolbarAction(AutomationProperties.GetAutomationId(b), AutomationProperties.GetName(b),
                ((StackPanel)b.Content).Children.OfType<IPhoneMirror.App.Controls.ThemedSymbolIcon>().First().Symbol)).ToArray();
        PreviewToolbarSettingsWindow? editor = null;
        void Settle() { DrainDispatcher(); main.UpdateLayout(); editor?.UpdateLayout(); }
        void AssertCoverage(string scenario)
        {
            Settle();
            var ownerOrigin = main.PointToScreen(new Point());
            var ownerEnd = main.PointToScreen(new Point(main.ActualWidth, main.ActualHeight));
            var editorOrigin = editor!.PointToScreen(new Point());
            var editorEnd = editor.PointToScreen(new Point(editor.ActualWidth, editor.ActualHeight));
            InteractionAssert(Math.Abs(editorOrigin.X - ownerOrigin.X) <= 2 && Math.Abs(editorOrigin.Y - ownerOrigin.Y) <= 2 &&
                Math.Abs(editorEnd.X - ownerEnd.X) <= 2 && Math.Abs(editorEnd.Y - ownerEnd.Y) <= 2,
                $"{scenario}: focus stage does not cover its owner: owner={ownerOrigin}..{ownerEnd}; stage={editorOrigin}..{editorEnd}.");
            var root = (FrameworkElement)editor.FindName("EditorRoot");
            InteractionAssert(Math.Abs(root.ActualWidth - editor.ActualWidth) < 1 &&
                Math.Abs(root.ActualHeight - editor.ActualHeight) < 1, "Editor content no longer fills its physical stage.");
            if (FocusTestGetDwmAttribute(new WindowInteropHelper(editor).Handle, 33, out var corners, sizeof(int)) == 0)
                InteractionAssert(corners == (main.WindowCornerPreference == Wpf.Ui.Controls.WindowCornerPreference.DoNotRound ? 1 : 2),
                    $"{scenario}: stage corners reveal the unshaded owner behind the fullscreen surface.");
            var target = editor.TargetToolbarBounds;
            InteractionAssert(target.Left >= 0 && target.Right <= root.ActualWidth && target.Bottom < root.ActualHeight,
                $"{scenario}: focused toolbar left the stage.");
            var footer = (FrameworkElement)editor.FindName("SettingsPanel");
            var footerBounds = footer.TransformToAncestor(root).TransformBounds(new Rect(footer.RenderSize));
            InteractionAssert(footerBounds.Left >= 0 && footerBounds.Right <= root.ActualWidth + 1 && footerBounds.Bottom <= root.ActualHeight,
                $"{scenario}: footer clipped after resizing.");
            SaveWindowRender(editor, Path.Combine(output, scenario + ".png"));
        }
        try
        {
            foreach (var textScale in new[] { 1d, 1.5 })
            {
                IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, textScale, false);
                KeyboardCall(main, "MaximizeWindow", (Rect?)null); Settle();
                editor = new PreviewToolbarSettingsWindow(new(), catalog, main.ApplyPreviewToolbar, _ => true)
                { Owner = main, FocusAnimationEnabled = false };
                editor.ConfigureToolbarFocus(main, toolbar);
                editor.Show(); Settle();
                AssertCoverage($"maximized-{textScale:0.0}");
                KeyboardCall(main, "RestoreWindowFromMaximized"); Settle();
                foreach (var (width, height) in new[] { (1100d, 740d), (1800d, 1000d), (960d, 800d) })
                {
                    var hwnd = new WindowInteropHelper(main).Handle;
                    var dpi = RepairGetDpiForWindow(hwnd) / 96d;
                    InteractionAssert(PerfSetWindowPos(hwnd, 0, 0, 0, (int)Math.Round(width * dpi),
                        (int)Math.Round(height * dpi), 0x0016), "Could not resize the owner HWND.");
                    AssertCoverage($"resize-{textScale:0.0}-{width}");
                }
                var scene = (Image)editor.FindName("SceneImage");
                var captureCount = editor.SceneCaptureCount;
                var captured = scene.Source;
                main.Left += 30; main.Top += 20; Settle();
                AssertCoverage($"move-{textScale:0.0}");
                InteractionAssert(captureCount == editor.SceneCaptureCount && ReferenceEquals(captured, scene.Source),
                    "Moving the owner needlessly recaptured the scene.");
                KeyboardCall(main, "ToggleFullScreen"); Settle(); AssertCoverage($"fullscreen-{textScale:0.0}");
                KeyboardCall(main, "ToggleFullScreen"); Settle(); AssertCoverage($"restored-{textScale:0.0}");
                KeyboardCall(editor, "OnCancel", editor, new RoutedEventArgs()); Settle();
                InteractionAssert(!editor.IsVisible, "Closing left a focus stage behind.");
                editor = null;
                Console.WriteLine($"PASS owner coverage at {textScale:P0} text: maximized opening, three native resizes, move without capture, fullscreen and restore, toolbar/footer bounds and close.");
            }
            // Resize before entrance completes: native coverage must update
            // immediately, then one settled snapshot replaces the old scene.
            IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, 1, false);
            KeyboardCall(main, "MaximizeWindow", (Rect?)null); Settle();
            editor = new PreviewToolbarSettingsWindow(new(), catalog, main.ApplyPreviewToolbar, _ => true)
            { Owner = main, FocusAnimationEnabled = true, AnimateInPreview = true };
            editor.ConfigureToolbarFocus(main, toolbar); editor.Show();
            InteractionAssert(editor.IsFocusMoving, "The resize fixture missed the active entrance animation.");
            var beforeResizeCaptureCount = editor.SceneCaptureCount;
            var ownerHwnd = new WindowInteropHelper(main).Handle;
            var ownerDpi = RepairGetDpiForWindow(ownerHwnd) / 96d;
            InteractionAssert(PerfSetWindowPos(ownerHwnd, 0, 0, 0, (int)Math.Round(1100 * ownerDpi),
                (int)Math.Round(780 * ownerDpi), 0x0016), "Could not resize the owner during entrance.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
            AssertCoverage("resized-during-entrance");
            InteractionAssert(editor.SceneCaptureCount == beforeResizeCaptureCount + 1 &&
                Math.Abs(((Image)editor.FindName("SceneImage")).Width - main.ActualWidth) < 1,
                $"A resize during entrance left a stale scene or repeatedly captured it during animation: count={beforeResizeCaptureCount}->{editor.SceneCaptureCount}, scene={((Image)editor.FindName("SceneImage")).Width:F1}, owner={main.ActualWidth:F1}, moving={editor.IsFocusMoving}.");
            KeyboardCall(editor, "OnCancel", editor, new RoutedEventArgs());
            AdvanceDispatcher(TimeSpan.FromMilliseconds(400));
            InteractionAssert(!editor.IsVisible, "Animated closing retained its owner surface.");
            editor = null;
            var area = WindowWorkAreaController.GetAvailableSize(main);
            var ordinary = new Window { Style = new Style(typeof(Window)),
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = main.Left, Top = main.Top,
                Width = area.Width + 200, Height = area.Height + 200, ShowInTaskbar = false, Content = new Grid() };
            try
            {
                ordinary.Show(); Settle();
                var ratio = RepairGetDpiForWindow(new WindowInteropHelper(ordinary).Handle) > 96 ? .8 : 1;
                InteractionAssert(ordinary.ActualWidth <= area.Width * ratio + 1 && ordinary.ActualHeight <= area.Height * ratio + 1,
                    $"The owner surface exemption disabled ordinary window work-area fitting: ordinary={ordinary.ActualWidth:F1}x{ordinary.ActualHeight:F1}, available={area.Width:F1}x{area.Height:F1}, ratio={ratio}.");
            }
            finally { ordinary.Close(); }
            Console.WriteLine("PASS: resize during entrance coalesces its scene refresh; ordinary windows still fit their work area.");
            return 0;
        }
        finally
        {
            if (editor is not null) { SetKeyboardField(editor, "_focusCanClose", true); editor.Close(); }
            CloseWorkspaceTestWindow(main); app.Shutdown();
        }
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int FocusTestGetDwmAttribute(nint window, int attribute, out int value, int size);
}
