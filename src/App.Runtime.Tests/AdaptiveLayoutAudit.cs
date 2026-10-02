using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static bool QuickLayoutAudit => Environment.GetEnvironmentVariable("IPHONE_MIRROR_UI_AUDIT_QUICK") == "1";

    private static void AuditAdaptiveLayout(Window window, string name, HashSet<string> findings)
    {
        foreach (var element in Visuals(window).OfType<FrameworkElement>().Where(e => e.IsVisible).ToArray())
        {
            if (element is not TextBlock && element is not Button && element is not TextBox && element is not ComboBox && element is not ScrollViewer) continue;
            if (Ancestors(element).OfType<UIElement>().Any(e => e.Opacity < 0.01)) continue;
            // Collapsed Expander content can remain in the visual tree during its
            // closing animation. Audit the header here and the content in the
            // explicit expanded-state pass below, not its intentional clipping.
            var ancestors = Ancestors(element).ToArray();
            if (ancestors.OfType<Expander>().Any(expander => !expander.IsExpanded &&
                expander.Content is DependencyObject content && ancestors.Contains(content))) continue;
            // WPF-UI keeps the text presenter at zero width in its icon-only pane.
            // Those labels are intentionally hidden and are exposed by tooltips / the expanded pane.
            if (element is TextBlock && Ancestors(element).OfType<Wpf.Ui.Controls.NavigationViewItem>().FirstOrDefault() is { ToolTip: not null } &&
                Ancestors(element).OfType<Wpf.Ui.Controls.NavigationView>().FirstOrDefault() is { IsPaneOpen: false }) continue;
            var text = element is TextBlock block ? RenderedText(block) : element is ContentControl control ? control.Content?.ToString() : element.Name;
            if (element is TextBlock && string.IsNullOrWhiteSpace(text)) continue;
            // Icon fonts are deliberately sized to icon boxes and have different ink metrics.
            if (element is TextBlock icon && (icon.FontFamily.Source.Contains("Fluent") || icon.FontFamily.Source.Contains("MDL2"))) continue;
            var label = $"{element.GetType().Name} {element.Name} '{text?.Replace("\n", " ")[..Math.Min(text?.Length ?? 0, 80)]}'";
            var trims = element is TextBlock { TextTrimming: not TextTrimming.None };
            if (element is TextBlock source && source.ActualWidth > 0 && source.ActualHeight > 0)
            {
                // A separate WPF text element uses the actual font, line breaking and line-height rules.
                // Do not remeasure live elements: doing so can hide a broken layout by repairing it.
                var probe = new TextBlock
                {
                    Text = text, FontFamily = source.FontFamily, FontSize = source.FontSize,
                    FontWeight = source.FontWeight, FontStyle = source.FontStyle, FontStretch = source.FontStretch,
                    Language = source.Language, FlowDirection = source.FlowDirection,
                    TextWrapping = source.TextWrapping, LineHeight = source.LineHeight,
                    LineStackingStrategy = source.LineStackingStrategy, Padding = source.Padding,
                };
                TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(source));
                TextOptions.SetTextRenderingMode(probe, TextOptions.GetTextRenderingMode(source));
                probe.Measure(new Size(source.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : source.ActualWidth + 0.5, double.PositiveInfinity));
                if (!trims && probe.DesiredSize.Width > source.ActualWidth + 2)
                    findings.Add($"{name}: text width {probe.DesiredSize.Width:0.0} > {source.ActualWidth:0.0}: {label}");
                if (probe.DesiredSize.Height > source.ActualHeight + 2)
                    findings.Add($"{name}: text height {probe.DesiredSize.Height:0.0} > {source.ActualHeight:0.0}: {label}");
            }

            var scrollX = false;
            var scrollY = false;
            for (DependencyObject? parent = element; parent is Visual; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollContentPresenter presenter && presenter.TemplatedParent is ScrollViewer scroll)
                {
                    scrollX |= scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && scroll.ExtentWidth > scroll.ViewportWidth + 1;
                    scrollY |= scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && scroll.ExtentHeight > scroll.ViewportHeight + 1;
                }
                if (parent is not FrameworkElement ancestor) continue;
                var clip = LayoutInformation.GetLayoutClip(ancestor);
                var bounds = element.TransformToAncestor((Visual)parent).TransformBounds(new Rect(element.RenderSize));
                Rect? limit = clip?.Bounds;
                if (parent == window) limit = new Rect(window.RenderSize);
                if (limit is not Rect area) continue;
                if ((!scrollX && !trims && (bounds.Left < area.Left - 2 || bounds.Right > area.Right + 2)) ||
                    (!scrollY && (bounds.Top < area.Top - 2 || bounds.Bottom > area.Bottom + 2)))
                {
                    findings.Add($"{name}: ancestor clips {label} at {parent.GetType().Name}/{ancestor.Name} ({bounds.Width:0}x{bounds.Height:0} in {area.Width:0}x{area.Height:0})");
                    break;
                }
            }
        }
    }

    private static string RenderedText(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text)) return block.Text;
        // AccessText uses an internal text container; its TextBlock.Text may be empty.
        var access = Ancestors(block).OfType<AccessText>().FirstOrDefault();
        return access is null ? new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text
            : access.Text.Replace("__", "\u0000").Replace("_", "").Replace("\u0000", "_");
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (DependencyObject? current = element; current is Visual; current = VisualTreeHelper.GetParent(current)) yield return current;
    }

    private static void AssertNarrowWorkspacePanelToggle(Window window, string name, HashSet<string> findings)
    {
        if (window is not IPhoneMirror.App.MainWindow || !name.EndsWith("workspace-settings")) return;
        var setSettings = window.GetType().GetMethod("SetSettingsPanelVisible", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var center = (FrameworkElement)window.FindName("CenterPanel");
        var settings = (FrameworkElement)window.FindName("ControlPanel");
        setSettings.Invoke(window, [false]);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(360));
        window.UpdateLayout();
        if (Grid.GetRowSpan(center) != 1 || Grid.GetColumn(settings) != 4)
            findings.Add(name + ": closing narrow settings panel left a stale stacked layout");
        setSettings.Invoke(window, [true]);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(360));
        window.UpdateLayout();
        if (Grid.GetRowSpan(center) != 2 || Grid.GetColumn(settings) != 0)
            findings.Add(name + ": reopening narrow settings panel did not restore adaptive layout");
    }

    private static void AuditAllTabs(Window window, string name, HashSet<string> findings, string? renderPath = null)
    {
        foreach (var issue in FindSystemBlackContent(window)) findings.Add($"{name}: {issue}");
        AuditAdaptiveLayout(window, name, findings);
        var expanderIndex = 0;
        foreach (var expander in Visuals(window).OfType<Expander>().Where(e => e.IsVisible).ToArray())
        {
            var expanded = expander.IsExpanded;
            expander.IsExpanded = true;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
            window.UpdateLayout();
            AuditAdaptiveLayout(window, name + "/expanded", findings);
            if (renderPath != null) SaveWindowRender(window, renderPath + $"-expanded-{expanderIndex++}.png");
            expander.IsExpanded = expanded;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
            window.UpdateLayout();
        }
        foreach (var tabs in Visuals(window).OfType<TabControl>().ToArray())
        {
            var selected = tabs.SelectedIndex;
            for (var index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
                window.UpdateLayout();
                foreach (var issue in FindSystemBlackContent(window)) findings.Add($"{name}/tab-{index}: {issue}");
                if (tabs.SelectedContent is FrameworkElement content && (!content.IsVisible || content.ActualHeight <= 0))
                    findings.Add($"{name}: tab {index} content is not visible");
                AuditAdaptiveLayout(window, $"{name}/tab-{index}", findings);
                if (renderPath != null) SaveWindowRender(window, renderPath + $"-tab-{index}.png");
            }
            tabs.SelectedIndex = selected;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
            window.UpdateLayout();
        }
    }
}
