using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Updater;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunThemeContentAudit(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", DisplayInstance)!.SetValue(app, true);
        app.InitializeComponent();
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var failures = new List<string>();
        using var trace = new AuditTraceListener();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        try
        {
            var open = typeof(MainWindow).GetMethod("OpenDeveloperSurface", DisplayInstance)!;
            typeof(MainWindow).GetMethod("SetLeftWorkspacePanel", DisplayInstance)!.Invoke(owner,
                [Enum.Parse(typeof(MainWindow).GetNestedType("LeftWorkspacePanel", BindingFlags.NonPublic)!, "Devices")]);
            SeedThemeList((ListBox)owner.FindName("DeviceListBox"));
            foreach (var surface in new[] { "device-binding", "bluetooth-client-binding", "airplay-device-selection" })
            {
                open.Invoke(owner, [surface]);
                var dialog = app.Windows.Cast<Window>().Last(w => w != owner);
                SeedThemeList(Visuals(dialog).OfType<ListBox>().First());
            }
            var toolbar = (ScrollViewer)owner.FindName("PreviewToolbarScrollViewer");
            var actions = (StackPanel)owner.FindName("PreviewQuickActions");
            ((FrameworkElement)owner.FindName("EnvironmentPanel")).Visibility = Visibility.Visible;
            actions.Visibility = Visibility.Visible;
            var buttons = actions.Children.OfType<Button>().ToArray();
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-HK", "zh-TW" })
            {
                DisplayLanguage(culture);
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.Dark })
                {
                    ApplyTheme(typeof(App).Assembly, theme);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                    foreach (var window in app.Windows.Cast<Window>().Where(w => w.IsVisible))
                    {
                        window.UpdateLayout();
                        foreach (var list in Visuals(window).OfType<ListBox>().Where(l => l.ItemsSource is ThemeListItem[]))
                        {
                            // Exercise both selected and unselected device cards in the live tree.
                            foreach (var selection in new[] { 0, 1 })
                            {
                                list.SelectedIndex = selection;
                                window.UpdateLayout();
                                var labels = Visuals(list).OfType<TextBlock>().Where(t => t.IsVisible && t.Text == "Ray’s Phone").ToArray();
                                Check(labels.Length == 2, $"{window.GetType().Name}: populated device labels are not visible.");
                                foreach (var label in labels)
                                    Check(SameThemeColor(label.Foreground, (Brush)window.FindResource("TextBrush")),
                                        $"{culture}/{theme}/{window.GetType().Name}: device label is {label.Foreground}.");
                            }
                        }
                        foreach (var issue in FindSystemBlackContent(window)) failures.Add($"{culture}/{theme}: {issue}");
                        foreach (var item in Visuals(window).OfType<NavigationViewItem>())
                        foreach (var icon in Visuals(item).OfType<SymbolIcon>())
                            Check(SameThemeColor(icon.Foreground, item.Foreground),
                                $"{theme}: navigation icon {icon.Symbol} does not follow its item foreground.");
                        SaveWindowRender(window, Path.Combine(output, $"{culture}-{theme}-{window.GetType().Name}.png"));
                    }
                    foreach (var enabled in new[] { true, false })
                    {
                        foreach (var button in buttons) button.IsEnabled = enabled;
                        owner.UpdateLayout();
                        foreach (var button in buttons)
                        {
                            foreach (var label in (button.Content is StackPanel panel
                                         ? panel.Children.OfType<TextBlock>() : Enumerable.Empty<TextBlock>()))
                            {
                                Check(label.FontSize == 12 && label.FontWeight == FontWeights.Normal,
                                    $"Toolbar label {label.Text} should render at 12 DIP normal weight.");
                                Check(SameThemeColor(label.Foreground, button.Foreground),
                                    $"{theme}/{enabled}: toolbar label {label.Text} does not follow its button foreground.");
                            }
                            foreach (var icon in Visuals(button).OfType<SymbolIcon>())
                                Check(SameThemeColor(icon.Foreground, button.Foreground),
                                    $"{theme}/{enabled}: toolbar icon {icon.Symbol} does not follow its button foreground.");
                        }
                    }
                    foreach (var button in buttons) button.IsEnabled = true;
                    foreach (var width in new[] { 1200.0, 520.0, 240.0 })
                    {
                        // Constrain the actual toolbar independently of the monitor work area.
                        toolbar.Width = width;
                        owner.Width = 1800;
                        toolbar.ScrollToLeftEnd();
                        owner.UpdateLayout(); DrainDispatcher(); owner.UpdateLayout();
                        var bar = (ScrollBar)toolbar.Template.FindName("PART_HorizontalScrollBar", toolbar);
                        Check((width == 240) == (bar.Visibility == Visibility.Visible),
                            $"{culture}/{theme}/{width}: unexpected toolbar overflow visibility.");
                        if (bar.IsVisible)
                        {
                            var barBounds = bar.TransformToAncestor(toolbar).TransformBounds(new Rect(bar.RenderSize));
                            var contentBounds = actions.TransformToAncestor(toolbar).TransformBounds(new Rect(actions.RenderSize));
                            Check(barBounds.Top >= contentBounds.Bottom + 3.5,
                                $"Toolbar scrollbar overlaps buttons: {barBounds.Top} < {contentBounds.Bottom} + 4.");
                            toolbar.ScrollToRightEnd();
                            owner.UpdateLayout(); DrainDispatcher(); owner.UpdateLayout();
                            Check(Math.Abs(toolbar.HorizontalOffset - toolbar.ScrollableWidth) < 1,
                                "Toolbar cannot scroll to the final action.");
                            var last = buttons.Last().TransformToAncestor(toolbar).TransformBounds(new Rect(buttons.Last().RenderSize));
                            Check(last.Left >= 0 && last.Right <= toolbar.ViewportWidth + 1,
                                "Final toolbar button is unreachable.");
                            toolbar.ScrollToLeftEnd();
                            owner.UpdateLayout();
                        }
                        SaveWindowRender(owner, Path.Combine(output, $"{culture}-{theme}-toolbar-{width}.png"));
                    }
                    toolbar.ClearValue(FrameworkElement.WidthProperty);
                }
            }
        }
        finally
        {
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            foreach (var window in app.Windows.Cast<Window>().Where(w => w != owner).ToArray()) window.Close();
            CloseWorkspaceTestWindow(owner);
            app.Shutdown();
        }
        failures.AddRange(trace.Messages);
        File.WriteAllLines(Path.Combine(output, "failures.txt"), failures.Distinct());
        foreach (var failure in failures.Distinct()) Console.Error.WriteLine(failure);
        Console.WriteLine($"Theme content audit: {failures.Count} failures; populated lists, theme switching, navigation icons, toolbar states and scrolling checked.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static bool SameThemeColor(Brush actual, Brush expected) =>
        actual is SolidColorBrush a && expected is SolidColorBrush b && a.Color == b.Color;

    private static IEnumerable<string> FindSystemBlackContent(Window window)
    {
        foreach (var element in Visuals(window).OfType<FrameworkElement>().Where(e => e.IsVisible))
        {
            var (brush, label) = element switch
            {
                TextBlock text when !string.IsNullOrWhiteSpace(text.Text) => (text.Foreground, text.Text),
                SymbolIcon icon => (icon.Foreground, icon.Symbol.ToString()),
                _ => (null, ""),
            };
            if (brush is SolidColorBrush solid && solid.Color == Colors.Black &&
                !HasReadableSystemForeground(element, solid.Color))
                yield return $"{window.GetType().Name}: system-black foreground on {label}.";
        }
    }

    private static bool HasReadableSystemForeground(FrameworkElement element, Color foreground)
    {
        // Black is an accidental fallback in the app palettes, but is the
        // intended WindowText in light Windows contrast themes. In that mode,
        // validate the painted foreground/background pair instead of its name.
        if (!SystemParameters.HighContrast &&
            Environment.GetEnvironmentVariable("IPHONE_MIRROR_UI_AUDIT_HIGH_CONTRAST") != "1") return false;
        if (foreground != SystemColors.WindowTextColor && foreground != SystemColors.HighlightTextColor) return false;
        var background = SystemColors.WindowColor;
        foreach (var ancestor in Ancestors(element).Reverse())
        {
            var fill = ancestor switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Control control => control.Background,
                TextBlock text => text.Background,
                _ => null,
            };
            if (fill is not SolidColorBrush solid) continue;
            var alpha = solid.Color.A / 255d * solid.Opacity;
            background = Color.FromRgb(
                (byte)Math.Round(solid.Color.R * alpha + background.R * (1 - alpha)),
                (byte)Math.Round(solid.Color.G * alpha + background.G * (1 - alpha)),
                (byte)Math.Round(solid.Color.B * alpha + background.B * (1 - alpha)));
        }
        static double Channel(byte value) => value / 255d <= 0.04045
            ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var a = Luminance(foreground); var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) >= 4.5;
    }

    private static void SeedThemeList(ListBox list)
    {
        // Replace only the visual fixture; never select a real device or persist a binding.
        BindingOperations.ClearBinding(list, Selector.SelectedItemProperty);
        list.ItemsSource = new[] { new ThemeListItem("theme-device-1"), new ThemeListItem("theme-device-2") };
        list.SelectedIndex = 0;
        list.UpdateLayout();
    }

    private sealed record ThemeListItem(string Udid)
    {
        public string AutomationId => Udid;
        public string DisplayName => "Ray’s Phone";
        public string DeviceName => DisplayName;
        public string ModelDisplay => "iPhone 12 mini";
        public string ModelName => ModelDisplay;
        public string OsDisplay => "iOS 18.7.8";
        public string StatusDisplay => DisplayL("ConnectionReady");
        public string ConnectionTimeText => "2026-10-01 12:00";
        public string BindingText => "";
        public string IdentifierText => Udid;
        public bool IsBound => false;
        public bool CanBind => true;
    }
}
