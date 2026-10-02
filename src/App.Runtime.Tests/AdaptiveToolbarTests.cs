using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunAdaptiveToolbarTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
        app.InitializeComponent();
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var toolbar = (AdaptiveToolbar)owner.FindName("PreviewToolbarScrollViewer");
        var actions = (StackPanel)owner.FindName("PreviewQuickActions");
        var buttons = actions.Children.OfType<Button>().ToArray();
        var labels = buttons.Select(b => b.Content).OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<TextBlock>()).ToArray();
        var commands = buttons.Select(b => b.Command).ToArray();
        var commandBindings = buttons.Select(b => BindingOperations.GetBinding(b, Button.CommandProperty)).ToArray();
        var styles = buttons.Select(b => b.Style).ToArray();
        var enabledBindings = buttons.Select(b => BindingOperations.GetBinding(b, UIElement.IsEnabledProperty)).ToArray();
        var tooltipBindings = buttons.Select(b => BindingOperations.GetBinding(b, FrameworkElement.ToolTipProperty)).ToArray();
        Window? host = null;
        using var trace = new AuditTraceListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        var results = new List<string>();
        try
        {
            ((FrameworkElement)owner.FindName("EnvironmentPanel")).Visibility = Visibility.Visible;
            // Keep this visual fixture visible when reparenting below causes
            // its inherited DataContext (and production visibility binding) to refresh.
            actions.Visibility = Visibility.Visible;
            // Exercise the real containing grid and window before isolating the
            // same controls for exact width boundaries and simulated monitor DPI.
            owner.MinWidth = 0;
            foreach (var windowWidth in new[] { 1500.0, 1280.0, 1000.0, 850.0, 1500.0 })
            {
                owner.Width = windowWidth;
                owner.UpdateLayout(); DrainDispatcher(); owner.UpdateLayout();
                CheckToolbarGeometry(toolbar, buttons, labels);
            }
            var parent = (Panel)toolbar.Parent;
            parent.Children.Remove(toolbar);
            // Keep the render origin at (0, 0) for tightly cropped screenshots.
            toolbar.HorizontalAlignment = HorizontalAlignment.Left;
            toolbar.VerticalAlignment = VerticalAlignment.Top;
            host = new Window
            {
                Style = new Style(typeof(Window)), Resources = owner.Resources, DataContext = owner.DataContext,
                Width = 1600, Height = 160, ShowInTaskbar = false,
                Content = toolbar, Background = (Brush)app.FindResource("AppBackgroundBrush"),
            };
            owner.Hide();
            host.Show(); DrainDispatcher();
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-HK" })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            foreach (var dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
            {
                DisplayLanguage(culture);
                ApplyTheme(typeof(App).Assembly, theme);
                VisualTreeHelper.SetRootDpi(host, new DpiScale(dpi, dpi));
                // SetRootDpi only changes visual DPI flags. Mirror HwndTarget's
                // real monitor-change path by invalidating descendant measurements too.
                foreach (var element in Visuals(host).OfType<UIElement>()) element.InvalidateMeasure();
                host.UseLayoutRounding = true;
                void Resize(double width)
                {
                    toolbar.Width = width;
                    host.UpdateLayout(); DrainDispatcher(); host.UpdateLayout();
                    CheckToolbarGeometry(toolbar, buttons, labels);
                }
                Resize(2000);
                var fullWidth = actions.DesiredSize.Width;
                ToolbarAssert(labels.All(l => l.Visibility == Visibility.Visible), "Wide toolbar must show every label.");
                ToolbarAssert(Math.Abs(VisualTreeHelper.GetDpi(labels[0]).DpiScaleX - dpi) < 0.001,
                    "DPI must propagate to the actual text elements.");
                Resize(fullWidth);
                ToolbarAssert(labels.All(l => l.Visibility == Visibility.Visible),
                    $"{culture}/{theme}/{dpi}: all labels must fit at their exact measured width {fullWidth:R}.");
                var boundaries = new List<double> { fullWidth };
                var exactWidths = new List<double> { fullWidth };
                if (dpi == 1) SaveToolbarRender(toolbar, Path.Combine(output, $"{culture}-{theme}-wide.png"));
                for (var hidden = 1; hidden <= labels.Length; hidden++)
                {
                    Resize(actions.DesiredSize.Width - 2);
                    boundaries.Add(toolbar.Width);
                    exactWidths.Add(actions.DesiredSize.Width);
                    ToolbarAssert(labels.Take(labels.Length - hidden).All(l => l.Visibility == Visibility.Visible) &&
                        labels.Skip(labels.Length - hidden).All(l => l.Visibility == Visibility.Collapsed),
                        $"{culture}/{theme}/{dpi}: expected exactly the rightmost {hidden} labels to collapse.");
                    ToolbarAssert(toolbar.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed,
                        "Labels must collapse before a scrollbar is needed.");
                    if (dpi == 1 && hidden is 1 or 3 or 6)
                        SaveToolbarRender(toolbar, Path.Combine(output, $"{culture}-{theme}-hidden-{hidden}.png"));
                }
                var iconWidth = actions.DesiredSize.Width;
                // Check every exact boundary, including restoration from one
                // physical pixel less space. A tolerance must not hide real overflow.
                for (var hidden = labels.Length; hidden >= 0; --hidden)
                {
                    Resize(exactWidths[hidden] - 1 / dpi);
                    ToolbarAssert(labels.Count(l => l.Visibility == Visibility.Collapsed) == Math.Min(hidden + 1, labels.Length) &&
                        (toolbar.ComputedHorizontalScrollBarVisibility == Visibility.Visible) == (hidden == labels.Length),
                        $"{culture}/{theme}/{dpi}: one pixel below boundary {hidden} must compact or scroll.");
                    Resize(exactWidths[hidden]);
                    for (var repeat = 0; repeat < 3; ++repeat)
                    {
                        toolbar.InvalidateMeasure();
                        host.UpdateLayout(); DrainDispatcher(); host.UpdateLayout();
                        CheckToolbarGeometry(toolbar, buttons, labels);
                        ToolbarAssert(labels.Count(l => l.Visibility == Visibility.Collapsed) == hidden &&
                            toolbar.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed,
                            $"{culture}/{theme}/{dpi}: exact width {exactWidths[hidden]:R} must stably retain {hidden} collapsed labels.");
                    }
                }
                Resize(iconWidth - 30);
                ToolbarAssert(toolbar.ComputedHorizontalScrollBarVisibility == Visibility.Visible,
                    "If all icons cannot fit, scrolling must remain available.");
                toolbar.ScrollToRightEnd(); host.UpdateLayout(); DrainDispatcher(); host.UpdateLayout();
                var last = buttons[^1].TransformToAncestor(toolbar).TransformBounds(new Rect(buttons[^1].RenderSize));
                ToolbarAssert(last.Left >= 0 && last.Right <= toolbar.ViewportWidth + 1, "The final icon must remain reachable.");
                toolbar.ScrollToLeftEnd();
                for (var index = boundaries.Count - 1; index >= 0; --index)
                {
                    Resize(boundaries[index]);
                    ToolbarAssert(labels.Count(l => l.Visibility == Visibility.Collapsed) == index,
                        $"{culture}/{theme}/{dpi}: growing to {toolbar.Width} must restore {index} collapsed labels, got {labels.Count(l => l.Visibility == Visibility.Collapsed)}; extent={toolbar.ExtentWidth}, desired={actions.DesiredSize.Width}, buttons={string.Join(",", buttons.Select(b => b.DesiredSize.Width))}.");
                }
                // Fractional boundaries and a rapid shrink/grow sweep model live
                // dragging. Repeated layout at one size must not oscillate.
                foreach (var width in boundaries.SelectMany(w => new[] { w - 0.25, w, w + 0.25 }).Concat(
                             Enumerable.Range(0, 60).Select(i => iconWidth + (fullWidth - iconWidth) * Math.Abs(30 - i) / 30)))
                {
                    Resize(width);
                    var state = string.Join(",", labels.Select(l => l.Visibility));
                    for (var repeat = 0; repeat < 3; repeat++)
                    {
                        host.UpdateLayout();
                        ToolbarAssert(state == string.Join(",", labels.Select(l => l.Visibility)) && toolbar.IsMeasureValid && toolbar.IsArrangeValid,
                            "A settled layout must remain stable without further refreshes.");
                    }
                }
                // A resource change on a hidden label must restore it without a resize.
                Resize(boundaries[1]);
                host.Resources["SeparateWindow"] = "";
                host.UpdateLayout(); DrainDispatcher(); host.UpdateLayout();
                ToolbarAssert(labels[^1].Visibility == Visibility.Visible, "Hidden label resource updates must trigger measurement.");
                host.Resources.Remove("SeparateWindow");
                host.UpdateLayout(); DrainDispatcher(); host.UpdateLayout();
                ToolbarAssert(labels[^1].Visibility == Visibility.Collapsed, "Restoring a longer resource must compact again.");
                for (var i = 0; i < buttons.Length; i++)
                {
                    ToolbarAssert(ReferenceEquals(commands[i], buttons[i].Command) && ReferenceEquals(styles[i], buttons[i].Style) &&
                        ReferenceEquals(commandBindings[i], BindingOperations.GetBinding(buttons[i], Button.CommandProperty)) &&
                        ReferenceEquals(enabledBindings[i], BindingOperations.GetBinding(buttons[i], UIElement.IsEnabledProperty)) &&
                        ReferenceEquals(tooltipBindings[i], BindingOperations.GetBinding(buttons[i], FrameworkElement.ToolTipProperty)),
                        "Compaction must preserve commands, styles and interaction bindings.");
                    var peer = new ButtonAutomationPeer(buttons[i]);
                    ToolbarAssert(!string.IsNullOrWhiteSpace(peer.GetName()) && buttons[i].ToolTip is not null,
                        "Every button must retain its tooltip and accessible name.");
                    foreach (var icon in Visuals(buttons[i]).OfType<Wpf.Ui.Controls.SymbolIcon>())
                        ToolbarAssert(icon.IsVisible && SameThemeColor(icon.Foreground, buttons[i].Foreground),
                            "Icons must remain visible and follow the button theme.");
                }
                Resize(fullWidth);
                results.Add($"PASS {culture}/{theme}/{dpi:P0}: full labels, 6 right-to-left steps, exact-width/one-pixel boundaries, forced remeasure, restore, fractional/live resize, resource change, scrolling, bindings and accessibility.");
            }
            ToolbarAssert(trace.Messages.Count == 0, string.Join(Environment.NewLine, trace.Messages));
            File.WriteAllLines(Path.Combine(output, "results.txt"), results);
            foreach (var result in results) Console.WriteLine(result);
            Console.WriteLine($"Adaptive toolbar: {results.Count} language/theme/DPI combinations passed.");
            return 0;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            host?.Close();
            CloseWorkspaceTestWindow(owner);
            app.Shutdown();
        }
    }

    private static void CheckToolbarGeometry(AdaptiveToolbar toolbar, Button[] buttons, TextBlock[] labels)
    {
        ToolbarAssert(toolbar.IsVisible && buttons.All(b => b.IsVisible), "The toolbar fixture must be visible.");
        var hidden = false;
        foreach (var label in labels)
        {
            if (label.Visibility == Visibility.Collapsed) hidden = true;
            else ToolbarAssert(!hidden, "Collapsed labels must form a suffix from the right.");
        }
        var previousRight = double.NegativeInfinity;
        foreach (var button in buttons)
        {
            var bounds = button.TransformToAncestor(toolbar).TransformBounds(new Rect(button.RenderSize));
            ToolbarAssert(bounds.Left >= previousRight - 0.01, "Toolbar buttons must not overlap.");
            previousRight = bounds.Right;
            if (toolbar.ComputedHorizontalScrollBarVisibility != Visibility.Visible)
                ToolbarAssert(bounds.Left >= -1 && bounds.Right <= toolbar.ViewportWidth + 1,
                    $"A toolbar button is clipped: {AutomationProperties.GetAutomationId(button)}, bounds={bounds}, viewport={toolbar.ViewportWidth}, extent={toolbar.ExtentWidth}, width={toolbar.Width}, dpi={VisualTreeHelper.GetDpi(toolbar).DpiScaleX}.");
            foreach (var label in labels.Where(l => l.IsVisible && button.IsAncestorOf(l)))
            {
                var content = label.TransformToAncestor(button).TransformBounds(new Rect(label.RenderSize));
                ToolbarAssert(content.Left >= 0 && content.Right <= button.ActualWidth + 0.01 &&
                    label.ActualWidth + 1 >= label.DesiredSize.Width - label.Margin.Left - label.Margin.Right &&
                    LayoutInformation.GetLayoutClip(label) is null,
                    "Visible toolbar text must not be clipped.");
            }
        }
    }

    private static void SaveToolbarRender(AdaptiveToolbar toolbar, string path)
    {
        var bounds = new Rect(toolbar.RenderSize);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle((Brush)Application.Current.FindResource("AppBackgroundBrush"), null, bounds);
            // Composite the toolbar over the active theme's opaque background.
            context.DrawRectangle(new VisualBrush(toolbar)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds,
            }, null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(toolbar.ActualWidth),
            (int)Math.Ceiling(toolbar.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void ToolbarAssert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
