using System.Collections;
using System.IO;
using System.Reflection;
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
    private static int RunDriverComboBoxTests(string assemblyPath)
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
