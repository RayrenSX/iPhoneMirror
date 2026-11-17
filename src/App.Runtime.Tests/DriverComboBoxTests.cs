using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDriverComboBoxTests(string assemblyPath, string? layoutOutput = null)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Assembly.Load("Wpf.Ui");
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var app = (Application)Activator.CreateInstance(
            assembly.GetType("IPhoneMirror.DriverInstaller.App")!)!;
        app.GetType().GetProperty("IsUiPreviewMode", InteractionMembers)!.SetValue(app, true);
        app.GetType().GetMethod("InitializeComponent")!.Invoke(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Resources.MergedDictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"/{assembly.GetName().Name};component/Localization/Strings.zh-CN.xaml",
                UriKind.Relative),
        });
        var window = (Window)Activator.CreateInstance(
            assembly.GetType("IPhoneMirror.DriverInstaller.MainWindow")!)!;
        // Keep the regression independent of connected hardware and driver operations.
        var loaded = window.GetType().GetMethod("OnLoaded", InteractionMembers | BindingFlags.DeclaredOnly)!;
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, loaded);
        window.ShowInTaskbar = false;
        var selectedDevice = window.GetType().GetProperty("SelectedDevice")!;
        var devices = (IList)window.GetType().GetProperty("Devices")!.GetValue(window)!;
        var connected = (IList)window.GetType().GetProperty("ConnectedDevices")!.GetValue(window)!;
        for (var index = 0; index < 2; index++)
        {
            var device = Activator.CreateInstance(
                assembly.GetType("IPhoneMirror.DriverInstaller.Models.AppleDeviceRecord")!,
                [$@"USB\VID_05AC&PID_12A8\COMBO_TEST_{index}", $"COMBO_TEST_{index}",
                 $"Test iPhone {index}", index == 0 ? "iPhone13,1" : "iPhone18,3",
                 index == 0 ? "iPhone 12 mini" : "iPhone 17", $"Test phone {index}",
                 index == 0 ? "18.7.8" : "26.6.2", index + 1, "usbccgp", true,
                 index == 0, Array.Empty<string>(), (uint?)0, true,
                 "usb.inf", "Composite.Dev", "10.0.26100.1", Array.Empty<string>()])!;
            devices.Add(device);
            connected.Add(device);
        }
        selectedDevice.SetValue(window, connected[0]);
        try
        {
            window.Show();
            DrainDispatcher();
            if (layoutOutput is not null)
            {
                AssertDriverWindowLayout(window, assembly, layoutOutput);
                return 0;
            }
            var combo = Visuals(window).OfType<ComboBox>()
                .Single(control => ReferenceEquals(control.ItemsSource, connected));
            var advancedList = Visuals(window).OfType<ListBox>()
                .Single(control => ReferenceEquals(control.ItemsSource, devices));
            foreach (var index in new[] { 1, 0, 1 })
            {
                AssertDriverPopupInput(combo, index);
                // UI Automation commits selection after separately checking the actual
                // preview mouse route. It needs no global cursor or desktop input.
                var peer = UIElementAutomationPeer.CreatePeerForElement(combo).GetChildren()
                    .Where(child => child.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider)
                    .ElementAt(index);
                ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
                combo.IsDropDownOpen = false;
                DrainDispatcher();
                InteractionAssert(ReferenceEquals(selectedDevice.GetValue(window), connected[index]) &&
                    ReferenceEquals(advancedList.SelectedItem, connected[index]),
                    $"Device selection must update the model and the advanced device list: expected={index}, " +
                    $"combo={combo.SelectedIndex}, model={connected.IndexOf(selectedDevice.GetValue(window))}, " +
                    $"advanced={advancedList.SelectedIndex}.");
                InteractionAssert((bool)window.GetType().GetProperty("CanUninstallDriver")!.GetValue(window)! == (index == 0),
                    "Device-specific actions must follow the selected phone.");
                var detail = Visuals(window).OfType<TextBlock>().Single(text =>
                    BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == "SelectedDevice.DetailText");
                InteractionAssert(detail.Text == connected[index]!.GetType().GetProperty("DetailText")!.GetValue(connected[index]) as string,
                    "The device details must follow the selected phone.");
            }

            var theme = (ComboBox)window.FindName("ThemeComboBox");
            // Exercise the shared handler without persisting test theme preferences.
            BindingOperations.ClearBinding(theme, Selector.SelectedValueProperty);
            theme.SelectedIndex = 0;
            AssertDriverPopupInput(theme, 1);
            theme.IsDropDownOpen = false;
            combo.IsEnabled = false;
            RaiseDriverPreviewClick(combo);
            InteractionAssert(!combo.IsDropDownOpen, "A disabled device selector must stay closed.");
            Console.WriteLine("Driver ComboBox regressions passed: popup input, repeated device selection, details, actions, theme and disabled state.");
            return 0;
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }

    private static void AssertDriverWindowLayout(Window window, Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        var app = Application.Current;
        var appearance = assembly.GetType("IPhoneMirror.UI.Services.AccessibilityAppearance")!
            .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
        var advancedProperty = window.GetType().GetProperty("IsAdvancedMode")!;
        var footer = Visuals(window).OfType<Button>().Single(button =>
            BindingOperations.GetBinding(button, Button.ContentProperty)?.Path.Path == "AdvancedButtonText");
        var page = Visuals(window).OfType<ScrollViewer>().First();
        var localization = app.Resources.MergedDictionaries.Single(dictionary =>
            dictionary.Source?.OriginalString.Contains("/Localization/Strings.", StringComparison.Ordinal) == true);
        var initializeLanguage = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverLocalization")!
            .GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!;
        window.MinWidth = 0;
        window.MinHeight = 0;
        var count = 0;
        foreach (var language in new[] { "zh-CN", "zh-HK", "zh-TW", "en-US", "ja-JP" })
        foreach (var textScale in new[] { 1d, 2d })
        foreach (var size in language == "zh-CN"
            ? new[] { new Size(1180, 760), new Size(980, 640), new Size(800, 480) }
            : new[] { new Size(980, 640) })
        foreach (var advanced in new[] { false, true })
        {
            localization.Source = new Uri($"/{assembly.GetName().Name};component/Localization/Strings.{language}.xaml", UriKind.Relative);
            initializeLanguage.Invoke(null, [new[] { "--language", language }]);
            window.GetType().GetField("_appleSupport", InteractionMembers)!.SetValue(window, Activator.CreateInstance(
                assembly.GetType("IPhoneMirror.DriverInstaller.Models.AppleSupportStatus")!,
                [true, true, "Apple Mobile Device Service", true, "oem3.inf", app.FindResource("AppleMuxReady"), true, false, null]));
            window.GetType().GetField("_libUsb", InteractionMembers)!.SetValue(window, Activator.CreateInstance(
                assembly.GetType("IPhoneMirror.DriverInstaller.Models.LibUsbStackStatus")!,
                [true, true, true, "1.2.6.0", string.Format((string)app.FindResource("LibUsbRunning"), "1.2.6.0")]));
            foreach (var property in new[] { "AppleStatusText", "LibUsbStatusText", "AdvancedButtonText" })
                window.GetType().GetMethod("OnPropertyChanged", InteractionMembers, [typeof(string)])!.Invoke(window, [property]);
            window.GetType().GetProperty("OperationStatus")!.SetValue(window,
                string.Format((string)app.FindResource("DevicesReady"), 2));
            appearance.Invoke(null, [app, (double?)textScale, (bool?)false]);
            advancedProperty.SetValue(window, advanced);
            window.Width = size.Width;
            window.Height = size.Height;
            page.ScrollToTop();
            DrainDispatcher();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(550));
            window.UpdateLayout();
            var label = $"{(language == "zh-CN" ? "" : language + "-")}{(advanced ? "advanced" : "simple")}-{size.Width}x{size.Height}-text{textScale}";
            if (!advanced) AssertDriverSimpleAlignment(window, app, label, output);
            // Ordinary window sizes must show the footer without page scrolling.
            // This catches a viewport-height minimum plus outer margins, even
            // though the footer remains technically reachable via a scrollbar.
            if (textScale == 1 && size.Height >= 640)
            {
                var bounds = footer.TransformToAncestor(window).TransformBounds(new Rect(footer.RenderSize));
                InteractionAssert(bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight - 10 && page.ScrollableHeight < 1,
                    $"Driver footer must fit inside the window: {label}, bounds={bounds}, page overflow={page.ScrollableHeight}.");
                if (!advanced)
                {
                    var simpleScroll = Visuals(window).OfType<ScrollViewer>().Single(scroll =>
                        scroll.IsVisible && !ReferenceEquals(scroll, page));
                    InteractionAssert(simpleScroll.ScrollableHeight < 1,
                        $"The simple setup page must fit without scrolling at ordinary sizes: {label}, overflow={simpleScroll.ScrollableHeight}.");
                }
            }
            if (advanced)
            {
                var actions = Visuals(window).OfType<Button>().Where(button =>
                    BindingOperations.GetBinding(button, Button.ContentProperty)?.Path.Path == "InstallButtonText" ||
                    Equals(button.Content, app.FindResource("Repair")) || Equals(button.Content, app.FindResource("Uninstall"))).ToArray();
                InteractionAssert(actions.Length == 3 && actions.All(button => button.ActualHeight <= 72 * textScale),
                    $"Driver actions must keep their natural button height: {label}, heights={string.Join(",", actions.Select(button => button.ActualHeight))}.");
                foreach (var action in actions)
                {
                    for (DependencyObject? parent = VisualTreeHelper.GetParent(action); parent is not null;
                         parent = VisualTreeHelper.GetParent(parent))
                        if (parent is ScrollViewer scroll)
                        {
                            scroll.ScrollToEnd();
                            DrainDispatcher();
                            window.UpdateLayout();
                        }
                    action.BringIntoView();
                    DrainDispatcher();
                    var bounds = action.TransformToAncestor(window).TransformBounds(new Rect(action.RenderSize));
                    if (bounds.Bottom > window.ActualHeight)
                        SaveWindowRender(window, Path.Combine(output, label + "-failure.png"));
                    InteractionAssert(bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight,
                        $"Driver action must be reachable by scrolling: {label}, {action.Content}, bounds={bounds}.");
                }
            }
            footer.BringIntoView();
            DrainDispatcher();
            var footerBounds = footer.TransformToAncestor(window).TransformBounds(new Rect(footer.RenderSize));
            if (footerBounds.Bottom > window.ActualHeight)
                SaveWindowRender(window, Path.Combine(output, label + "-footer-failure.png"));
            InteractionAssert(footerBounds.Top >= 0 && footerBounds.Bottom <= window.ActualHeight,
                $"Driver footer must remain reachable with large text: {label}, bounds={footerBounds}, page={page.VerticalOffset}/{page.ScrollableHeight}.");
            AssertDriverHeaderFooterAlignment(window, app, page, footer, label);
            SaveWindowRender(window, Path.Combine(output, label + ".png"));
            count++;
        }
        Console.WriteLine($"Driver layout regressions passed: {count} renders across simple/advanced layouts, five languages, three window sizes and 100%/200% text.");
    }

    private static void AssertDriverSimpleAlignment(Window window, Application app, string label, string output)
    {
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window)
            .TransformBounds(new Rect(element.RenderSize));
        var selector = Visuals(window).OfType<ComboBox>().Single(control => control.IsVisible &&
            BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path.Path == "ConnectedDevices");
        var install = Visuals(window).OfType<Button>().Single(control => control.IsVisible &&
            Equals(control.Content, app.FindResource("InstallAll")));
        Border StatusCard(string path)
        {
            var text = Visuals(window).OfType<TextBlock>().Single(element => element.IsVisible &&
                BindingOperations.GetBinding(element, TextBlock.TextProperty)?.Path.Path == path);
            for (DependencyObject? parent = VisualTreeHelper.GetParent(text); parent is not null;
                parent = VisualTreeHelper.GetParent(parent))
                if (parent is Border border) return border;
            throw new InvalidOperationException("The status text has no visible card.");
        }
        var apple = Bounds(StatusCard("AppleStatusText"));
        var filter = Bounds(StatusCard("LibUsbStatusText"));
        var device = Bounds(selector);
        var primary = Bounds(install);
        var title = Bounds(Visuals(window).OfType<TextBlock>().Single(control => control.IsVisible &&
            Equals(control.Text, app.FindResource("PrepareTitle"))));
        var description = Bounds(Visuals(window).OfType<TextBlock>().Single(control => control.IsVisible &&
            Equals(control.Text, app.FindResource("PrepareDescription"))));
        var deviceLabel = Bounds(Visuals(window).OfType<TextBlock>().Single(control => control.IsVisible &&
            Equals(control.Text, app.FindResource("CurrentDevice"))));
        var detail = Bounds(Visuals(window).OfType<TextBlock>().Single(control => control.IsVisible &&
            BindingOperations.GetBinding(control, TextBlock.TextProperty)?.Path.Path == "SelectedDevice.DetailText"));
        var feedback = Bounds(Visuals(window).OfType<TextBlock>().Single(control => control.IsVisible &&
            BindingOperations.GetBinding(control, TextBlock.TextProperty)?.Path.Path == "OperationStatus"));
        var left = Math.Min(apple.Left, filter.Left);
        var right = Math.Max(apple.Right, filter.Right);
        var maximumEdgeError = new[]
        {
            Math.Abs(device.Left - left), Math.Abs(device.Right - right),
            Math.Abs(primary.Left - left), Math.Abs(primary.Right - right),
            Math.Abs(apple.Top - filter.Top), Math.Abs(apple.Bottom - filter.Bottom),
            Math.Abs(deviceLabel.Left - left), Math.Abs(detail.Left - left), Math.Abs(feedback.Left - left),
            Math.Abs((title.Left + title.Right - left - right) / 2),
            Math.Abs((description.Left + description.Right - left - right) / 2),
        }.Max();
        File.WriteAllText(Path.Combine(output, label + "-alignment.json"),
            JsonSerializer.Serialize(new { label, apple = apple.ToString(), filter = filter.ToString(),
                device = device.ToString(), primary = primary.ToString(), title = title.ToString(),
                description = description.ToString(), deviceLabel = deviceLabel.ToString(),
                detail = detail.ToString(), feedback = feedback.ToString(), maximumEdgeError },
                new JsonSerializerOptions { WriteIndented = true }));
        if (maximumEdgeError > 1)
            SaveWindowRender(window, Path.Combine(output, label + "-alignment-failure.png"));
        InteractionAssert(maximumEdgeError <= 1,
            $"Driver status cards, device selector and primary action must share the same content edges: " +
            $"{label}, cards={left:F1}..{right:F1}, selector={device.Left:F1}..{device.Right:F1}, " +
            $"primary={primary.Left:F1}..{primary.Right:F1}, maximum error={maximumEdgeError:F1}px.");
    }

    private static void AssertDriverHeaderFooterAlignment(Window window, Application app,
        ScrollViewer page, Button advanced, string label)
    {
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window)
            .TransformBounds(new Rect(element.RenderSize));
        double CenterY(Rect bounds) => (bounds.Top + bounds.Bottom) / 2;
        var theme = Bounds((FrameworkElement)window.FindName("ThemeComboBox"));
        var refresh = Bounds(Visuals(window).OfType<Button>().Single(control => control.IsVisible &&
            Equals(control.ToolTip, app.FindResource("Refresh"))));
        var content = Bounds((FrameworkElement)page.Content);
        var notice = Bounds(Visuals(window).OfType<TextBlock>().Single(control => control.IsVisible &&
            Equals(control.Text, app.FindResource("DriverChangeNotice"))));
        var brand = Bounds(Visuals(window).OfType<TextBlock>().Single(control => control.IsVisible &&
            control.Text == "USB · Apple"));
        var footer = Bounds(advanced);
        InteractionAssert(Math.Abs(refresh.Right - content.Right) <= 1 &&
            Math.Abs(CenterY(theme) - CenterY(refresh)) <= 1 && theme.Right < refresh.Left,
            $"Driver theme and refresh controls must form an aligned header row: {label}, theme={theme}, refresh={refresh}, content={content}.");
        InteractionAssert(Math.Abs(notice.Left - content.Left) <= 1 &&
            Math.Abs(brand.Right - content.Right) <= 1 && Math.Abs(CenterY(notice) - CenterY(footer)) <= 1,
            $"Driver footer notice and advanced action must share a baseline and the page edges: {label}, " +
            $"notice={notice}, advanced={footer}, brand={brand}, content={content}.");
    }

    private static void AssertDriverPopupInput(ComboBox combo, int index)
    {
        RaiseDriverPreviewClick(combo);
        DrainDispatcher();
        InteractionAssert(combo.IsDropDownOpen, "Clicking the selector must open the popup.");
        var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(index);
        var label = Visuals(item).OfType<TextBlock>().First();
        var click = RaiseDriverPreviewClick(label);
        InteractionAssert(!click.Handled && combo.IsDropDownOpen,
            "Clicking a popup item must reach the item without closing the popup during preview.");
        var popup = (Popup)(combo.Template.FindName("PART_Popup", combo) ??
            combo.Template.FindName("Popup", combo));
        click = RaiseDriverPreviewClick((UIElement)popup.Child);
        InteractionAssert(!click.Handled && combo.IsDropDownOpen,
            "Clicking popup padding must not toggle the selector.");
        RaiseDriverPreviewClick(combo);
        InteractionAssert(!combo.IsDropDownOpen, "Clicking the selector again must close the popup.");
        RaiseDriverPreviewClick(combo);
        DrainDispatcher();
    }

    private static MouseButtonEventArgs RaiseDriverPreviewClick(UIElement source)
    {
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
        };
        source.RaiseEvent(click);
        return click;
    }
}
