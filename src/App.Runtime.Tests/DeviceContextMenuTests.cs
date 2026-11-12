using System.IO;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDeviceContextMenuTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        Console.WriteLine("Device menu: creating isolated workspace.");
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        Console.WriteLine("Device menu: workspace created.");
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        // Device onboarding is a separate modal flow, outside this menu test.
        vm.Devices.CollectionChanged -= (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
            typeof(NotifyCollectionChangedEventHandler), window,
            typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
        var list = (ListBox)window.FindName("DeviceListBox");
        const string firstId = "00008101-0000000000000001";
        const string otherId = "00008101-0000000000000002";
        DeviceViewModel Device(string udid, ConnectionState state = ConnectionState.Ready) =>
            DeviceViewModel.FromNative(new NativeDeviceInfo { Udid = udid, Name = udid == firstId ? "Phone A" : "Phone B",
                ProductType = "iPhone13,1", OsVersion = "18.7.8", ConnectionType = "USB", State = state });
        var first = Device(firstId); var other = Device(otherId);
        ContextMenu Menu(DeviceViewModel device)
        {
            window.UpdateLayout(); ToolsPump();
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(device);
            InteractionAssert(row is not null, "Device menu target has no row.");
            var menu = row!.ContextMenu; menu.PlacementTarget = row;
            // Anchor review captures to the row rather than the user's desktop cursor.
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
            menu.IsOpen = true; ToolsPump();
            return menu;
        }
        try
        {
            vm.Devices.Clear(); vm.Devices.Add(first); vm.Devices.Add(other);
            KeyboardCall(vm, "SetSelectedDevice", first, false);
            Console.WriteLine("Device menu: fixtures loaded.");
            KeyboardCall(window, "SetLeftWorkspacePanel", Enum.Parse(KeyboardField(window, "_leftWorkspacePanel").GetType(), "Devices"));
            FinishWorkspaceReveal(window);
            var sent = new List<(string, DeviceAction)>();
            window.ExecuteDeviceAction = (id, action, token) => { sent.Add((id, action)); return Task.CompletedTask; };
            var confirmations = 0;
            window.ConfirmDeviceAction = (_, _) => { confirmations++; return false; };
            var menu = Menu(other);
            Console.WriteLine("Device menu: context opened.");
            var items = menu.Items.OfType<MenuItem>().ToArray();
            InteractionAssert(items.Select(item => item.Tag as string).SequenceEqual(new[] { "CopyInfo", "Restart", "Shutdown" }) &&
                items.All(item => item.IsEnabled), "Device context menu retained mirroring/sleep or lost supported actions.");
            InteractionAssert(ReferenceEquals(vm.SelectedDevice, first), "Opening another device's menu switched the current source.");
            string? copied = null; window.CopyDeviceInfoText = text => copied = text;
            KeyboardCall(window, "OnDeviceActionClick", items[0], new RoutedEventArgs());
            InteractionAssert(copied?.Contains(otherId) == true && !copied.Contains(firstId), "Copy used the selected device rather than the clicked one.");
            foreach (var language in new[] { "zh-CN", "zh-HK", "zh-TW", "en-US", "ja-JP" })
            {
                DisplayLanguage(language); ToolsPump();
                InteractionAssert(items.All(item => item.Header is string text && text.Length > 0 && !text.StartsWith("DeviceAction")),
                    "A device action label is missing a translation.");
            }
            DisplayLanguage("zh-CN"); ToolsPump();
            // Popup HWNDs are not part of a Window's WPF render tree.
            // Composite both visuals to retain the actual menu in the review image.
            var dpi = VisualTreeHelper.GetDpi(window);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var menuOrigin = window.PointFromScreen(menu.PointToScreen(new Point()));
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(menu), null,
                    new Rect(menuOrigin, new Size(menu.ActualWidth, menu.ActualHeight)));
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, "device-context-menu.png"))) encoder.Save(stream);
            menu.IsOpen = false;
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Restart));
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Shutdown));
            InteractionAssert(confirmations == 2 && sent.Count == 0, "Cancel sent a power command.");
            window.ConfirmDeviceAction = (target, _) => { InteractionAssert(ReferenceEquals(target, other), "Confirmation targets the wrong device."); return true; };
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Restart));
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Shutdown));
            InteractionAssert(sent.Count == 2 && sent.All(entry => entry.Item1 == otherId) && ReferenceEquals(vm.SelectedDevice, first),
                "Power routing switched source, targeted another phone, or duplicated a command.");
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ExecuteDeviceAction = (_, _, _) => pending.Task;
            var running = window.RunDeviceActionAsync(other, DeviceAction.Restart); ToolsPump();
            menu = Menu(other);
            InteractionAssert(menu.Items.OfType<MenuItem>().Skip(1).All(item => !item.IsEnabled), "Busy power commands remain clickable.");
            menu.IsOpen = false;
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Restart));
            pending.SetResult(); ToolsComplete(running);
            window.ExecuteDeviceAction = (id, action, token) => { sent.Add((id, action)); return Task.CompletedTask; };
            var callsBeforeRemovedAction = sent.Count;
            ToolsComplete(window.RunDeviceActionAsync(other, (DeviceAction)0));
            InteractionAssert(sent.Count == callsBeforeRemovedAction, "The removed sleep action value was still accepted.");
            foreach (var device in new[] { Device("airplay://" + otherId), Device("00008101-0000000000000000", ConnectionState.Connected) })
            {
                vm.Devices.Add(device); menu = Menu(device);
                InteractionAssert(menu.Items.OfType<MenuItem>().First().IsEnabled && menu.Items.OfType<MenuItem>().Skip(1).All(item => !item.IsEnabled),
                    "Unavailable power actions were enabled for AirPlay or untrusted devices.");
                menu.IsOpen = false;
            }
            window.ConfirmDeviceAction = (_, _) => { vm.Devices.Remove(other); return true; };
            var callsBefore = sent.Count;
            ToolsComplete(window.RunDeviceActionAsync(other, DeviceAction.Restart));
            InteractionAssert(sent.Count == callsBefore, "A removed device received an action after confirmation.");
            var start = DeviceActionService.CreateStartInfo(Path.Combine(output, "bridge.exe"), otherId, DeviceAction.Shutdown);
            InteractionAssert(!start.UseShellExecute && start.CreateNoWindow && start.ArgumentList.SequenceEqual(
                new[] { "--device-action", "shutdown", "--udid", otherId, "--usb" }), "Unsafe or untargeted bridge invocation.");
            try { DeviceActionService.CreateStartInfo("bridge.exe", "airplay://" + otherId, DeviceAction.Restart); throw new InvalidOperationException("Invalid target accepted."); }
            catch (ArgumentException) { }
            try { DeviceActionService.CreateStartInfo("bridge.exe", otherId, (DeviceAction)0); throw new InvalidOperationException("Removed sleep action accepted."); }
            catch (ArgumentException) { }
            Console.WriteLine("PASS device menu: three actions, sleep removed/rejected, five languages, right target, unchanged selection, cancel/confirm, busy guard, USB/trust availability and disconnect recheck. No real device command sent.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }
}
