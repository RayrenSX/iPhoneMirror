using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;
using Image = System.Windows.Controls.Image;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunHiddenToolbarMotionTests(string output, bool softwareOnly = true)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = softwareOnly ? System.Windows.Interop.RenderMode.SoftwareOnly : System.Windows.Interop.RenderMode.Default;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var source = (FrameworkElement)main.FindName("EnvironmentPanel");
        var original = ((Panel)main.FindName("PreviewQuickActions")).Children.OfType<Button>().ToArray();
        var catalog = original.Select(b => new ToolbarAction(AutomationProperties.GetAutomationId(b), AutomationProperties.GetName(b),
            ((StackPanel)b.Content).Children.OfType<IPhoneMirror.App.Controls.ThemedSymbolIcon>().First().Symbol)).ToArray();
        PreviewToolbarSettingsWindow? editor = null;
        void WaitForMotion()
        {
            var watch = Stopwatch.StartNew();
            while (editor!.IsFocusMoving && watch.ElapsedMilliseconds < 1200)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            InteractionAssert(!editor.IsFocusMoving, "The toolbar slide animation did not complete.");
        }
        try
        {
            foreach (var scenario in new[] { "lightweight", "manually-hidden" })
            {
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete;
                vm.HidePreviewToolbar = scenario == "manually-hidden";
                if (scenario == "lightweight") vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight;
                DrainDispatcher(); main.UpdateLayout();
                InteractionAssert(!source.IsVisible, "The hidden toolbar fixture still has an on-screen origin.");
                foreach (var ending in new[] { "cancel", "done", "early-close", "no-motion" })
                {
                    var saved = false;
                    editor = new PreviewToolbarSettingsWindow(new(), catalog, main.ApplyPreviewToolbar, _ => saved = true)
                    { Owner = main, AnimateInPreview = true, FocusAnimationEnabled = ending != "no-motion" };
                    editor.ConfigureToolbarFocus(main, source);
                    var scene = (Image)editor.FindName("SceneImage"); var background = scene.Source;
                    var surface = (FrameworkElement)editor.FindName("ToolbarSurface");
                    var content = (FrameworkElement)editor.FindName("EditorContent");
                    var shift = (TranslateTransform)editor.FindName("LiveToolbarShift");
                    var scale = (ScaleTransform)editor.FindName("LiveToolbarScale");
                    var sceneScale = (ScaleTransform)editor.FindName("SceneScale");
                    var snapshot = (Image)editor.FindName("FocusToolbarImage");
                    editor.Show();
                    InteractionAssert(scene.RenderTransform.Value.IsIdentity && !sceneScale.HasAnimatedProperties &&
                        editor.SceneTargetMatrix.IsIdentity && snapshot.Source is null && editor.ToolbarCaptureCount == 0 &&
                        scale.ScaleX == 1 && scale.ScaleY == 1 && shift.X == 0,
                        "A hidden toolbar still zooms from a fabricated origin or captures a nonexistent toolbar.");
                    if (ending != "no-motion")
                    {
                        var placements = editor.PlacementPassCount;
                        InteractionAssert(editor.IsFocusMoving && !content.IsHitTestVisible && shift.Y <= -13 && surface.Opacity < .05,
                            "The toolbar did not begin above its destination with editing gated.");
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(35));
                        var entranceTick = Stopwatch.StartNew();
                        while (editor.IsFocusMoving && editor.FocusProgress <= 0 && entranceTick.ElapsedMilliseconds < 500)
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
                        InteractionAssert(shift.Y is > -14 and < 0 && surface.Opacity is > 0 and < 1 &&
                            editor.PlacementPassCount == placements && editor.SceneCaptureCount == 1,
                            "The slide/fade skipped its intermediate frames or rebuilt layout/snapshots.");
                        if (ending == "early-close")
                        {
                            var y = shift.Y; var opacity = surface.Opacity;
                            KeyboardCall(editor, "OnCancel", editor, new RoutedEventArgs());
                            InteractionAssert(Math.Abs(shift.Y - y) < .1 && Math.Abs(surface.Opacity - opacity) < .01,
                                "Early cancellation jumped before reversing the current slide.");
                        }
                        else
                        {
                            SaveWindowRender(editor, Path.Combine(output, scenario + "-" + ending + "-entering.png"));
                            WaitForMotion();
                        }
                    }
                    if (ending != "early-close")
                    {
                        InteractionAssert(content.IsHitTestVisible && Math.Abs(shift.Y) < .001 && surface.Opacity == 1 &&
                            scene.RenderTransform.Value.IsIdentity && snapshot.Source is null && snapshot.Opacity == 0,
                            "Editing retained a toolbar offset, zoom or phantom return image.");
                        var hidden = editor.Draft.Items[0]; editor.SetItemVisible(hidden, false);
                        if (ending == "done") KeyboardCall(editor, "OnDone", editor, new RoutedEventArgs());
                        else KeyboardCall(editor, "OnCancel", editor, new RoutedEventArgs());
                        if (ending != "no-motion")
                        {
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                            // A newly installed WPF clock may not tick until
                            // the next composition turn on a busy dispatcher.
                            var exitTick = Stopwatch.StartNew();
                            while (editor.IsFocusMoving && editor.FocusProgress >= 1 && exitTick.ElapsedMilliseconds < 500)
                                AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
                            InteractionAssert(shift.Y is < 0 and > -14 && surface.Opacity is > 0 and < 1,
                                $"Exit did not slide up and fade from the editing position: y={shift.Y:F4}, opacity={surface.Opacity:F4}, progress={editor.FocusProgress:F4}, moving={editor.IsFocusMoving}, visible={editor.IsVisible}.");
                            SaveWindowRender(editor, Path.Combine(output, scenario + "-" + ending + "-leaving.png"));
                        }
                    }
                    WaitForMotion(); DrainDispatcher();
                    InteractionAssert(!editor.IsVisible && !shift.HasAnimatedProperties && !surface.HasAnimatedProperties &&
                        scene.Source is null && scene.RenderTransform.Value.IsIdentity &&
                        saved == (ending == "done") && editor.ToolbarCaptureCount == 0 && editor.SceneCaptureCount == 1,
                        "Exit left clocks, snapshots, a false save result or an extra scene capture.");
                    InteractionAssert(!source.IsVisible, "Editing made the hidden source toolbar visible.");
                    main.ApplyPreviewToolbar(new()); editor = null;
                    Console.WriteLine($"PASS {scenario}/{ending}: top slide/fade, stationary background, no fake source, input gates, continuous reverse, save/cancel and clock/snapshot cleanup.");
                }
            }
            return 0;
        }
        finally
        {
            if (editor is not null) { SetKeyboardField(editor, "_focusCanClose", true); editor.Close(); }
            CloseWorkspaceTestWindow(main); app.Shutdown();
        }
    }
}
