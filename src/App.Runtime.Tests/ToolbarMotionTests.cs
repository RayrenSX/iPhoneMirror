using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunToolbarMotionTests(string output, bool softwareOnly = true)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = softwareOnly ? System.Windows.Interop.RenderMode.SoftwareOnly : System.Windows.Interop.RenderMode.Default;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var toolbar = (FrameworkElement)main.FindName("EnvironmentPanel");
        var original = ((Panel)main.FindName("PreviewQuickActions")).Children.OfType<Button>().ToArray();
        var catalog = original.Select(b => new ToolbarAction(AutomationProperties.GetAutomationId(b), AutomationProperties.GetName(b),
            ((StackPanel)b.Content).Children.OfType<IPhoneMirror.App.Controls.ThemedSymbolIcon>().First().Symbol)).ToArray();
        var metrics = new List<object>();
        try
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                var start = Stopwatch.GetTimestamp();
                var editor = new PreviewToolbarSettingsWindow(new(), catalog, main.ApplyPreviewToolbar, _ => true)
                { Owner = main, AnimateInPreview = true, FocusAnimationEnabled = true };
                editor.ConfigureToolbarFocus(main, toolbar);
                var prepareMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var scene = (Image)editor.FindName("SceneImage");
                var background = scene.Source;
                var frames = new List<double>();
                TimeSpan? lastRenderTime = null;
                void OnFrame(object? sender, EventArgs args)
                {
                    var time = ((RenderingEventArgs)args).RenderingTime;
                    if (lastRenderTime is { } last && time > last) frames.Add((time - last).TotalMilliseconds);
                    lastRenderTime = time;
                }
                try
                {
                    editor.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(45));
                    var shade = (System.Windows.Shapes.Path)editor.FindName("SurroundShade");
                    var brush = (Freezable)shade.Fill; var geometry = shade.Data;
                    var placementCount = editor.PlacementPassCount;
                    var scale = ((TransformGroup)scene.RenderTransform).Children.OfType<ScaleTransform>().Single();
                    InteractionAssert(!editor.AllowsTransparency && brush.IsFrozen && geometry.IsFrozen && scale.HasAnimatedProperties,
                        "Focus still uses an alpha HWND or per-frame mutable shade/managed transforms.");
                    InteractionAssert(shade.Fill is ImageBrush { ImageSource: System.Windows.Media.Imaging.BitmapSource texture } &&
                        Math.Max(texture.PixelWidth, texture.PixelHeight) <= 768,
                        "The soft shade still rasterizes a full-size gradient each frame.");
                    CompositionTarget.Rendering += OnFrame;
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(175));
                    InteractionAssert(editor.IsFocusMoving && editor.FocusProgress is > 0 and < 1 &&
                        editor.PlacementPassCount == placementCount && editor.SceneCaptureCount == 1 &&
                        ReferenceEquals(background, scene.Source) && ReferenceEquals(brush, shade.Fill) && ReferenceEquals(geometry, shade.Data),
                        "The active zoom captured/rebuilt/relaid out its scene or shade.");
                    var surface = (FrameworkElement)editor.FindName("ToolbarSurface");
                    var footer = (FrameworkElement)editor.FindName("SettingsPanel");
                    InteractionAssert(surface.Opacity > 0 && footer.Opacity > 0,
                        "Toolbar/footer still wait for zoom completion instead of joining its transition.");
                    var snapshot = (FrameworkElement)editor.FindName("FocusToolbarImage");
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(editor).TransformBounds(new Rect(element.RenderSize));
                    var a = Bounds(surface); var b = Bounds(snapshot);
                    InteractionAssert(Math.Abs(a.Left - b.Left) < 2 && Math.Abs(a.Top - b.Top) < 2 && Math.Abs(a.Width - b.Width) < 3,
                        "Live toolbar and its snapshot diverge during their handoff.");
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                    var completionWait = Stopwatch.StartNew();
                    while (editor.IsFocusMoving && completionWait.ElapsedMilliseconds < 500)
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(15));
                    CompositionTarget.Rendering -= OnFrame;
                    var allocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    InteractionAssert(!editor.IsFocusMoving && !scale.HasAnimatedProperties && footer.Opacity == 1,
                        "Completed entrance retained animation clocks or delayed footer appearance.");
                    var endMatrix = scene.RenderTransform.Value;
                    var beginExit = Stopwatch.GetTimestamp();
                    KeyboardCall(editor, "OnCancel", editor, new RoutedEventArgs());
                    var exitStartMs = Stopwatch.GetElapsedTime(beginExit).TotalMilliseconds;
                    Console.WriteLine($"Cycle {cycle}: captures={editor.SceneCaptureCount}, same={ReferenceEquals(scene.Source, background)}, matrix={endMatrix.M11}->{scene.RenderTransform.Value.M11}, exit={exitStartMs:F2}ms, completion wait={completionWait.Elapsed.TotalMilliseconds:F2}ms");
                    InteractionAssert(editor.SceneCaptureCount == 1 && ReferenceEquals(scene.Source, background) &&
                        Math.Abs(scene.RenderTransform.Value.M11 - endMatrix.M11) < .01,
                        "Exit recaptured or jumped the full background instead of reversing its current state.");
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(340));
                    var exitWait = Stopwatch.StartNew();
                    while (editor.IsVisible && exitWait.ElapsedMilliseconds < 500)
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(15));
                    InteractionAssert(!editor.IsVisible && !scale.HasAnimatedProperties && scene.Source is null &&
                        Math.Abs(scene.RenderTransform.Value.M11 - 1) < .001,
                        "Exit failed to restore identity/close/release clocks and snapshots.");
                    var ordered = frames.Order().ToArray();
                    metrics.Add(new { cycle, prepareMs, exitStartMs, fullSceneCaptures = editor.SceneCaptureCount,
                        toolbarCaptures = editor.ToolbarCaptureCount, motionPlacementPasses = 0, allocationBytes,
                        renderFrameSamples = frames.Count,
                        renderIntervalMedianMs = ordered.Length == 0 ? 0 : ordered[ordered.Length / 2],
                        renderIntervalP95Ms = ordered.Length == 0 ? 0 : ordered[Math.Min(ordered.Length - 1, (int)(ordered.Length * .95))] });
                }
                finally
                {
                    CompositionTarget.Rendering -= OnFrame;
                    SetKeyboardField(editor, "_focusCanClose", true); editor.Close();
                }
            }
            // Reverse a partially completed entrance immediately and preserve
            // every animated value, without waiting for its forward completion.
            var early = new PreviewToolbarSettingsWindow(new(), catalog, main.ApplyPreviewToolbar, _ => true)
            { Owner = main, AnimateInPreview = true, FocusAnimationEnabled = true };
            early.ConfigureToolbarFocus(main, toolbar); early.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(85));
            try
            {
                var scene = (Image)early.FindName("SceneImage"); var before = scene.RenderTransform.Value;
                early.Close();
                var after = scene.RenderTransform.Value;
                InteractionAssert(early.IsFocusMoving && Math.Abs(before.M11 - after.M11) < .01 &&
                    early.SceneCaptureCount == 1 && early.ToolbarCaptureCount == 1,
                    "Early cancellation restarted or recaptured the transition.");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(340));
                InteractionAssert(!early.IsVisible, "Early reverse waited for the original entrance to complete.");
            }
            finally { SetKeyboardField(early, "_focusCanClose", true); early.Close(); }

            var review = new PreviewToolbarSettingsWindow(new(), catalog, main.ApplyPreviewToolbar, _ => true)
            { Owner = main, AnimateInPreview = true, FocusAnimationEnabled = true };
            review.ConfigureToolbarFocus(main, toolbar); review.Show();
            try
            {
                foreach (var (delay, name) in new[] { (90, "enter-early"), (110, "enter-handoff"), (240, "editing") })
                { AdvanceDispatcher(TimeSpan.FromMilliseconds(delay)); SaveWindowRender(review, Path.Combine(output, name + ".png")); }
                var list = (ListBox)review.FindName("PreviewList");
                var instances = list.Items.Cast<object>().Select(row => (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(row)).ToArray();
                var source = list.ItemsSource; var placements = review.PlacementPassCount;
                var id = review.Draft.Items[0]; var rowStart = instances[0].TranslatePoint(new Point(instances[0].ActualWidth / 2, 8), list);
                review.BeginToolbarDrag(id, rowStart);
                review.UpdateToolbarDrag(new Point(list.ActualWidth + 20, rowStart.Y));
                var shifts = instances.Select(item => (TranslateTransform)item.RenderTransform).ToArray();
                InteractionAssert(shifts.Any(shift => shift.HasAnimatedProperties) && shifts.All(shift => Math.Abs(shift.X) < .01) &&
                    ReferenceEquals(source, list.ItemsSource) && review.PlacementPassCount == placements &&
                    instances.SequenceEqual(list.Items.Cast<object>().Select(row => list.ItemContainerGenerator.ContainerFromItem(row))),
                    "Drag neighbors jumped before their first frame, rebuilt the row or forced layout.");
                review.EndToolbarDrag(cancel: true);
                InteractionAssert(shifts.All(shift => !shift.HasAnimatedProperties && shift.X == 0),
                    "Finishing a drag retained neighbor animation clocks.");
                KeyboardCall(review, "OnCancel", review, new RoutedEventArgs());
                AdvanceDispatcher(TimeSpan.FromMilliseconds(85)); SaveWindowRender(review, Path.Combine(output, "leaving.png"));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(300));
            }
            finally { SetKeyboardField(review, "_focusCanClose", true); review.Close(); }
            File.WriteAllText(Path.Combine(output, "motion-metrics.json"), JsonSerializer.Serialize(new
            { rendering = RenderOptions.ProcessRenderMode.ToString(), reportedRenderTier = RenderCapability.Tier >> 16,
                note = "Composition callback intervals are diagnostics, not physical GPU/display FPS.", metrics }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS toolbar motion: native opaque window, frozen shade texture, direct animation clocks, zero active-frame placement/full recapture, aligned concurrent handoff, cached exit, immediate continuous reversal, identity/clock cleanup; render diagnostics saved.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(main); app.Shutdown(); }
    }
}
