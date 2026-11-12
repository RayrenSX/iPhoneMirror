using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.UI.Controls;
using IPhoneMirror.UI.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Inspect the actual rendered viewport, including template-owned viewers in
    // lists, editors and document controls. A thin thumb is still an overlap if
    // its hit area occupies the text/control viewport.
    private static int AuditScrollBarLayout(DependencyObject root, string name, HashSet<string> findings)
    {
        var count = 0;
        foreach (var bar in Visuals(root).OfType<ScrollBar>().Where(b => b.IsVisible && b.ActualWidth > 0 && b.ActualHeight > 0))
        {
            var viewer = Ancestors(bar).OfType<ScrollViewer>().FirstOrDefault();
            if (viewer is null) continue;
            var presenter = Visuals(viewer).OfType<ScrollContentPresenter>()
                .FirstOrDefault(p => p.TemplatedParent == viewer);
            if (presenter is null)
            {
                findings.Add($"{name}: {viewer.Name} has a visible scrollbar without a content viewport.");
                continue;
            }
            count++;
            var bounds = bar.TransformToAncestor(viewer).TransformBounds(new Rect(bar.RenderSize));
            var viewport = presenter.TransformToAncestor(viewer).TransformBounds(new Rect(presenter.RenderSize));
            var overlap = Rect.Intersect(bounds, viewport);
            var label = $"{name}: {viewer.GetType().Name}/{viewer.Name}/{bar.Orientation}";
            if (!overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5)
                findings.Add($"{label} scrollbar overlays content by {overlap.Width:0.0}x{overlap.Height:0.0} DIP.");
            if (bounds.Left < -0.5 || bounds.Top < -0.5 || bounds.Right > viewer.ActualWidth + 0.5 || bounds.Bottom > viewer.ActualHeight + 0.5)
                findings.Add($"{label} scrollbar extends beyond its viewer ({bounds} in {viewer.RenderSize}).");
            if (bar.Orientation == Orientation.Vertical)
            {
                // Settings deliberately place the gutter outside the content's
                // symmetric frame padding; their own audit compares frame insets.
                if (viewer.Name is "ApplicationSettingsScrollViewer" or "ProjectionSettingsScrollViewer" or "ControlSettingsScrollViewer")
                    continue;
                var left = viewport.Left - viewer.Padding.Left;
                var right = viewer.ActualWidth - viewport.Right - viewer.Padding.Right;
                if (Math.Abs(left - right) > 1)
                    findings.Add($"{label} content gutters are asymmetric: left={left:0.0}, right={right:0.0}.");
                if (viewer.ActualWidth - bounds.Right > 3)
                    findings.Add($"{label} leaves an excessive gap outside its scrollbar.");
            }
        }
        return count;
    }

    private static int RunScrollBarLayoutAudit(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var findings = new HashSet<string>();
        var results = new List<object>();
        var cases = new Dictionary<string, Func<FrameworkElement>>
        {
            ["vertical-page"] = () => new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = LongScrollContent() },
            ["both-directions"] = () => new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = LongScrollContent(wide: true) },
            ["horizontal-toolbar"] = () => new ScrollViewer { Style = (Style)app.FindResource("HorizontalContentScrollViewer"),
                Content = new Button { Content = "Last toolbar action", Width = 1000 } },
            ["dialog-viewport"] = () => new DialogViewport { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = LongScrollContent() },
            ["fitting-page"] = () =>
            {
                var viewer = new ScrollViewer { Style = (Style)app.FindResource("PageScrollViewerStyle"),
                    Padding = new Thickness(24, 20, 24, 20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                var page = new Grid();
                page.SetBinding(FrameworkElement.MinHeightProperty, new System.Windows.Data.Binding("ViewportHeight") { Source = viewer });
                page.Children.Add(new TextBlock { Text = "Device page", VerticalAlignment = VerticalAlignment.Top });
                viewer.Content = page;
                return viewer;
            },
            ["virtualized-list"] = () => new ListBox { ItemsSource = Enumerable.Range(1, 80).Select(i => $"{i}: {LocalizationService.Get("Close")} / 日本語 / 繁體中文"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch },
            ["multiline-editor"] = () => new TextBox { Style = (Style)app.FindResource("RoundedTextBox"), AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = string.Join('\n', Enumerable.Repeat(new string('W', 100), 50)) },
            ["release-document"] = () => new FlowDocumentScrollViewer { IsToolBarVisible = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Document = new FlowDocument(new Paragraph(new Run(string.Join(' ', Enumerable.Repeat("Release notes 日本語 繁體中文", 250))))) },
            ["combo-dropdown"] = () => new ComboBox { ItemsSource = Enumerable.Range(1, 60).Select(i => $"{i}: {LocalizationService.Get("Close")} / 日本語"),
                SelectedIndex = 0, VerticalAlignment = VerticalAlignment.Top, MaxDropDownHeight = 180 },
        };
        try
        {
            foreach (var culture in SupportedUiCultures.All)
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            foreach (var scale in new[] { 1d, 2d })
            foreach (var width in new[] { 360d, 600d })
            {
                SetAuditCulture(culture); ThemeService.Apply(theme);
                AccessibilityAppearance.Apply(app, scale, false);
                foreach (var (surface, create) in cases)
                {
                    var id = $"{culture}-{theme}-{scale}-{width}-{surface}";
                    var window = new Window { Style = null, Width = width, Height = 320, ShowInTaskbar = false,
                        Content = create(), Background = (Brush)app.FindResource("CardBrush") };
                    try
                    {
                        window.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(20)); window.UpdateLayout();
                        DependencyObject root = window;
                        Popup? popup = null;
                        if (window.Content is ComboBox combo)
                        {
                            combo.IsDropDownOpen = true; AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                            popup = (Popup)(combo.Template.FindName("PART_Popup", combo) ?? combo.Template.FindName("Popup", combo));
                            root = popup.Child;
                        }
                        var checkedBars = AuditScrollBarLayout(root, id, findings);
                        if (surface == "fitting-page")
                        {
                            if (checkedBars != 0) findings.Add(id + ": fitting page has an unnecessary scrollbar.");
                        }
                        else if (checkedBars == 0) findings.Add(id + ": overflowing content produced no visible scrollbar.");
                        foreach (var bar in Visuals(root).OfType<ScrollBar>().Where(b => b.IsVisible).ToArray())
                        {
                            var viewer = Ancestors(bar).OfType<ScrollViewer>().First();
                            var vertical = bar.Orientation == Orientation.Vertical;
                            var before = vertical ? viewer.VerticalOffset : viewer.HorizontalOffset;
                            (vertical ? ScrollBar.PageDownCommand : ScrollBar.PageRightCommand).Execute(null, bar);
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(10)); window.UpdateLayout();
                            var after = vertical ? viewer.VerticalOffset : viewer.HorizontalOffset;
                            if (after <= before) findings.Add(id + ": scrollbar page command did not move content: " + bar.Orientation);
                            viewer.ScrollToHome(); viewer.ScrollToLeftEnd();
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(10)); window.UpdateLayout();
                            var thumb = (Thumb)bar.Template.FindName("ScrollThumb", bar);
                            thumb.RaiseEvent(new DragDeltaEventArgs(vertical ? 0 : 30, vertical ? 30 : 0) { RoutedEvent = Thumb.DragDeltaEvent });
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(10)); window.UpdateLayout();
                            if ((vertical ? viewer.VerticalOffset : viewer.HorizontalOffset) <= 0)
                                findings.Add(id + ": thumb drag did not move content: " + bar.Orientation);
                        }
                        foreach (var viewer in Visuals(root).OfType<ScrollViewer>().ToArray())
                        {
                            viewer.ScrollToEnd(); viewer.ScrollToRightEnd();
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(10)); window.UpdateLayout();
                            AuditScrollBarLayout(root, id + "/end", findings);
                            if (viewer.ScrollableHeight > 1 && viewer.VerticalOffset < viewer.ScrollableHeight - 1)
                                findings.Add(id + ": vertical content end cannot be reached.");
                            if (viewer.ScrollableWidth > 1 && viewer.HorizontalOffset < viewer.ScrollableWidth - 1)
                                findings.Add(id + ": horizontal content end cannot be reached.");
                        }
                        results.Add(new { culture, theme = theme.ToString(), textScale = scale, width, surface, checkedBars });
                        if (culture == "ja-JP" && scale == 2 && width == 360 && popup is null)
                            SaveWindowRender(window, Path.Combine(output, id + ".png"));
                        if (popup is not null) ((ComboBox)window.Content).IsDropDownOpen = false;
                    }
                    finally { window.Close(); }
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                expectedCombinations = SupportedUiCultures.All.Count * 2 * 2 * 2 * cases.Count,
                actualCombinations = results.Count, results, findings,
            }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
        }
        foreach (var finding in findings) Console.Error.WriteLine(finding);
        Console.WriteLine($"Scrollbar layout: {results.Count} combinations, {findings.Count} findings.");
        return findings.Count == 0 ? 0 : 1;
    }

    private static FrameworkElement LongScrollContent(bool wide = false)
    {
        var stack = new StackPanel { Width = wide ? 1000 : double.NaN };
        for (var i = 0; i < 30; i++)
            stack.Children.Add(new Button { Content = new TextBlock { Text = $"{i + 1}: {LocalizationService.Get("Close")} — 日本語 / 繁體中文",
                TextWrapping = TextWrapping.Wrap }, HorizontalContentAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 4) });
        return stack;
    }
}
