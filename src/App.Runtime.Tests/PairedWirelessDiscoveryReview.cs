using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPairedWirelessDiscoveryReview(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "review-settings.json")));
        LocalizationService.SetLanguage("zh-CN");
        ThemeService.Apply(AppTheme.Dark);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        vm.Devices.CollectionChanged -= (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
            typeof(NotifyCollectionChangedEventHandler), window,
            typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
        try
        {
            var network = DeviceViewModel.FromPairedNetwork(new("00008150-0000000000000001",
                "我的 iPhone", "iPhone17,1", "26.0"), null);
            var wired = DeviceViewModel.FromNative(new NativeDeviceInfo {
                Udid = "00008101-0000000000000002", Name = "iPad", ProductType = "iPad13,1",
                ConnectionType = "USB", OsVersion = "26.0", State = ConnectionState.Ready
            }, DeviceAppearance.Unknown);
            KeyboardCall(vm, "ReconcileDevices", new[] { network, wired }, network.Udid, false, null);
            KeyboardCall(window, "SetLeftWorkspacePanel",
                Enum.Parse(KeyboardField(window, "_leftWorkspacePanel").GetType(), "Devices"));
            FinishWorkspaceReveal(window);
            var panel = (FrameworkElement)window.FindName("DevicePanel");
            void Capture(string name)
            {
                ToolsPump(150);
                window.UpdateLayout();
                var image = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),
                    (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen())
                    drawing.DrawRectangle(window.TryFindResource("WindowBackgroundBrush") as Brush ?? Brushes.Black,
                        null, new Rect(panel.RenderSize));
                image.Render(background);
                image.Render(panel);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(output, name));
                encoder.Save(file);
            }
            Capture("paired-wireless-dark.png");
            void VerifyHeaderFocus(string theme)
            {
                foreach (var name in new[] { "RefreshDevicesButton", "SourceCardOptionsButton", "CloseDevicePanelButton" })
                {
                    var button = (Button)window.FindName(name);
                    InteractionAssert(button.FocusVisualStyle is null && button.Focus(),
                        name + " must retain keyboard focus without an external focus frame.");
                    window.UpdateLayout(); ToolsPump();
                    var border = (Border)button.Template.FindName("IconRoot", button);
                    InteractionAssert(border.CornerRadius.TopLeft >= 8 &&
                        ((SolidColorBrush)border.BorderBrush).Color == ((SolidColorBrush)button.FindResource("AccentBrush")).Color,
                        name + " must show its single rounded focus border.");
                    Capture($"header-focus-{theme}-{name}.png");
                }
                var popupButton = (Button)window.FindName("SourceCardOptionsButton");
                var popup = new ContextMenu { PlacementTarget = popupButton };
                popup.Items.Add(new MenuItem { Header = "Focus return test" });
                popup.IsOpen = true; ToolsPump(); popup.IsOpen = false; ToolsPump();
                InteractionAssert(popupButton.Focus() && popupButton.FocusVisualStyle is null,
                    "Returning from a popup must not restore the old outer focus frame.");
            }
            VerifyHeaderFocus("dark");
            var labels = FindVisualDescendants<TextBlock>(panel).Where(text => text.IsVisible).Select(text => text.Text).ToArray();
            InteractionAssert(labels.Contains("已配对 · 无线在线") && labels.Contains("Wi-Fi"),
                "The real device list must distinguish a verified Wi-Fi device from USB and active AirPlay.");
            ThemeService.Apply(AppTheme.Light);
            Capture("paired-wireless-light.png");
            VerifyHeaderFocus("light");
            foreach (var style in new[] { "SecondaryButton", "PrimaryButton", "DangerButton", "GhostButton",
                "CompactButton", "ToolButton", "IconButton", "TitleBarButton", "SubWindowCloseButton" })
            {
                var styled = new Button { Style = (Style)app.FindResource(style) };
                InteractionAssert(styled.FocusVisualStyle is null, style + " must not add a duplicate focus adorner.");
            }
            var preflight = (Task<(bool Success, int ErrorCode, CaptureFailureKind FailureKind,
                string Message, CaptureFailureStage FailureStage)>)KeyboardCall(vm, "EnsureSourceReadyAsync", network, CancellationToken.None)!;
            ToolsComplete(preflight);
            InteractionAssert(!preflight.Result.Success && preflight.Result.ErrorCode == 0 &&
                preflight.Result.Message == LocalizationService.Get("CoreDeviceMirrorUnsupported"),
                "A network device on an older OS must explain CoreDevice support without entering USB preflight.");
            var requested = new List<DeviceViewModel>();
            var pendingStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.CoreDeviceMirrorStarter = (device, cancellation) =>
            {
                requested.Add(device);
                return pendingStart.Task.WaitAsync(cancellation);
            };
            KeyboardCall(vm, "SetSelectedDevice", network, false);
            var startButton = (Button)window.FindName("CaptureActionButton");
            InteractionAssert(ReferenceEquals(startButton.Command, vm.StartCommand) && vm.StartCommand.CanExecute(null),
                "An online network device must expose the ordinary Start mirroring action.");
            vm.StartCommand.Execute(null);
            ToolsPump();
            InteractionAssert(requested.SequenceEqual(new[] { network }) && vm.IsBusy && !vm.StartCommand.CanExecute(null),
                "Start mirroring must route the selected Wi-Fi device to CoreDevice and disable duplicate starts.");
            ToolsComplete((Task)KeyboardCall(vm, "StartAsync")!);
            InteractionAssert(requested.Count == 1, "A duplicate click started a second CoreDevice producer.");
            KeyboardCall(vm, "SetSelectedDevice", wired, false);
            pendingStart.SetResult(); ToolsPump(150);
            InteractionAssert(!vm.IsBusy && ReferenceEquals(vm.SelectedDevice, wired),
                "CoreDevice startup completion must release busy state without changing a newer selection.");
            pendingStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backgroundStart = vm.StartBackgroundSessionAsync(network);
            InteractionAssert(requested.Count == 2 && ReferenceEquals(requested[^1], network),
                "Independent and shortcut starts must use the same CoreDevice route for a Wi-Fi device.");
            pendingStart.SetResult(); ToolsComplete(backgroundStart);
            vm.CoreDeviceMirrorStarter = null;
            InteractionAssert(!vm.IsBusy && ReferenceEquals(vm.SelectedDevice, wired),
                "Background CoreDevice routing must retain the current device and release lifecycle ownership.");
            var usb = DeviceViewModel.FromNative(new NativeDeviceInfo {
                Udid = network.Udid, Name = network.Name, ProductType = network.ProductType, OsVersion = network.OsVersion,
                ConnectionType = "USB", State = ConnectionState.Ready
            }, DeviceAppearance.Unknown);
            KeyboardCall(vm, "ReconcileDevices", new[] { usb, wired }, network.Udid, false, null);
            InteractionAssert(vm.Devices.Count == 2 && ReferenceEquals(vm.Devices[0], network) &&
                ReferenceEquals(vm.SelectedDevice, network) && !network.IsPairedNetwork && network.Ready,
                "Plugging USB must reuse the selected card without duplicates or selection changes.");
            Capture("paired-wireless-to-usb.png");
            var discovery = (PairedDeviceDiscovery)KeyboardField(vm, "_pairedDeviceDiscovery");
            var knownNetwork = new PairedNetworkDevice(network.Udid, network.Name, network.ProductType, network.OsVersion);
            var noProbe = new CancellationToken(canceled: true);
            foreach (var transition in new[] { "usb-unplug", "coredevice-stop" })
            {
                discovery.ObserveVerifiedDevices([knownNetwork]);
                discovery.SnapshotAndRefresh([network.Udid], noProbe);
                var resumed = discovery.SnapshotAndRefresh([], noProbe);
                InteractionAssert(resumed.Count == 1, transition + " discarded the last verified network device.");
                var rediscovered = DeviceViewModel.FromPairedNetwork(resumed.Single(), null);
                KeyboardCall(vm, "ReconcileDevices", new[] { rediscovered, wired }, network.Udid, false, null);
                InteractionAssert(vm.Devices.Count == 2 && ReferenceEquals(vm.Devices[0], network) &&
                    ReferenceEquals(vm.SelectedDevice, network) && network.IsPairedNetwork && !network.Ready,
                    transition + " must retain the same selected Wi-Fi card while verification resumes.");
                Capture("paired-wireless-after-" + transition + ".png");
            }
            File.WriteAllText(Path.Combine(output, "verification.txt"),
                "PASS: WPF Wi-Fi status, shared focused button styles, main/background CoreDevice routing, duplicate-click protection, selection retention, same-card CoreDevice stop and USB unplug handoffs.\n" +
                "Screenshots use fixture devices; they do not prove live wireless phone presence.\n");
            Console.WriteLine("Paired wireless WPF review passed: " + output);
            return 0;
        }
        finally { CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }
}
