using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunMainPreviewCompositionTests(bool highDpi = false, bool livePhone = false)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var dpiContext = WorkspaceSetDpiAwareness((nint)(-4));
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        IPhoneMirror.SharedUI.Services.WindowWorkAreaController.EnableForApplication();
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        app.UpdateSettings.MirroringAutoControlMode = ReverseControlMode.None;
        var main = livePhone ? new MainWindow { ShowInTaskbar = false } : CreateWorkspaceTestWindow(app);
        if (livePhone)
        {
            app.MainWindow = main;
            main.Show();
        }
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var core = (NativeCore)KeyboardField(vm, "_core");
        var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
        const string udid = "main-preview-composition-fixture";
        using var session = livePhone ? null : core.CreateExternalSession(udid, false, 0).Handle ??
            throw new InvalidOperationException("Could not create the synthetic preview session.");
        var hwnd = new WindowInteropHelper(main).Handle;
        var host = (NativePreviewHost)main.FindName("MainPreviewHost");
        var center = (Grid)main.FindName("CenterPanel");
        var previewStage = (Border)main.FindName("CompletePreviewStage");
        var viewport = (PreviewViewport)main.FindName("PreviewViewportSlot");
        var panel = (Border)main.FindName("PreviewPanel");
        var workspace = (Grid)main.FindName("MainContentGrid");
        var rootLayout = (Grid)main.FindName("RootLayout");
        var sourceCard = (FrameworkElement)main.FindName("LeftPanelHost");
        var settingsCard = (FrameworkElement)main.FindName("ControlPanel");
        var device = DeviceViewModel.FromNative(new NativeDeviceInfo
            { Udid = udid, Name = "Preview fixture", ProductType = "iPhone13,1", State = ConnectionState.Ready },
            DeviceAppearance.Unknown);
        const uint width = 1082, height = 2340;
        var lumaLength = checked((int)(width * height));
        var nv12 = new byte[lumaLength * 3 / 2];
        nv12.AsSpan(0, lumaLength).Fill(175);
        nv12.AsSpan(lumaLength).Fill(128);
        var green = Color.FromRgb(20, 220, 90);
        var blue = Color.FromRgb(29, 105, 225);
        void Settle()
        {
            main.UpdateLayout();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
            main.UpdateLayout();
        }

        void VerifyWaitingForPicture()
        {
            Settle();
            var ready = ((FrameworkElement)main.FindName("ReadyToMirrorState")).IsVisible;
            var waiting = ((FrameworkElement)main.FindName("WaitingForPictureState")).IsVisible;
            InteractionAssert(ready && waiting && !host.IsVisible,
                $"A session without a frame must show the waiting state and hide the native preview. " +
                $"Ready={ready}, waiting={waiting}, native={host.IsVisible}, capturing={vm.IsCapturing}.");
            InteractionAssert(!previewStage.IsVisible && ReferenceEquals(panel.Background, panel.FindResource("PreviewChromeBrush")),
                "The waiting state must use its themed background for readable text.");
        }

        void VerifyStageLayout()
        {
            Settle();
            var aspect = (double)vm.SourceVideoWidth / vm.SourceVideoHeight;
            var expectedWidth = Math.Min(viewport.ActualWidth, viewport.ActualHeight * aspect);
            var expectedHeight = Math.Min(viewport.ActualHeight, viewport.ActualWidth / aspect);
            var bounds = panel.TransformToAncestor(viewport).TransformBounds(new Rect(panel.RenderSize));
            InteractionAssert(previewStage.IsVisible &&
                Math.Abs(bounds.Width - expectedWidth) <= 1 && Math.Abs(bounds.Height - expectedHeight) <= 1 &&
                Math.Abs(bounds.Left - (viewport.ActualWidth - bounds.Width) / 2) <= 1 &&
                Math.Abs(bounds.Top - (viewport.ActualHeight - bounds.Height) / 2) <= 1,
                $"Complete preview does not fill and center within its stage: {bounds}, expected {expectedWidth:F1}×{expectedHeight:F1}.");
            var stageBounds = previewStage.TransformToAncestor(center).TransformBounds(new Rect(previewStage.RenderSize));
            var panelBounds = panel.TransformToAncestor(center).TransformBounds(new Rect(panel.RenderSize));
            InteractionAssert(viewport.Margin.Top == 0 && viewport.Margin.Bottom == 0 &&
                Math.Abs(stageBounds.Top - panelBounds.Top) <= 1 && Math.Abs(stageBounds.Bottom - panelBounds.Bottom) <= 1,
                $"The stage leaves vertical padding around the fitted picture: stage={stageBounds}, picture={panelBounds}.");
            var workspaceLeft = workspace.TranslatePoint(new Point(), rootLayout).X;
            InteractionAssert(Math.Abs(workspaceLeft) <= 1 && Math.Abs(rootLayout.ActualWidth - workspace.ActualWidth) <= 1,
                $"Complete workspace does not fill the content area: left={workspaceLeft}, width={workspace.ActualWidth}, available={rootLayout.ActualWidth}.");
            if (sourceCard.IsVisible)
            {
                var sourceLeft = sourceCard.TranslatePoint(new Point(), rootLayout).X;
                InteractionAssert(Math.Abs(sourceLeft) <= 1, $"The source card drifted away from the left edge: {sourceLeft}.");
            }
            if (settingsCard.IsVisible)
            {
                var settingsRight = settingsCard.TranslatePoint(new Point(settingsCard.ActualWidth, 0), rootLayout).X;
                InteractionAssert(Math.Abs(rootLayout.ActualWidth - settingsRight) <= 1,
                    $"The settings card drifted away from the right edge: {settingsRight}, expected {rootLayout.ActualWidth}.");
            }
        }

        void VerifyCaptureButtonColors()
        {
            var button = new Button { Style = ((Button)main.FindName("CaptureActionButton")).Style };
            // Drive the WPF state properties without moving the desktop mouse.
            const System.Reflection.BindingFlags staticField =
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var hover = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", staticField)!.GetValue(null)!;
            var press = (DependencyPropertyKey)typeof(System.Windows.Controls.Primitives.ButtonBase)
                .GetField("IsPressedPropertyKey", staticField)!.GetValue(null)!;
            button.ApplyTemplate();
            var root = (Border)button.Template.FindName("ActionRoot", button);
            foreach (var (capture, media) in new[] { (false, false), (true, false), (false, true) })
            {
                button.DataContext = new { HasCaptureSession = capture, IsMediaCasting = media };
                var prefix = capture || media ? "CaptureStop" : "CaptureStart";
                void VerifyFill(string suffix)
                {
                    DrainDispatcher();
                    InteractionAssert(root.Background is SolidColorBrush fill &&
                        fill.Color == ((SolidColorBrush)button.FindResource(prefix + suffix)).Color,
                        $"The capture button used {root.Background} for {prefix + suffix} (device={capture}, media={media}).");
                }
                VerifyFill("Brush");
                button.SetValue(hover, true);
                VerifyFill("HoverBrush");
                button.SetValue(press, true);
                VerifyFill("PressedBrush");
                button.SetValue(press, false);
                VerifyFill("HoverBrush");
                button.SetValue(hover, false);
                VerifyFill("Brush");
            }
            Console.WriteLine("PASS capture button hover and press: start, device stop and media stop use the proper themed colors.");
        }

        void VerifyCompletePicture(string stage, Color backdrop, DeviceCornerProfile? profile = null, bool square = false)
        {
            // The fitted video shares its top and bottom with the stage. Use
            // one backing color to sample its antialiasing independently of
            // the stage's own rounded outline.
            previewStage.Background = Brushes.Transparent;
            previewStage.BorderBrush = Brushes.Transparent;
            try { VerifyPicture(stage, backdrop, profile, square); }
            finally
            {
                previewStage.SetResourceReference(Border.BackgroundProperty, "PreviewChromeBrush");
                previewStage.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
            }
        }

        void VerifyPicture(string stage, Color? background, DeviceCornerProfile? profile = null, bool square = false)
        {
            Settle();
            // Keep the desktop samples inside the fixture even if another
            // topmost window appeared while the dispatcher was settling.
            _ = WorkspaceSetWindowPos(hwnd, (nint)(-1), 0, 0, 0, 0, 0x0013);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(35));
            InteractionAssert(host.WindowHandle != 0 && host.IsVisible, stage + ": preview host is hidden.");
            InteractionAssert(!((FrameworkElement)main.FindName("PreviewPlaceholder")).IsVisible,
                stage + ": the waiting state is still visible underneath the video.");
            PreviewStyleGetWindowRect(host.WindowHandle, out var bounds);
            var picture = PreviewCoordinateMapper.ContentRect(bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top, vm.SourceVideoWidth, vm.SourceVideoHeight);
            var left = bounds.Left + (int)Math.Round(picture.Left);
            var top = bounds.Top + (int)Math.Round(picture.Top);
            var right = bounds.Left + (int)Math.Round(picture.Right) - 1;
            var bottom = bounds.Top + (int)Math.Round(picture.Bottom) - 1;
            var middleX = (left + right) / 2;
            var middleY = (top + bottom) / 2;
            var samplePoint = new CompositionPoint(middleX, middleY);
            var sampleWindow = CompositionWindowFromPoint(samplePoint);
            if (CompositionGetRootWindow(sampleWindow, 2) != hwnd)
            {
                main.Activate();
                _ = WorkspaceSetWindowPos(hwnd, (nint)(-1), 0, 0, 0, 0, 0x0013);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                sampleWindow = CompositionWindowFromPoint(samplePoint);
            }
            InteractionAssert(CompositionGetRootWindow(sampleWindow, 2) == hwnd,
                stage + ": the preview is covered by another window.");
            var dc = PreviewStyleGetDC(0);
            int Distance(Color pixel, Color target) => Math.Max(Math.Abs(pixel.R - target.R),
                Math.Max(Math.Abs(pixel.G - target.G), Math.Abs(pixel.B - target.B)));
            Color Pixel(int x, int y)
            {
                var pixel = PreviewStyleGetPixel(dc, x, y);
                InteractionAssert(pixel != uint.MaxValue, "Desktop preview pixel is unavailable.");
                return Color.FromRgb((byte)pixel, (byte)(pixel >> 8), (byte)(pixel >> 16));
            }
            try
            {
                var image = Pixel(middleX, middleY);
                var backdrop = background ?? Pixel(left - 4, top + 4);
                if (livePhone)
                {
                    var liveSession = sessions.Get(vm.SelectedDevice!.Udid)?.Handle;
                    InteractionAssert(liveSession is not null && vm.IsCapturing &&
                        core.GetDeviceSessionStatus(liveSession).VideoFrames > 0,
                        stage + ": the iPhone 12 mini is no longer providing video.");
                    InteractionAssert(Distance(image, backdrop) > 8 ||
                        Distance(Pixel(middleX, top + (bottom - top) / 4), backdrop) > 8,
                        stage + ": phone content is indistinguishable from the window background.");
                }
                else
                    InteractionAssert(image.R is > 145 and < 225 && Math.Abs(image.R - image.B) < 3,
                        $"{stage}: synthetic video did not reach the main preview, center is {image}.");
                foreach (var (x, y, dx, dy) in new[] { (left, top, 1, 1), (right, top, -1, 1),
                    (left, bottom, 1, -1), (right, bottom, -1, -1) })
                {
                    var outside = Pixel(x + dx * 2, y + dy * 2);
                    if (square)
                    {
                        InteractionAssert(Distance(outside, image) <= 8, stage + ": video does not reach the square corner.");
                        continue;
                    }
                    InteractionAssert(Distance(outside, backdrop) <= 8,
                        $"{stage}: corner ({x},{y}) exposes {outside}, expected {backdrop}; " +
                        $"native={bounds.Left},{bounds.Top}..{bounds.Right},{bounds.Bottom}; fitted={picture}; " +
                        $"host DPI={WorkspaceGetDpi(host.WindowHandle)}.");
                    if (profile is not { } shape) continue;
                    var radius = shape.RadiusRatio * Math.Min(picture.Width, picture.Height);
                    // Sample on both sides of the fitted contour.
                    var exterior = (int)Math.Floor(radius * .18);
                    var interior = (int)Math.Ceiling(radius * .34);
                    var outsideCurve = Pixel(x + dx * exterior, y + dy * exterior);
                    var insideCurve = Pixel(x + dx * interior, y + dy * interior);
                    var insideMatches = livePhone
                        ? background is null || background == Colors.Black || Distance(insideCurve, backdrop) > 8
                        : Distance(insideCurve, image) <= 8;
                    if (Distance(outsideCurve, backdrop) > 8 || !insideMatches)
                    {
                        var diagonal = string.Join(", ", Enumerable.Range(0, 9)
                            .Select(index => $"{index * 16}={Pixel(x + dx * index * 16, y + dy * index * 16)}"));
                        var region = InteractionCreateRectRgn(0, 0, 0, 0);
                        var regionType = InteractionGetWindowRgn(host.WindowHandle, region);
                        InteractionGetRgnBox(region, out var regionBounds);
                        InteractionDeleteObject(region);
                        PreviewStyleGetClientRect(host.WindowHandle, out var clientSize);
                        throw new InvalidOperationException(
                            $"{stage}: corner ({x},{y}) does not follow the device curve. " +
                            $"Outside={outsideCurve}, inside={insideCurve}, backdrop={backdrop}, image={image}, radius={radius:F2}, " +
                            $"window={bounds.Left},{bounds.Top}..{bounds.Right},{bounds.Bottom}, picture={picture}, " +
                            $"client={clientSize.Right}x{clientSize.Bottom}, region={regionType}:{regionBounds.Left},{regionBounds.Top}..{regionBounds.Right},{regionBounds.Bottom}. " +
                            $"Diagonal: {diagonal}.");
                    }
                    if (livePhone) continue;
                    var blended = false;
                    for (var sample = 2; sample <= 6 && !blended; sample++)
                    {
                        var row = (int)Math.Round(radius * sample / 10);
                        var vertical = 1 - (row + .5) / radius;
                        var column = (int)Math.Floor(radius * (1 - Math.Pow(
                            1 - Math.Pow(vertical, shape.CurveExponent), 1 / shape.CurveExponent)));
                        for (var offset = -2; offset <= 2 && !blended; offset++)
                        {
                            var pixel = Pixel(x + dx * (column + offset), y + dy * row);
                            blended = Distance(pixel, backdrop) > 10 && Distance(pixel, image) > 10;
                        }
                    }
                    InteractionAssert(blended, $"{stage}: corner ({x},{y}) has no antialiased pixels.");
                }
                if (top > bounds.Top + 4)
                    InteractionAssert(Distance(Pixel(middleX, top - 3), backdrop) <= 8,
                        stage + ": a separate rectangular fill is visible above the picture.");
            }
            finally { PreviewStyleReleaseDC(0, dc); }
            Console.WriteLine($"PASS {stage}: native picture and current WPF background meet at all four corners.");
        }

        void VerifyMainPreviewAppearance()
        {
            KeyboardCall(main, "SetSettingsPanelVisible", true);
            FinishWorkspaceReveal(main);
            ((RadioButton)main.FindName("ApplicationSettingsTab")).IsChecked = true;
            Settle();
            var section = (ContentControl)main.FindName("MainPreviewAppearanceSection");
            T FindSetting<T>(string id) where T : FrameworkElement =>
                FindVisualDescendants<T>(section).Single(element =>
                    System.Windows.Automation.AutomationProperties.GetAutomationId(element) == id);
            var corners = FindSetting<CheckBox>("MainPreviewRoundedCornersCheckBox");
            var frameToggle = FindSetting<CheckBox>("MainPreviewPhoneFrameCheckBox");
            var hint = FindSetting<TextBlock>("MainPreviewPhoneFrameHint");
            var enabledHint = hint.Text;
            var baseline = System.Text.Json.JsonSerializer.Deserialize<UpdateSettings>("{}")!;
            InteractionAssert(baseline.MainPreviewRoundedCorners && !baseline.MainPreviewPhoneFrame,
                "An existing settings file must retain rounded previews with the frame off.");
            int FrameRotation() => NativeCore.GetDeviceWindowPhoneFrameOrientation(vm.CurrentSessionHandle, host.WindowHandle);

            InteractionAssert(corners.IsVisible && frameToggle.IsVisible && frameToggle.IsEnabled &&
                main.FindName("MainPreviewPhoneFrameButton") is null, "Both settings must be discoverable on the application page.");
            frameToggle.IsChecked = true;
            Settle();
            for (var attempt = 0; vm.MainPreviewPhoneFrame && FrameRotation() < 0 && attempt < 10; ++attempt)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(vm.MainPreviewPhoneFrame && FrameRotation() == 0 && frameToggle.IsChecked == true,
                "The iPhone 12 mini frame was not enabled and presented.");
            var frame = DeviceFrameCatalog.Find("iPhone13,1")!;
            const int surfaceWidth = 400, surfaceHeight = 800;
            var screen = PhoneFrameGeometry.ScreenBounds(frame, FrameRotation(), surfaceWidth, surfaceHeight);
            PreviewPointerEventArgs InputAt(int x, int y) =>
                new(PreviewPointerKind.ButtonDown, x, y, 1, 0, surfaceWidth, surfaceHeight, 0, 0, 0, 0, host.WindowHandle);
            var centerInput = KeyboardCall(main, "MapMainPreviewFramePointer",
                InputAt((int)Math.Round(screen.X + screen.Width / 2), (int)Math.Round(screen.Y + screen.Height / 2)));
            InteractionAssert(centerInput is PreviewPointerEventArgs centerPoint &&
                Math.Abs(centerPoint.X - width / 2d) <= 3 && Math.Abs(centerPoint.Y - height / 2d) <= 3 &&
                KeyboardCall(main, "MapMainPreviewFramePointer", InputAt(0, surfaceHeight / 2)) is null,
                "The frame did not map the live screen center or reject a bezel press.");

            SetKeyboardField(vm, "_selectedDevice", DeviceViewModel.FromNative(new NativeDeviceInfo
                { Udid = udid, Name = device.Name, ProductType = "iPhone99,1", State = ConnectionState.Ready },
                DeviceAppearance.Unknown));
            KeyboardCall(main, "SynchronizeMainPreviewHost");
            Settle();
            InteractionAssert(frameToggle.IsEnabled && frameToggle.IsChecked == true &&
                FrameRotation() == -1 && vm.MainPreviewPhoneFrame,
                "An unsupported model must hide the frame without clearing the saved choice.");
            SetKeyboardField(vm, "_selectedDevice", device);
            KeyboardCall(main, "SynchronizeMainPreviewHost");
            Settle();
            InteractionAssert(frameToggle.IsEnabled && FrameRotation() == 0,
                "A supported model must restore the user's saved frame choice.");

            KeyboardCall(main, "ToggleFullScreen");
            Settle();
            InteractionAssert(vm.MainPreviewPhoneFrame && FrameRotation() == -1 && !frameToggle.IsVisible,
                "Fullscreen must temporarily hide the frame.");
            KeyboardCall(main, "ToggleFullScreen");
            Settle();
            InteractionAssert(FrameRotation() == 0 && frameToggle.IsChecked == true,
                "Leaving fullscreen must restore the frame.");

            corners.IsChecked = false;
            Settle();
            var saved = System.Text.Json.JsonSerializer.Deserialize<UpdateSettings>(
                System.Text.Json.JsonSerializer.Serialize(app.UpdateSettings.Clone()))!;
            InteractionAssert(!vm.MainPreviewRoundedCorners && !vm.MainPreviewPhoneFrame &&
                !saved.MainPreviewRoundedCorners && !saved.MainPreviewPhoneFrame &&
                FrameRotation() == -1 && !frameToggle.IsEnabled && frameToggle.IsChecked == false &&
                !string.IsNullOrWhiteSpace(hint.Text) && hint.Text != enabledHint && previewStage.CornerRadius.TopLeft == 0,
                "Turning off rounded corners must clear the frame preference and disable the toggle.");
            VerifyCompletePicture("square complete", blue, square: true);
            corners.IsChecked = true;
            Settle();
            InteractionAssert(vm.MainPreviewRoundedCorners && frameToggle.IsEnabled && !vm.MainPreviewPhoneFrame &&
                FrameRotation() == -1 && previewStage.CornerRadius.TopLeft > 0,
                "Re-enabling rounded corners should let the user choose whether to restore the phone frame.");
            KeyboardCall(main, "SetSettingsPanelVisible", false);
            FinishWorkspaceReveal(main);
            Console.WriteLine("PASS main preview appearance: device artwork, framed pointer mapping, fullscreen restoration, square corners and saved prerequisite.");
        }

        try
        {
            main.Topmost = true;
            main.Left = SystemParameters.WorkArea.Left + 35;
            main.Top = SystemParameters.WorkArea.Top + 25;
            main.Width = 1100;
            main.Height = 780;
            if (highDpi)
            {
                var displays = new List<WorkspaceDisplayInfo>();
                WorkspaceMonitorCallback collect = (nint monitor, nint dc, ref WorkspaceNativeRect rect, nint data) =>
                {
                    var info = new WorkspaceDisplayInfo { Size = (uint)Marshal.SizeOf<WorkspaceDisplayInfo>() };
                    if (WorkspaceGetMonitorInfo(monitor, ref info)) displays.Add(info);
                    return true;
                };
                _ = WorkspaceEnumMonitors(0, 0, collect, 0);
                var area = displays.OrderBy(d => d.Monitor.Left).First().WorkArea;
                _ = WorkspaceSetWindowPos(hwnd, 0, area.Left + 48, area.Top + 48,
                    Math.Min(1300, area.Right - area.Left - 96), Math.Min(1380, area.Bottom - area.Top - 96), 0x14);
                Settle();
                InteractionAssert(WorkspaceGetDpi(hwnd) > 96, "High-DPI composition requires a scaled display.");
            }
            Console.WriteLine($"WPF preview fixture DPI={WorkspaceGetDpi(hwnd)}.");
            if (!livePhone) VerifyCaptureButtonColors();
            center.Background = new SolidColorBrush(green);
            var panelType = KeyboardField(main, "_leftWorkspacePanel").GetType();
            KeyboardCall(main, "SetLeftWorkspacePanel", Enum.Parse(panelType, "Devices"));
            FinishWorkspaceReveal(main);
            if (livePhone)
            {
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (!vm.Devices.Any(d => !d.IsWireless && d.ProductType == "iPhone13,1") &&
                    timeout.Elapsed < TimeSpan.FromSeconds(50))
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                vm.SelectedDevice = vm.Devices.Single(d => !d.IsWireless && d.ProductType == "iPhone13,1");
                vm.SelectedUsbProjectionMode = vm.UsbProjectionModes.Single(option => option.Mode == UsbProjectionMode.Demo);
                InteractionAssert(vm.StartCommand.CanExecute(null), "The connected iPhone 12 mini is not ready for USB projection.");
                vm.StartCommand.Execute(null);
                timeout.Restart();
                var observedWaiting = false;
                while ((!vm.IsCapturing || !vm.HasMirroringVideo) &&
                    timeout.Elapsed < TimeSpan.FromSeconds(55))
                {
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    observedWaiting |= vm.IsCapturing && !vm.HasMirroringVideo && !host.IsVisible &&
                        ((FrameworkElement)main.FindName("WaitingForPictureState")).IsVisible;
                }
                InteractionAssert(vm.IsCapturing && vm.HasMirroringVideo && vm.SourceVideoWidth > 0 && vm.CurrentSessionHandle != 0,
                    "The real iPhone 12 mini did not produce a projection frame.");
                InteractionAssert(observedWaiting, "The live test did not observe the waiting status before the first frame.");
                Console.WriteLine($"LIVE USB iPhone 12 mini (iPhone13,1), Demo mode, {vm.SourceVideoWidth}x{vm.SourceVideoHeight}.");
            }
            else
            {
                SetKeyboardField(vm, "_selectedDevice", device);
                sessions.Set(new DeviceCaptureState { Udid = udid, Handle = session });
                KeyboardCall(vm, "OnPropertyChanged", nameof(MainViewModel.SelectedDevice));
                KeyboardCall(vm, "NotifySelectedDeviceProperties");
                NativeCore.SelectPreviewSession(session);
                KeyboardCall(vm, "ApplyCaptureStatus", new NativeCaptureStatus { State = CaptureState.Handshaking });
                VerifyWaitingForPicture();
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight;
                VerifyWaitingForPicture();
                KeyboardCall(main, "ToggleFullScreen");
                VerifyWaitingForPicture();
                KeyboardCall(main, "ToggleFullScreen");
                vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete;
                center.Background = new SolidColorBrush(green);
                core.PushExternalFrame(session!, 1, width, height, nv12);
                var status = core.GetDeviceSessionStatus(session!);
                InteractionAssert(status.Width == width && status.Height == height && vm.CurrentSessionHandle == session!.RawHandle,
                    $"Synthetic source geometry did not reach the main session: {status.Width}x{status.Height}, handle={vm.CurrentSessionHandle}.");
                KeyboardCall(vm, "ApplyCaptureStatus", status);
                Console.WriteLine("PASS: active session shows waiting status before the first frame in Complete, Lightweight and fullscreen.");
            }
            Settle();

            var profile = DeviceCornerProfileResolver.Resolve("iPhone13,1");
            PreviewStyleGetWindowRect(hwnd, out var normal);
            _ = PerfSetWindowPos(hwnd, 0, 0, 0, 400, normal.Bottom - normal.Top, 0x0016);
            VerifyStageLayout();
            VerifyCompletePicture("minimum complete", green, profile);
            KeyboardCall(main, "MaximizeWindow", (object?)null);
            KeyboardCall(main, "SetSettingsPanelVisible", true);
            FinishWorkspaceReveal(main);
            InteractionAssert(settingsCard.IsVisible, "The maximized complete test requires the settings card.");
            center.Background = new SolidColorBrush(blue);
            VerifyStageLayout();
            VerifyCompletePicture("maximized complete", blue, profile);
            KeyboardCall(main, "SetSettingsPanelVisible", false);
            KeyboardCall(main, "SetLeftWorkspacePanel", Enum.Parse(panelType, "Devices"));
            FinishWorkspaceReveal(main);
            KeyboardCall(main, "RestoreWindowFromMaximized");
            if (!livePhone) VerifyMainPreviewAppearance();

            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight;
            KeyboardCall(main, "SetLeftWorkspacePanel", Enum.Parse(panelType, "None"));
            KeyboardCall(main, "SetSettingsPanelVisible", true);
            FinishWorkspaceReveal(main);
            center.Background = new SolidColorBrush(green);
            VerifyPicture("lightweight with settings", green, profile);
            InteractionAssert(!previewStage.IsVisible && !((FrameworkElement)main.FindName("EnvironmentPanel")).IsVisible &&
                !((FrameworkElement)main.FindName("StatsPanel")).IsVisible,
                "Lightweight mode left the preview bars visible.");
            center.Background = new SolidColorBrush(blue);
            VerifyPicture("lightweight after WPF repaint", blue, profile);
            KeyboardCall(main, "MaximizeWindow", (object?)null);
            VerifyPicture("maximized lightweight with settings", blue, profile);
            center.ClearValue(Panel.BackgroundProperty);
            VerifyPicture("maximized lightweight with the application theme", null, profile);
            KeyboardCall(main, "ToggleFullScreen");
            VerifyPicture("video fullscreen", Colors.Black, profile);
            KeyboardCall(main, "ToggleFullScreen");
            center.Background = new SolidColorBrush(green);
            VerifyPicture("lightweight restored to maximized", green, profile);
            KeyboardCall(main, "RestoreWindowFromMaximized");
            VerifyPicture("lightweight restored to normal", green, profile);
            if (livePhone) return 0;
            SetKeyboardField(vm, "_selectedDevice", DeviceViewModel.FromNative(new NativeDeviceInfo
            {
                Udid = udid, Name = device.Name, ProductType = "iPhone18,3", State = ConnectionState.Ready,
            }, DeviceAppearance.Unknown));
            KeyboardCall(vm, "NotifySelectedDeviceProperties");
            KeyboardCall(main, "SynchronizeMainPreviewHost");
            var iPhone17 = DeviceCornerProfileResolver.Resolve("iPhone18,3");
            VerifyPicture("lightweight after switching device profile", green, iPhone17);
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete;
            VerifyCompletePicture("complete after switching device profile", green, iPhone17);
            SetKeyboardField(vm, "_sourceVideoWidth", height);
            SetKeyboardField(vm, "_sourceVideoHeight", width);
            core.PushExternalFrame(session!, 1, height, width, nv12);
            KeyboardCall(main, "ApplyApplicationDisplayMode");
            VerifyStageLayout();
            VerifyCompletePicture("complete landscape", green, iPhone17);
            vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight;
            VerifyPicture("lightweight landscape", green,
                DeviceCornerProfileResolver.Resolve("iPhone18,3"));
            KeyboardCall(vm, "ApplyCaptureStatus", new NativeCaptureStatus { State = CaptureState.Stopped });
            Settle();
            InteractionAssert(!host.IsVisible && ((FrameworkElement)main.FindName("ReadyToMirrorState")).IsVisible &&
                !((FrameworkElement)main.FindName("WaitingForPictureState")).IsVisible,
                "Stopping capture must restore the ready card without the waiting indicator.");
            KeyboardCall(vm, "ApplyCaptureStatus", new NativeCaptureStatus { State = CaptureState.Handshaking });
            VerifyWaitingForPicture();
            core.PushExternalFrame(session!, 1, height, width, nv12);
            KeyboardCall(vm, "ApplyCaptureStatus", core.GetDeviceSessionStatus(session!));
            VerifyPicture("preview reattached after stopping", green,
                DeviceCornerProfileResolver.Resolve("iPhone18,3"));
            return 0;
        }
        finally
        {
            if (!livePhone)
            {
                KeyboardCall(vm, "set_IsCapturing", false);
                SetKeyboardField(vm, "_selectedDevice", null);
                NativeCore.SelectPreviewSession(null);
                sessions.Remove(udid);
            }
            // Live USB shutdown waits for the phone to return to its normal USB configuration.
            CloseWorkspaceTestWindow(main, livePhone ? TimeSpan.FromSeconds(60) : null);
            if (livePhone) Console.WriteLine("LIVE CLEANUP: the real USB projection and test window were closed.");
            app.Shutdown();
            _ = WorkspaceSetDpiAwareness(dpiContext);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CompositionPoint(int X, int Y);

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint CompositionWindowFromPoint(CompositionPoint point);

    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern nint CompositionGetRootWindow(nint window, uint flags);
}
