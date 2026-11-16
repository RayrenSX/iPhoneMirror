using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;
using IPhoneMirror.UI.Animations;
using IPhoneMirror.UI.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int AuditJapaneseFrameworkResources(string publishedRoot)
    {
        var satellitePath = Path.GetFullPath(Path.Combine(publishedRoot, "ja", "PresentationFramework.resources.dll"));
        var satellite = Assembly.LoadFile(satellitePath);
        var menuLabels = new Dictionary<string, string>();
        foreach (var name in satellite.GetManifestResourceNames().Where(n => n.EndsWith(".resources", StringComparison.Ordinal)))
        {
            using var stream = satellite.GetManifestResourceStream(name)!;
            using var reader = new System.Resources.ResourceReader(stream);
            foreach (System.Collections.DictionaryEntry entry in reader)
                if (entry.Key is string key && key.Contains("ContextMenu", StringComparison.Ordinal) && entry.Value is string value)
                    menuLabels[key] = value;
        }
        foreach (var action in new[] { "Cut", "Copy", "Paste" })
            InteractionAssert(menuLabels.Any(pair => pair.Key.EndsWith("_" + action, StringComparison.Ordinal) &&
                pair.Value.Any(character => character is >= '\u3040' and <= '\u30ff')),
                "Japanese framework editing-menu label missing: " + action);
        Console.WriteLine($"Japanese WPF editing-menu resources passed: {satellitePath}; {menuLabels.Count} labels.");
        return 0;
    }

    private static int RunUiResourceRegressions(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var host = new Window { Style = null, Width = 400, Height = 300, ShowInTaskbar = false };
        host.Show(); app.MainWindow = host;
        try
        {
            foreach (var culture in SupportedUiCultures.All)
            {
                SetAuditCulture(culture);
                var failure = new IOException("UNLOCALIZED_IO_SENTINEL", new UnauthorizedAccessException("INNER_DIAGNOSTIC_SENTINEL"));
                foreach (var key in new[] { "DeviceBindingRefreshFailed", "DeviceBindingOperationFailed", "MediaOutputOperationFailed", "TrayStartFailed", "TrayRefreshFailed" })
                    InteractionAssert(LocalizationService.ErrorMessage(failure, key) == LocalizationService.Get(key), "Raw exception escaped into user feedback: " + key);
                var businessMessage = LocalizationService.Get("MediaOutputAlreadyRunning");
                InteractionAssert(ReferenceEquals(LocalizationService.ErrorMessage(new InvalidOperationException(businessMessage), "MediaOutputOperationFailed"), businessMessage),
                    "An already localized business refusal lost its specific instruction.");
                // Identical text supplied externally must not be mistaken for a known business refusal.
                InteractionAssert(LocalizationService.ErrorMessage(new IOException(new string(businessMessage.AsSpan())), "MediaOutputOperationFailed") ==
                    LocalizationService.Get("MediaOutputOperationFailed"), "Exception classification guessed from displayed text.");
            }
            SetAuditCulture("ja-JP");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Apply(theme); AccessibilityAppearance.Apply(app, 2, false);
                var textFactory = typeof(FirstRunSetupWindow).GetMethod("Text", BindingFlags.Static | BindingFlags.NonPublic)!;
                var welcome = (TextBlock)textFactory.Invoke(null, ["SetupWelcomeNote", 16d])!;
                host.Content = welcome;
                InteractionAssert(welcome.FontSize == 32, "Setup text factory does not scale.");
                AccessibilityAppearance.Apply(app, 1, false);
                InteractionAssert(welcome.FontSize == 16, "Open setup text does not react to a scale change.");
                foreach (var contrast in new[] { (Window: Brushes.White, Text: Brushes.Black), (Window: Brushes.Black, Text: Brushes.White) })
                {
                    AccessibilityAppearance.Apply(app, 2, true);
                    var palette = new ResourceDictionary { ["DialogSurfaceBrush"] = contrast.Window, ["TextBrush"] = contrast.Text };
                    app.Resources.MergedDictionaries.Add(palette);
                    host.Content = null;
                    var surface = new Border { Child = welcome };
                    host.Content = surface;
                    surface.SetResourceReference(Border.BackgroundProperty, "DialogSurfaceBrush");
                    welcome.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    host.UpdateLayout();
                    InteractionAssert(ReferenceEquals(surface.Background, contrast.Window) && ReferenceEquals(welcome.Foreground, contrast.Text),
                        "High contrast surface/text pair did not update.");
                    SaveWindowRender(host, Path.Combine(output, $"contrast-{theme}-{contrast.Window.Color}.png"));
                    surface.Child = null;
                    app.Resources.MergedDictionaries.Remove(palette);
                }
            }
            AccessibilityAppearance.Apply(app, 1, false);
            TestScrollBarMotion(host);
            Console.WriteLine("UI resource regressions passed: localized failures/business refusals, live setup text scaling, inverse contrast palettes, both scrollbar orientations and live reduced motion.");
            return 0;
        }
        finally { host.Close(); app.Shutdown(); }
    }

    private static void TestScrollBarMotion(Window host)
    {
        foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
        {
            var bar = new ScrollBar { Orientation = orientation, Maximum = 100, ViewportSize = 50, Value = 40,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (orientation == Orientation.Vertical) bar.Height = 220; else bar.Width = 220;
            host.Content = bar; AdvanceDispatcher(TimeSpan.FromMilliseconds(50)); host.UpdateLayout();
            var thumb = (Thumb)bar.Template.FindName("ScrollThumb", bar);
            thumb.ApplyTemplate();
            var grip = (FrameworkElement)thumb.Template.FindName("Grip", thumb);
            var track = (FrameworkElement)bar.Template.FindName("TrackSurface", bar);
            var lengthProperty = orientation == Orientation.Vertical ? FrameworkElement.HeightProperty : FrameworkElement.WidthProperty;
            var thicknessProperty = orientation == Orientation.Vertical ? FrameworkElement.WidthProperty : FrameworkElement.HeightProperty;
            var originalLength = (double)thumb.GetValue(lengthProperty);
            var originalThickness = orientation == Orientation.Vertical ? thumb.ActualWidth : thumb.ActualHeight;
            foreach (var animations in new[] { true, false })
            foreach (var hover in new[] { true, false })
            {
                ScrollBarMotion.SetAnimationsEnabled(bar, animations);
                ScrollBarMotion.ApplyState(bar, hover, animations);
                // Changing the preference during a transition must clear its clocks immediately.
                ScrollBarMotion.SetAnimationsEnabled(bar, false);
                if (animations)
                    InteractionAssert(!thumb.HasAnimatedProperties && !track.HasAnimatedProperties && !grip.HasAnimatedProperties,
                        "Changing reduced motion left a running animation clock.");
                ScrollBarMotion.ApplyState(bar, hover, false);
                host.UpdateLayout();
                InteractionAssert((double)grip.GetValue(thicknessProperty) == (hover ? 4 : 2) &&
                    Math.Abs(track.Opacity - (hover ? 0.62 : 0)) < 0.001 && !thumb.HasAnimatedProperties && !track.HasAnimatedProperties && !grip.HasAnimatedProperties,
                    $"Reduced motion must apply the final hover state immediately: {orientation}, hover={hover}/{bar.IsMouseOver}, enabled={ScrollBarMotion.GetIsEnabled(bar)}, loaded={bar.IsLoaded}, thickness={grip.GetValue(thicknessProperty)}, track={track.Opacity}, animated={thumb.HasAnimatedProperties}/{track.HasAnimatedProperties}/{grip.HasAnimatedProperties}.");
                InteractionAssert(originalThickness >= 10 &&
                    Math.Abs((orientation == Orientation.Vertical ? thumb.ActualWidth : thumb.ActualHeight) - originalThickness) < 0.01,
                    "Hover or reduced motion changed the wide scrollbar pointer target.");
                var pointerFindings = new HashSet<string>();
                AuditScrollBarPointerTargets(bar, $"{orientation}/{hover}/{animations}", pointerFindings);
                InteractionAssert(pointerFindings.Count == 0, string.Join(Environment.NewLine, pointerFindings));
                InteractionAssert(double.IsNaN(originalLength) ? double.IsNaN((double)thumb.GetValue(lengthProperty)) :
                    (double)thumb.GetValue(lengthProperty) == originalLength, "Hover compressed the scrollbar's draggable length.");
            }
        }
        host.Content = null;
    }
}
