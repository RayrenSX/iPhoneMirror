using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWiredReadinessReview(string output)
    {
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
            var discoveryPending = DeviceViewModel.FromNative(new NativeDeviceInfo
            {
                Udid = "00008101-0000000000000001", Name = "iPhone 12 mini", ProductType = "iPhone13,1",
                ConnectionType = "USB", State = ConnectionState.UsbPresentNoMux,
            }, DeviceAppearance.Unknown);
            var capturePending = DeviceViewModel.FromInventory(new NativeDeviceInfo
            {
                Udid = "00008101-0000000000000002", Name = "iPhone 14 Pro", ProductType = "iPhone15,2",
                ConnectionType = "USB", OsVersion = "18.7.8", State = ConnectionState.Ready,
            }, captureNeedsPreparation: true);
            KeyboardCall(vm, "ReconcileDevices",
                new[] { discoveryPending, capturePending }, discoveryPending.Udid, false, null);
            KeyboardCall(window, "SetLeftWorkspacePanel",
                Enum.Parse(KeyboardField(window, "_leftWorkspacePanel").GetType(), "Devices"));
            FinishWorkspaceReveal(window);
            var panel = (FrameworkElement)window.FindName("DevicePanel");
            window.UpdateLayout();
            var labels = FindVisualDescendants<TextBlock>(panel).Where(text => text.IsVisible).Select(text => text.Text).ToArray();
            VerifyCardStatus(discoveryPending, "等待连接");
            VerifyCardStatus(capturePending, "投屏未就绪");
            InteractionAssert(labels.Count(label => label == discoveryPending.DisplayName) == 1 &&
                labels.Count(label => label == capturePending.DisplayName) == 1 &&
                !labels.Any(label => label.Contains("iOS", StringComparison.Ordinal)),
                "Each device card should show its name without an OS version.");
            CaptureDeviceList("wired-device-list.png");
            ThemeService.Apply(AppTheme.Light);
            CaptureDeviceList("wired-device-list-light.png");
            ThemeService.Apply(AppTheme.Dark);

            var captureReady = DeviceViewModel.FromInventory(new NativeDeviceInfo
            {
                Udid = capturePending.Udid, Name = capturePending.Name, ProductType = capturePending.ProductType,
                ConnectionType = capturePending.ConnectionType, OsVersion = capturePending.OsVersion, State = ConnectionState.Ready,
            }, captureNeedsPreparation: false);
            capturePending.UpdateFrom(captureReady);
            VerifyCardStatus(capturePending, "可投屏");

            void VerifyCardStatus(DeviceViewModel device, string expected)
            {
                window.UpdateLayout(); ToolsPump();
                var status = FindVisualDescendants<TextBlock>(panel).SingleOrDefault(text =>
                    ReferenceEquals(text.DataContext, device) && text.Text == expected);
                InteractionAssert(status?.Parent is Grid { ToolTip: string detail } && detail == device.StatusDisplay,
                    "Device cards must show the short status and retain the full explanation on hover.");
            }

            void CaptureDeviceList(string fileName)
            {
                ToolsPump(120);
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
                using var file = File.Create(Path.Combine(output, fileName));
                encoder.Save(file);
            }

            var probe = (Task<(bool Success, int ErrorCode, CaptureFailureKind FailureKind,
                string Message, CaptureFailureStage FailureStage)>)KeyboardCall(vm, "EnsureSourceReadyAsync",
                discoveryPending, CancellationToken.None)!;
            ToolsComplete(probe);
            InteractionAssert(!probe.Result.Success && probe.Result.FailureStage == CaptureFailureStage.UsbPreflight &&
                probe.Result.ErrorCode == (int)NativeResult.DeviceNotFound,
                "A Windows-only row must reach the exact-device driver check, which rejects an absent test device.");

            var windowsOnly = DeviceViewModel.FromNative(new NativeDeviceInfo
            {
                Udid = discoveryPending.Udid, Name = "Apple Mobile Device USB Composite Device",
                ConnectionType = "USB", State = ConnectionState.UsbPresentNoMux,
            }, DeviceAppearance.Unknown);
            // Use a fresh card so a synthetic product name does not mask a
            // real Windows-only placeholder before the management reply.
            vm.Devices.Remove(discoveryPending);
            vm.Devices.Insert(0, windowsOnly);
            KeyboardCall(vm, "SetSelectedDevice", windowsOnly, false);
            CaptureDeviceList("wired-metadata-before.png");
            JsonElement Metadata(string identifier) => JsonSerializer.SerializeToElement(new
            {
                device = new { available = true, values = new
                {
                    UniqueDeviceID = identifier, DeviceName = "我的 iPhone", ProductType = "iPhone13,1", ProductVersion = "26.0"
                } }
            });
            KeyboardCall(vm, "ApplyWiredDisplayMetadata", windowsOnly, windowsOnly.Udid, Metadata(capturePending.Udid));
            InteractionAssert(windowsOnly.ProductType.Length == 0, "Another phone's metadata was accepted.");
            KeyboardCall(vm, "ApplyWiredDisplayMetadata", windowsOnly, windowsOnly.Udid,
                Metadata(windowsOnly.Udid.Replace("-", "").ToUpperInvariant()));
            windowsOnly.UpdateFrom(DeviceViewModel.FromNative(new NativeDeviceInfo
            {
                Udid = windowsOnly.Udid, Name = "Apple Mobile Device USB Composite Device",
                ConnectionType = "USB", State = ConnectionState.UsbPresentNoMux,
            }, DeviceAppearance.Unknown));
            InteractionAssert(windowsOnly.DisplayName == "我的 iPhone" && windowsOnly.ProductType == "iPhone13,1" &&
                windowsOnly.ModelDisplay == "iPhone 12 mini" && windowsOnly.OsDisplay == "iOS 26.0" && vm.SelectedName == "我的 iPhone",
                "Refresh must preserve verified phone metadata even when Apple USBMux is unavailable.");
            CaptureDeviceList("wired-metadata-after.png");
            var namedDeviceLabels = FindVisualDescendants<TextBlock>(panel).Where(text => text.IsVisible && ReferenceEquals(text.DataContext, windowsOnly))
                .Select(text => text.Text).ToArray();
            InteractionAssert(namedDeviceLabels.Contains(windowsOnly.DisplayName) &&
                !namedDeviceLabels.Any(label => label.Contains(windowsOnly.ModelDisplay, StringComparison.Ordinal) ||
                    label.Contains(windowsOnly.OsDisplay, StringComparison.Ordinal)),
                "A named device card must not show a separate model or OS version.");
            vm.Devices.Remove(windowsOnly);
            vm.Devices.Insert(0, discoveryPending);
            KeyboardCall(vm, "SetSelectedDevice", discoveryPending, false);

            CaptureStatusNoticeWindow.ShowDeveloperErrorPreview(window, discoveryPending.DisplayName);
            var dialog = app.Windows.OfType<CaptureStatusNoticeWindow>().Single();
            try
            {
                var visible = FindVisualDescendants<TextBlock>(dialog).Where(text => text.IsVisible)
                    .Select(text => text.Text).ToArray();
                InteractionAssert(visible.Contains(CaptureErrorGuidance.ErrorCodeText(-2103)) &&
                    visible.Contains(CaptureErrorGuidance.FailureStageText(CaptureFailureStage.InterfaceOpen)) &&
                    !dialog.BodyText.Contains("-2103", StringComparison.Ordinal),
                    "The start failure must show the stage and code apart from the recovery message.");
                AssertNoticeHeaderAlignment(dialog, "HeaderTitle");
                ToolsCapture(dialog, Path.Combine(output, "wired-start-failure.png"));
                ThemeService.Apply(AppTheme.Light);
                ToolsCapture(dialog, Path.Combine(output, "wired-start-failure-light.png"));
            }
            finally { dialog.Close(); }
            ThemeService.Apply(AppTheme.Dark);
            CaptureStatusNoticeWindow.ShowDeveloperReverseControlErrorPreview(window);
            var reverse = app.Windows.OfType<CaptureStatusNoticeWindow>().Single();
            try
            {
                AssertNoticeHeaderAlignment(reverse, "HeaderTitle");
                AssertInlineDiagnostic(reverse, "TechnicalDetailsText", "USB / bridge startup timeout");
                ToolsCapture(reverse, Path.Combine(output, "reverse-control-failure.png"));
            }
            finally { reverse.Close(); }
            var startup = new StartupErrorWindow(new DllNotFoundException("Preview runtime component"),
                @"C:\iPhoneMirror\Logs\startup.log") { Owner = window };
            startup.Show();
            try
            {
                AssertNoticeHeaderAlignment(startup, "HeadingText");
                AssertInlineDiagnostic(startup, "DetailsTextBox", "Preview runtime component");
                ToolsCapture(startup, Path.Combine(output, "startup-failure.png"));
            }
            finally { startup.Close(); }
            void CapturePreview(Action show, Type type, string filename)
            {
                show();
                var preview = app.Windows.Cast<Window>().Single(item => item.GetType() == type);
                try { ToolsCapture(preview, Path.Combine(output, filename)); }
                finally { preview.Close(); }
            }
            CapturePreview(() => CaptureStatusNoticeWindow.ShowDeveloperUsbPreview(window),
                typeof(CaptureStatusNoticeWindow), "usb-configuration-failure.png");
            CapturePreview(() => CaptureStatusNoticeWindow.ShowDeveloperStoppedPreview(window),
                typeof(CaptureStatusNoticeWindow), "capture-stopped.png");
            CapturePreview(() => CaptureRecoveryWindow.ShowDeveloperPreview(window),
                typeof(CaptureRecoveryWindow), "capture-timeout-recovery.png");
            CapturePreview(() => AppPromptWindow.ShowDeveloperPreview(window),
                typeof(AppPromptWindow), "application-prompt.png");
            window.Width = 1120;
            window.Height = 720;
            ToolsPump(200);
            var status = vm.GetControlStatus(ControlStatusMode.Usb, discoveryPending.Udid);
            status.Begin(ControlStatusMode.Usb, discoveryPending.DisplayName);
            ReverseControlStatusWindow.Show(window, status, cancel: () => { }, retry: () => { });
            var controlStatus = app.Windows.OfType<ReverseControlStatusWindow>().Single(item => item.IsVisible);
            try
            {
                var handle = new WindowInteropHelper(controlStatus).Handle;
                var control = KeyboardCall(vm, "FindControl", discoveryPending.Udid)!;
                using var confirmationLifetime = new CancellationTokenSource();
                var confirmation = (Task<bool>)KeyboardCall(vm, "ConfirmReverseControlPrerequisitesAsync",
                    control, false, confirmationLifetime.Token)!;
                try
                {
                    ToolsCapture(controlStatus, Path.Combine(output, "reverse-control-developer-confirmation.png"));
                    InteractionAssert(!confirmation.IsCompleted &&
                        ReferenceEquals(app.Windows.OfType<ReverseControlStatusWindow>().Single(item => item.IsVisible), controlStatus),
                        "Developer-mode confirmation must block startup in the open reverse-control window.");
                    var proceed = FindVisualDescendants<Button>(controlStatus).Single(button =>
                        System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "ControlPromptPrimaryButton");
                    InteractionAssert(proceed.IsVisible && proceed.IsEnabled, "The confirmation requires a visible Continue action.");
                    proceed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ToolsComplete(confirmation);
                    InteractionAssert(confirmation.Result, "Continue did not confirm the reverse-control prerequisites.");
                }
                finally { confirmationLifetime.Cancel(); }
                status.Report(ControlStatusMode.Usb, ControlStage.Connecting, discoveryPending.DisplayName,
                    LocalizationService.Get("ControlPreparing"));
                ToolsCapture(controlStatus, Path.Combine(output, "reverse-control-starting.png"));
                CaptureControlOverWorkspace(window, controlStatus, output, "reverse-control-starting-in-app.png");
                status.FailCurrent(LocalizationService.Get("UsbControlFailureDeviceConnectionTimeout"),
                    "USB / bridge startup timeout");
                ToolsPump(400);
                var shown = app.Windows.OfType<ReverseControlStatusWindow>().Single(item => item.IsVisible);
                InteractionAssert(ReferenceEquals(shown, controlStatus) && new WindowInteropHelper(shown).Handle == handle
                    && !app.Windows.OfType<CaptureStatusNoticeWindow>().Any(item => item.IsVisible),
                    "A failure must reuse the open reverse-control window without opening another warning.");
                AssertNoticeHeaderAlignment(controlStatus, "HeaderTitle");
                AssertInlineDiagnostic(controlStatus, "FailureDetailsText", "USB / bridge startup timeout");
                ToolsCapture(controlStatus, Path.Combine(output, "reverse-control-status-failure.png"));
                CaptureControlOverWorkspace(window, controlStatus, output, "reverse-control-failure-in-app.png");
            }
            finally { controlStatus.Close(); }
            Console.WriteLine("The original reverse-control prerequisite prompt appeared in the same window and Continue confirmed it.");
            Console.WriteLine("Reverse-control failure reused the same window and native handle.");
            Console.WriteLine("Wired device card states and failure notice verified.");
            return 0;
        }
        finally { CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }

    private static void CaptureControlOverWorkspace(Window owner, Window status, string output, string fileName)
    {
        owner.UpdateLayout();
        status.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(owner);
        var offset = status.PointToScreen(new Point()) - owner.PointToScreen(new Point());
        var size = owner.RenderSize;
        var surface = new DrawingVisual();
        using (var drawing = surface.RenderOpen())
        {
            drawing.DrawRectangle(new VisualBrush(owner), null, new Rect(size));
            drawing.DrawRectangle(new VisualBrush(status), null, new Rect(
                new Point(offset.X / dpi.DpiScaleX, offset.Y / dpi.DpiScaleY), status.RenderSize));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, fileName));
        png.Save(file);
    }

    private static void AssertInlineDiagnostic(Window notice, string name, string expected)
    {
        notice.UpdateLayout();
        var details = (TextBox)notice.FindName(name);
        InteractionAssert(details.IsVisible && details.IsReadOnly && details.Text.Contains(expected, StringComparison.Ordinal)
            && !FindVisualDescendants<Expander>(notice).Any(),
            "Diagnostic details must be visible and selectable without expanding a section.");
    }

    private static void AssertNoticeHeaderAlignment(Window notice, string titleName)
    {
        notice.UpdateLayout();
        var header = (FrameworkElement)notice.FindName("NoticeHeader");
        var centers = new[] { "HeaderIcon", titleName, "HeaderClose" }.Select(name =>
        {
            var element = (FrameworkElement)notice.FindName(name);
            return element.TranslatePoint(new Point(0, element.ActualHeight / 2), header).Y;
        }).ToArray();
        InteractionAssert(centers.Max() - centers.Min() <= 1, "Notice header elements are vertically misaligned.");
    }
}
