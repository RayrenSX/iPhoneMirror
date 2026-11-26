using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPreviewToolsTests()
    {
        const string firstFile = @"C:\Pictures\中文照片.png";
        const string secondFile = @"C:\Videos\test clip.mp4";
        InteractionAssert(NativeFileDrop.Read(CreateShellDrop(firstFile, secondFile)).SequenceEqual([firstFile, secondFile]),
            "Native drop lost a file or altered a Unicode path.");

        var unicodeText = "北京e\u0301";
        var unicode = new ScreenTextPage([(unicodeText,
            new[] { (unicodeText, new Rect(0, 0, 90, 20)) })]).Words.Single();
        InteractionAssert(unicode.Boundaries.SequenceEqual([0, 1, 2, 4]) &&
            unicode.Highlight(2, 4).Width == 30,
            "OCR selection must keep combining characters together while selecting CJK text by character.");
        var page = ReadScreenText("GOOD DAY", "HELLO WORLD");
        InteractionAssert(page.Text.Split(Environment.NewLine).Length == 2 && page.Words.Count >= 4 &&
            page.Words.All(word => new Rect(0, 0, 800, 312).Contains(word.Bounds)),
            "Windows OCR must preserve text lines and word geometry for a padded preview frame.");
        Console.WriteLine("PASS preview tools: Unicode shell drop, Windows OCR, selectable text geometry.");
        return 0;
    }

    private static ScreenTextPage ReadScreenText(params string[] lines)
    {
        const int width = 800, height = 312, stride = width * 4 + 32;
        var picture = new DrawingVisual();
        using (var dc = picture.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (var i = 0; i < lines.Length; i++)
                dc.DrawText(new FormattedText(lines[i], CultureInfo.GetCultureInfo("en-US"),
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 68, Brushes.Black, 1), new Point(40, 30 + i * 148));
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(picture);
        var pixels = new byte[stride * height];
        image.CopyPixels(pixels, stride, 0);
        return Task.Run(() => ScreenTextRecognition.ReadPageAsync(new VideoFrame(width, height, stride, 0, pixels)))
            .GetAwaiter().GetResult();
    }

    private static nint CreateShellDrop(params string[] files)
    {
        var paths = Encoding.Unicode.GetBytes(string.Join('\0', files) + "\0\0");
        var packet = new byte[20 + paths.Length];
        var drop = GlobalAlloc(0, (nuint)packet.Length);
        if (drop == 0) throw new OutOfMemoryException();
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), 1); // DROPFILES.fWide
        paths.CopyTo(packet, 20);
        Marshal.Copy(packet, 0, drop, packet.Length);
        return drop;
    }

    private static int RunPreviewToolsReview(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "review-settings.json")));
        typeof(LocalizationService).GetMethod("ApplyLanguage",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, ["zh-CN", false, true]);
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var main = CreateWorkspaceTestWindow(app);
        try
        {
            var toolbar = (FrameworkElement)main.FindName("EnvironmentPanel");
            foreach (var name in new[] { "QuickCopyScreenTextButton", "QuickSendPreviewFilesButton" })
                ((Button)main.FindName(name)).SetCurrentValue(UIElement.IsEnabledProperty, true);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
            main.UpdateLayout();
            var mainPath = Path.Combine(output, "01-preview-entry.png");
            SaveWindowRender(main, mainPath);
            var mainRender = new BitmapImage();
            mainRender.BeginInit();
            mainRender.CacheOption = BitmapCacheOption.OnLoad;
            mainRender.UriSource = new Uri(Path.GetFullPath(mainPath));
            mainRender.EndInit();
            var bounds = toolbar.TransformToAncestor(main).TransformBounds(new Rect(toolbar.RenderSize));
            var cropArea = new Int32Rect((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height);
            SavePreviewBitmap(new CroppedBitmap(mainRender, cropArea), mainPath);

            var mainHandle = new WindowInteropHelper(main).Handle;
            foreach (var name in new[] { "QuickCopyScreenTextButton", "SidebarCopyScreenTextButton" })
            {
                var button = (Button)main.FindName(name);
                InteractionAssert(button.ContextMenu is null &&
                    System.Windows.Automation.AutomationProperties.GetName(button) == "复制画面文字",
                    "The main OCR button must expose only the copy-screen-text action.");
            }

            var errorText = new object[] { LocalizationService.Get("ScreenshotNoFrame") };
            KeyboardCall(main, "ShowPreviewFeedback", mainHandle, "ScreenTextSelectionFailed", null, null, errorText);
            var feedback = (ScreenTextFeedbackWindow)KeyboardField(main, "_screenTextFeedback");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(130));
            var panel = (FrameworkElement)main.FindName("PreviewPanel");
            var topLeft = panel.PointToScreen(new Point());
            var bottomRight = panel.PointToScreen(new Point(panel.ActualWidth, panel.ActualHeight));
            VerifyFeedbackBounds(feedback, topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
            SaveWindowRender(feedback, Path.Combine(output, "05-screen-text-error.png"));
            var feedbackPath = Path.Combine(output, "06-screen-text-error-over-preview.png");
            SaveWindowRender(main, feedbackPath);
            CompositeFeedbackScreenshot(main, feedback, feedbackPath);
            feedback.Close();

            var copyRequests = 0;
            string[]? dropped = null;
            InteractionAssert(NativePreviewWindow.TryCreateAndShowForContent(
                new Border { Background = Brushes.LightGray }, 390, 844, "iPhoneMirror · iPhone 13",
                () => true, _ => { }, () => 1, () => { }, () => { }, out var preview), "Cannot create preview HWND.");
            using (preview!)
            {
                preview!.SetPreviewTools(() => copyRequests++, files => dropped = files);
                _ = SendPreviewDrop(preview.Handle, NativeFileDrop.Message, CreateShellDrop(@"C:\Demo\演示文稿.pdf"), 0);
                InteractionAssert(dropped?.SequenceEqual([@"C:\Demo\演示文稿.pdf"]) == true,
                    "The independent HWND did not deliver its shell file drop.");
                KeyboardCall(preview, "ShowContextMenu");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                var menu = (ContextMenu)KeyboardField(preview, "_contextMenu");
                SavePreviewVisual(menu, Path.Combine(output, "02-independent-menu.png"));
                var commands = menu.Items.OfType<MenuItem>().ToArray();
                InteractionAssert(commands.Count(item => Equals(item.Header, "复制画面文字 (Alt+C)")) == 1 &&
                    !commands.Any(item => item.Header is string label &&
                        (label.Contains("Alt+Shift+C", StringComparison.Ordinal) || label.Contains("Alt+R", StringComparison.Ordinal))),
                    "The independent menu must show a single OCR action.");
                InteractionAssert(!commands.Any(item => Equals(item.Header, LocalizationService.Get("PreviewFilesPick"))),
                    "The independent preview menu still exposes the file picker.");
                commands.Single(item => Equals(item.Header, LocalizationService.Get("CopyScreenTextShortcut")))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                InteractionAssert(copyRequests == 1, "The independent menu did not invoke the OCR action.");
                menu.IsOpen = false;
                KeyboardCall(main, "ShowPreviewFeedback", preview.Handle, "ScreenTextSelectionFailed", null, null, errorText);
                var independentFeedback = (ScreenTextFeedbackWindow)KeyboardField(main, "_screenTextFeedback");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(130));
                InteractionAssert(PreviewStyleGetWindowRect(preview.Handle, out var boundsBefore),
                    "Independent preview bounds are unavailable.");
                VerifyFeedbackBounds(independentFeedback, boundsBefore.Left, boundsBefore.Top, boundsBefore.Right, boundsBefore.Bottom);
                _ = PreviewStyleSetWindowPos(preview.Handle, 0, boundsBefore.Left + 32, boundsBefore.Top + 24, 0, 0, 0x15);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(160));
                InteractionAssert(PreviewStyleGetWindowRect(preview.Handle, out var boundsAfter) && boundsAfter.Left != boundsBefore.Left,
                    "Independent preview did not move.");
                VerifyFeedbackBounds(independentFeedback, boundsAfter.Left, boundsAfter.Top, boundsAfter.Right, boundsAfter.Bottom);
                independentFeedback.Close();
            }
            VerifyScreenTextSelection(main, output);
            TestPreviewTransferDialog(main, output);
        }
        finally { CloseWorkspaceTestWindow(main); }
        Console.WriteLine("PASS preview tools: toolbar and independent menu, OCR drag selection, copy and dismissal, file transfer.");
        return 0;
    }

    private static int RunPreviewFilesControlTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "settings.json")));
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        try { VerifyPreviewFilesRequireControl(main, output); }
        finally { CloseWorkspaceTestWindow(main); app.Shutdown(); }
        return 0;
    }

    private static void VerifyPreviewFilesRequireControl(MainWindow main, string output)
    {
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
        var mirrors = (MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        var windows = (Dictionary<string, NativePreviewWindow>)KeyboardField(mirrors, "_windows");
        var guidance = (HashSet<string>)KeyboardField(main, "_deviceProfileGuidanceShown");
        var originalDevice = vm.SelectedDevice;
        var originalResolver = KeyboardField(vm, "_identityResolver");
        var bindings = new DeviceBindingManager(Path.Combine(output, $"files-bindings-{Guid.NewGuid():N}.json"));
        var resolver = new DeviceIdentityResolver(bindings);
        SetKeyboardField(vm, "_identityResolver", resolver);
        var wired = DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = "preview-files-wired", Name = "File transfer iPhone", State = ConnectionState.Ready });
        var wireless = DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = "airplay://02:00:00:00:00:91", Name = "File transfer AirPlay", State = ConnectionState.Ready });
        var profile = bindings.CreateProfileFromIdentity(wired.Name, DeviceIdentityType.Wired, wired.Udid, null).Profile!;
        InteractionAssert(bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, wireless.Udid, wireless.Name, null, true).Success,
            "The transfer fixture needs a bound AirPlay source.");
        try
        {
            foreach (var device in new[] { wired, wireless })
            {
                guidance.Add(device.Udid);
                vm.Devices.Add(device);
                using var session = new NativeSessionHandle(device.IsWireless ? 9002UL : 9001UL, ownsHandle: false);
                sessions.Set(new DeviceCaptureState { Udid = device.Udid, Handle = session });
                SetKeyboardField(vm, "_selectedDevice", device);
                var files = new[] { @"C:\Demo\must-not-send.pdf" };
                var target = vm.GetPreviewSharedFilesTarget(device);
                void RejectRequest(string? file)
                {
                    try
                    {
                        vm.PreviewSharedFilesAsync(device, target, file is null ? null : "org.example.vlc",
                            file, () => true, default).GetAwaiter().GetResult();
                        throw new InvalidOperationException("A preview-only device accepted a file request.");
                    }
                    catch (AutomationException error)
                    {
                        InteractionAssert(error.Code == "PREVIEW_FILES_CONTROL_REQUIRED",
                            "The file request must ask for wired or wireless reverse control.");
                    }
                }
                try
                {
                    RejectRequest(null);
                    RejectRequest(files[0]);
                    SetKeyboardField(vm, "_bluetoothControlEnabled", true);
                    SetKeyboardField(vm, "_bluetoothControlConnected", true);
                    SetKeyboardField(vm, "_bluetoothControlDeviceUdid", device.Udid);
                    try { RejectRequest(null); RejectRequest(files[0]); }
                    finally
                    {
                        SetKeyboardField(vm, "_bluetoothControlEnabled", false);
                        SetKeyboardField(vm, "_bluetoothControlConnected", false);
                        SetKeyboardField(vm, "_bluetoothControlDeviceUdid", null!);
                    }
                    KeyboardCall(main, "OnMainNativePreviewFilesDropped", (object)files);
                    var feedback = (ScreenTextFeedbackWindow)KeyboardField(main, "_screenTextFeedback");
                    InteractionAssert(feedback.IsVisible && vm.LogText.Contains(LocalizationService.Get("PreviewFilesControlRequired")) &&
                        !(bool)KeyboardField(main, "_previewFileDropOpen"),
                        "A main-preview drop must show and log the reverse-control error without opening a transfer.");
                    SaveWindowRender(feedback, Path.Combine(output, device.IsWireless ? "12-airplay-control-required.png" : "11-wired-control-required.png"));
                    feedback.Close();

                    InteractionAssert(NativePreviewWindow.TryCreateAndShowForContent(
                        new Border { Background = Brushes.LightGray }, 390, 844, device.Name,
                        () => true, _ => { }, () => 1, () => { }, () => { }, out var preview),
                        "Cannot create the independent file-drop preview.");
                    using (preview!)
                    {
                        windows.Add(device.Udid, preview!);
                        try
                        {
                            preview!.SetPreviewTools(() => { }, paths =>
                                KeyboardCall(main, "QueuePreviewFileDrop", device, preview.Handle, paths));
                            _ = SendPreviewDrop(preview.Handle, NativeFileDrop.Message, CreateShellDrop(files), 0);
                            feedback = (ScreenTextFeedbackWindow)KeyboardField(main, "_screenTextFeedback");
                            InteractionAssert(feedback.IsVisible && !(bool)KeyboardField(main, "_previewFileDropOpen"),
                                "An independent-preview drop must show the reverse-control error.");
                            feedback.Close();
                        }
                        finally { windows.Remove(device.Udid); }
                    }

                    var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
                    using var bridge = new ExplorerReadyBridge(control);
                    control.Binding = resolver.ResolveControlBinding(device,
                        device.IsWireless ? ReverseControlMode.Wireless : ReverseControlMode.Usb);
                    if (device.IsWireless)
                    {
                        control.WiredBridge = null;
                        control.WiredEnabled = control.WiredConnected = false;
                        control.WirelessBridge = bridge.Host;
                        control.WirelessEnabled = control.WirelessConnected = true;
                    }
                    try
                    {
                        InteractionAssert(vm.IsDeviceHubReady(device), "A ready reverse-control route must allow file transfers.");
                        var installer = vm.CapturePreviewIpaInstaller(device, () => true);
                        InteractionAssert(DeviceViewModel.UdidEquals(installer.Udid, target), "IPA installation captured the wrong device.");
                        var gate = (SemaphoreSlim)KeyboardField(bridge.Inner, "_sendLock");
                        using var cancellation = new CancellationTokenSource();
                        Task<JsonElement> request;
                        gate.Wait();
                        try
                        {
                            request = vm.PreviewSharedFilesAsync(device, target, null, null, () => true, cancellation.Token);
                            InteractionAssert(!request.IsCompleted, "A ready file request must reach the existing control bridge.");
                            cancellation.Cancel();
                        }
                        finally { gate.Release(); }
                        try { WaitReviewTask(request); throw new InvalidOperationException("The request ignored cancellation."); }
                        catch (OperationCanceledException) { }
                        control.WiredEnabled = control.WirelessEnabled = false;
                        RejectRequest(files[0]);
                        try
                        {
                            installer.Install(@"C:\Demo\must-not-install.ipa", default).GetAwaiter().GetResult();
                            throw new InvalidOperationException("A disabled reverse-control route accepted an IPA installation.");
                        }
                        catch (AutomationException error)
                        {
                            InteractionAssert(error.Code == "CONTROL_NOT_AVAILABLE", "A stale installer must reject its captured route.");
                        }
                    }
                    finally
                    {
                        control.WirelessBridge = null;
                        control.WirelessEnabled = control.WirelessConnected = false;
                        control.Binding = null;
                    }
                }
                finally { sessions.Remove(device.Udid); vm.Devices.Remove(device); }
            }
        }
        finally
        {
            SetKeyboardField(vm, "_selectedDevice", originalDevice!);
            SetKeyboardField(vm, "_identityResolver", originalResolver);
        }
        Console.WriteLine("PASS file drops: wired and AirPlay previews require target-specific reverse control; disabled routes reject uploads.");
    }

    private static void VerifyScreenTextSelection(MainWindow main, string output)
    {
        var picture = new DrawingVisual();
        using (var dc = picture.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 24, 34)), null, new Rect(0, 0, 840, 1460));
            void Text(string text, int x, int y, double size, Brush color, bool bold = false) =>
                dc.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                        bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, color, 1),
                    new Point(x, y));
            var accent = new SolidColorBrush(Color.FromRgb(151, 200, 255));
            var muted = new SolidColorBrush(Color.FromRgb(187, 200, 220));
            Text("9:41", 52, 28, 32, Brushes.White, true);
            Text("NOTES", 54, 127, 28, accent, true);
            Text("Weekend plans", 52, 214, 78, Brushes.White, true);
            Text("Saturday, May 16", 54, 312, 37, muted);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(33, 43, 59)), null,
                new Rect(48, 418, 744, 398), 34, 34);
            Text("Morning train", 82, 454, 49, Brushes.White, true);
            Text("Meet at Central Station", 82, 552, 45, Brushes.White);
            Text("Platform 3 at 09:40", 82, 630, 45, Brushes.White);
            Text("Bring your ticket and ID", 82, 708, 41, muted);
            Text("Afternoon", 54, 916, 53, Brushes.White, true);
            Text("Lunch by the river", 54, 1008, 43, Brushes.White);
            Text("Walk through the old town", 54, 1085, 43, Brushes.White);
            Text("Back before sunset", 54, 1162, 43, muted);
        }
        var bitmap = new RenderTargetBitmap(840, 1460, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(picture);
        var pixels = new byte[840 * 1460 * 4];
        bitmap.CopyPixels(pixels, 840 * 4, 0);
        var frame = new VideoFrame(840, 1460, 840 * 4, 0, pixels);
        var rotated = MainWindow.OrientScreenTextFrame(frame, 1);
        InteractionAssert(rotated.Width == frame.Height && rotated.Height == frame.Width,
            "The selected window rotation was not applied to the OCR image.");
        var page = Task.Run(() => ScreenTextRecognition.ReadPageAsync(frame)).GetAwaiter().GetResult();
        InteractionAssert(page.Words.Count > 15 && page.Words.All(word =>
            new Rect(0, 0, 840, 1460).Contains(word.Bounds)), "WinRT OCR did not return usable source coordinates.");
        var chosen = page.Words.Where(word => word.Bounds.Top is > 535 and < 750).ToArray();
        InteractionAssert(chosen.Length >= 6, "OCR did not recognize both travel lines.");

        var originalBounds = new Rect(500, 170, 365, 634);
        string? copied = null;
        var current = true;
        var selection = new ScreenTextSelectionWindow(new WindowInteropHelper(main).Handle,
            ScreenshotService.CreateBitmap(frame),
            () => new(originalBounds, originalBounds), () => current, text => copied = text);
        var closed = false;
        selection.Closed += (_, _) => closed = true;
        try
        {
            InteractionAssert(selection.ShowForPreview(), "The OCR selection overlay did not open.");
            selection.SetPage(page);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(160));
            var surface = selection.Surface;
            AssertScreenTextPictureBounds(surface, originalBounds);
            Point Edge(ScreenTextPage.Word word, bool right) => surface.ToSurface(new Point(
                right ? word.Bounds.Right - 0.05 : word.Bounds.Left + 0.05,
                word.Bounds.Top + word.Bounds.Height / 2));
            var start = Edge(chosen[0], false);
            var end = Edge(chosen[^1], true);
            var expected = page.Text[chosen[0].Start..chosen[^1].End];
            InteractionAssert(surface.BeginSelection(start), "Dragging could not begin on recognized text.");
            surface.ExtendSelection(end);
            surface.EndSelection(end);
            InteractionAssert(surface.Selection == expected && expected.Contains(Environment.NewLine) && copied is null,
                "The OCR overlay must select across lines without copying until requested.");
            var buttons = FindVisualDescendants<Button>(selection).ToArray();
            Button Action(string id) => buttons.Single(button =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(button) == id);
            Action("ScreenTextCopySelection").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(copied == expected, "The copy action did not send only the selected characters.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(130));
            AssertScreenTextPictureBounds(surface, originalBounds);
            SaveWindowRender(selection, Path.Combine(output, "07-screen-text-selection.png"));

            _ = surface.BeginSelection(end);
            surface.EndSelection(start);
            InteractionAssert(surface.Selection == expected, "Reverse dragging altered text order.");
            var first = chosen[0];
            if (first.Boundaries.Length > 2)
            {
                var middle = surface.ToSurface(new Point(first.Bounds.Left + first.Bounds.Width * 0.65,
                    first.Bounds.Top + first.Bounds.Height / 2));
                _ = surface.BeginSelection(start);
                surface.EndSelection(middle);
                InteractionAssert(surface.Selection.Length > 0 && surface.Selection.Length < first.End - first.Start,
                    "Dragging inside one word must allow partial-text selection.");
            }
            Action("ScreenTextSelectAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(surface.Selection == page.Text, "Select all did not preserve OCR reading order.");
            var desktop = SystemParameters.WorkArea;
            var dpi = VisualTreeHelper.GetDpi(main);
            originalBounds = new Rect(desktop.X * dpi.DpiScaleX, desktop.Y * dpi.DpiScaleY,
                desktop.Width * dpi.DpiScaleX, desktop.Height * dpi.DpiScaleY);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(190));
            AssertScreenTextPictureBounds(surface, originalBounds);

            current = false;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(210));
            InteractionAssert(closed, "Changing sources must close the selection overlay.");
        }
        finally { if (!closed) selection.Close(); }
        VerifyScreenTextToolbarEntry(main, frame, output);
    }

    private static void AssertScreenTextPictureBounds(FrameworkElement surface, Rect expected)
    {
        var topLeft = surface.PointToScreen(new Point());
        var bottomRight = surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight));
        InteractionAssert(Math.Abs(topLeft.X - expected.Left) <= 1 && Math.Abs(topLeft.Y - expected.Top) <= 1 &&
            Math.Abs(bottomRight.X - expected.Right) <= 1 && Math.Abs(bottomRight.Y - expected.Bottom) <= 1,
            $"OCR picture moved or resized. Expected {expected}; found {new Rect(topLeft, bottomRight)}.");
    }

    private static void VerifyScreenTextToolbarEntry(MainWindow main, VideoFrame frame, string output)
    {
        const string udid = "selectable-screen-text-fixture";
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
        var core = (NativeCore)KeyboardField(vm, "_core");
        using var handle = core.CreateExternalSession(udid, false, 0).Handle ??
            throw new InvalidOperationException("Could not create the OCR preview fixture.");
        var device = DeviceViewModel.FromNative(new NativeDeviceInfo
        {
            Udid = udid, Name = "iPhone", ProductType = "iPhone13,1", State = ConnectionState.Ready,
        }, DeviceAppearance.Unknown);
        var state = new DeviceCaptureState { Udid = udid, Handle = handle };
        sessions.Set(state);
        try
        {
            var luma = checked((int)(frame.Width * frame.Height));
            var nv12 = new byte[luma * 3 / 2];
            nv12.AsSpan(luma).Fill(128);
            for (var i = 0; i < luma; i++)
                nv12[i] = (byte)(16 + (frame.Pixels[i * 4 + 1] * 219 + 127) / 255);
            core.PushExternalFrame(handle, 1, frame.Width, frame.Height, nv12);
            SetKeyboardField(vm, "_selectedDevice", device);
            NativeCore.SelectPreviewSession(handle);
            KeyboardCall(vm, "NotifySelectedDeviceProperties");
            KeyboardCall(vm, "ApplyCaptureStatus", core.GetDeviceSessionStatus(handle));
            main.Activate();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(180));
            ((Button)main.FindName("QuickCopyScreenTextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (KeyboardField(main, "_screenTextSelectionRequest") is not null &&
                timeout.Elapsed < TimeSpan.FromSeconds(5))
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            var overlay = KeyboardField(main, "_screenTextSelection") as ScreenTextSelectionWindow;
            InteractionAssert(overlay is not null, "The main OCR toolbar did not open an overlay for its native preview.");
            overlay!.Surface.SelectAll();
            InteractionAssert(overlay.Surface.Selection.Contains("Weekend", StringComparison.OrdinalIgnoreCase),
                "The toolbar did not recognize the source's native frame.");

            state.UpdateProtectionState(videoProtected: true, audioActive: false, 0, 0);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
            InteractionAssert(KeyboardField(main, "_screenTextSelection") is null,
                "Protected video must close an already open selection overlay.");
            state.UpdateProtectionState(videoProtected: false, audioActive: false, 0, 0);
            VerifyIndependentScreenTextBounds(main, device, output);
        }
        finally
        {
            KeyboardCall(main, "CloseScreenTextSelection");
            KeyboardCall(vm, "set_IsCapturing", false);
            SetKeyboardField(vm, "_selectedDevice", null);
            NativeCore.SelectPreviewSession(null);
            sessions.Remove(udid);
        }
    }

    private static void VerifyIndependentScreenTextBounds(MainWindow main, DeviceViewModel device, string output)
    {
        var mirrors = (MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        var opening = mirrors.ShowAsync(device);
        WaitReviewTask(opening);
        InteractionAssert(opening.Result.Success, "Could not open the independent OCR preview fixture.");
        using var preview = mirrors.FindWindow(mirrors.GetWindowHandle(device.Udid)) ??
            throw new InvalidOperationException("The independent test preview was not tracked.");
        foreach (var (withFrame, turn) in new[] { (false, 0), (true, 0), (true, 1) })
        {
            InteractionAssert(preview.SetPhoneFrame(withFrame) && (preview.ActiveDeviceFrame is not null) == withFrame,
                "Could not configure the independent preview's phone frame.");
            if (turn != 0) preview.Rotate(turn);
            preview.Activate();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(220));
            InteractionAssert(PreviewStyleGetWindowRect(preview.Handle, out var before) &&
                ScreenTextFeedbackWindow.TryReadPreviewBounds(preview.Handle, null, out _),
                "Could not get the independent window's original bounds.");
            _ = ScreenTextFeedbackWindow.TryReadPreviewBounds(preview.Handle, null, out var client);
            var artwork = preview.ActiveDeviceFrame;
            var rotation = artwork is null ? 0 : preview.DeviceFrameRotation;
            var (width, height, _) = preview.ControlGeometry;
            var expected = artwork is null
                ? PreviewCoordinateMapper.ContentRect(client.Width, client.Height, width, height)
                : PhoneFrameGeometry.ScreenBounds(artwork, rotation, (int)client.Width, (int)client.Height);
            expected.Offset(client.Left, client.Top);

            var menu = (ContextMenu)KeyboardField(preview, "_contextMenu");
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocalizationService.Get("CopyScreenTextShortcut")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (KeyboardField(main, "_screenTextSelectionRequest") is not null && timeout.Elapsed < TimeSpan.FromSeconds(5))
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            var selection = KeyboardField(main, "_screenTextSelection") as ScreenTextSelectionWindow;
            InteractionAssert(selection is not null && (selection.Surface.OpacityMask is ImageBrush) == withFrame,
                "The independent menu did not open OCR with the expected phone-screen mask.");
            try
            {
                AdvanceDispatcher(TimeSpan.FromMilliseconds(180));
                AssertScreenTextPictureBounds(selection!.Surface, expected);
                if (turn == 0 && artwork is not null)
                {
                    selection.Surface.SelectAll();
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    AssertScreenTextPictureBounds(selection.Surface, expected);
                    var page = (ScreenTextPage)KeyboardField(selection.Surface, "_page");
                    var shown = page.Words.Where(word => word.Bounds.Top is > 535 and < 690).ToArray();
                    InteractionAssert(shown.Length >= 5, "The independent OCR fixture did not find its two travel lines.");
                    var start = selection.Surface.ToSurface(new Point(shown[0].Bounds.Left + 0.05,
                        shown[0].Bounds.Top + shown[0].Bounds.Height / 2));
                    var end = selection.Surface.ToSurface(new Point(shown[^1].Bounds.Right - 0.05,
                        shown[^1].Bounds.Top + shown[^1].Bounds.Height / 2));
                    InteractionAssert(selection.Surface.BeginSelection(start), "Could not start selecting text in the framed preview.");
                    selection.Surface.EndSelection(end);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    SaveFramedScreenText(selection, artwork, rotation, client,
                        Path.Combine(output, "08-independent-screen-text.png"));
                }
                InteractionAssert(PreviewStyleGetWindowRect(preview.Handle, out var during) && before.Equals(during),
                    "Entering OCR changed the independent window’s native position or size.");
            }
            finally { selection!.Dismiss(); }
            AdvanceDispatcher(TimeSpan.FromMilliseconds(140));
            InteractionAssert(PreviewStyleGetWindowRect(preview.Handle, out var after) && before.Equals(after),
                "Exiting OCR changed the independent window’s native position or size.");
        }
    }

    private static void SaveFramedScreenText(ScreenTextSelectionWindow selection, DeviceFrameProfile frame,
        int rotation, Rect client, string path)
    {
        var outer = PhoneFrameGeometry.ArtworkBounds(frame, rotation, (int)client.Width, (int)client.Height);
        outer.Offset(client.Left, client.Top);
        var topLeft = selection.PointToScreen(new Point());
        var bottomRight = selection.PointToScreen(new Point(selection.ActualWidth, selection.ActualHeight));
        var layer = new Rect(topLeft, bottomRight);
        var canvas = Rect.Union(outer, layer);
        canvas.Inflate(24, 18);
        var dpi = VisualTreeHelper.GetDpi(selection);
        var frozenLayer = new RenderTargetBitmap((int)Math.Ceiling(layer.Width), (int)Math.Ceiling(layer.Height),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        frozenLayer.Render(selection);
        BitmapSource artwork = BitmapSource.Create(frame.FrameWidth, frame.FrameHeight, 96, 96,
            PixelFormats.Pbgra32, null, frame.Pixels, frame.FrameWidth * 4);
        if (rotation % 4 != 0) artwork = new TransformedBitmap(artwork, new RotateTransform(rotation % 4 * 90));
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(27, 30, 37)), null,
                new Rect(0, 0, canvas.Width, canvas.Height));
            outer.Offset(-canvas.Left, -canvas.Top);
            layer.Offset(-canvas.Left, -canvas.Top);
            dc.DrawImage(artwork, outer);
            dc.DrawImage(frozenLayer, layer);
        }
        var result = new RenderTargetBitmap((int)Math.Ceiling(canvas.Width), (int)Math.Ceiling(canvas.Height),
            96, 96, PixelFormats.Pbgra32);
        result.Render(drawing);
        SavePreviewBitmap(result, path);
    }

    private static void VerifyFeedbackBounds(ScreenTextFeedbackWindow feedback, double left, double top, double right, double bottom)
    {
        var handle = new WindowInteropHelper(feedback).Handle;
        InteractionAssert(PreviewStyleGetWindowRect(handle, out var actual) && actual.Right - actual.Left > 80 &&
            actual.Bottom - actual.Top > 20 && actual.Top >= top && actual.Left >= left &&
            Math.Abs((actual.Left + actual.Right) - (left + right)) < 5 &&
            bottom - actual.Bottom is >= 5 and <= 48,
            $"OCR feedback was not centered near the preview bottom. Bounds={actual.Left},{actual.Top},{actual.Right},{actual.Bottom}, preview={left},{top},{right},{bottom}.");
        InteractionAssert(PreviewStyleSendMessage(handle, 0x0084, 0, 0) == -1,
            "OCR feedback must be transparent to mouse input.");
    }

    private static void CompositeFeedbackScreenshot(Window main, Window feedback, string path)
    {
        var background = new BitmapImage();
        background.BeginInit();
        background.CacheOption = BitmapCacheOption.OnLoad;
        background.UriSource = new Uri(Path.GetFullPath(path));
        background.EndInit();
        var overlay = new RenderTargetBitmap((int)Math.Ceiling(feedback.ActualWidth),
            (int)Math.Ceiling(feedback.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        overlay.Render(feedback);
        var helper = new WindowInteropHelper(feedback);
        InteractionAssert(PreviewStyleGetWindowRect(helper.Handle, out var bounds), "Could not locate OCR feedback.");
        var fromDevice = PresentationSource.FromVisual(main)!.CompositionTarget!.TransformFromDevice;
        var position = main.PointFromScreen(new Point(bounds.Left, bounds.Top));
        var size = fromDevice.Transform(new Vector(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawImage(background, new Rect(0, 0, background.PixelWidth, background.PixelHeight));
            dc.DrawImage(overlay, new Rect(position, new Size(size.X, size.Y)));
        }
        var composed = new RenderTargetBitmap(background.PixelWidth, background.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        composed.Render(drawing);
        SavePreviewBitmap(composed, path);
    }

    private static void TestPreviewTransferDialog(Window owner, string output)
    {
        var files = new[] { @"C:\Demo\演示文稿.pdf", @"C:\Demo\片段.mp4" };
        var list = JsonSerializer.SerializeToElement(new { apps = new[] { new { name = "VLC", bundleId = "org.example.vlc" } } });
        var sent = new List<string>();
        string? blockedFile = null;
        var current = true;
        async Task<JsonElement> Request(string? bundle, string? file, CancellationToken token)
        {
            if (bundle is null) return list;
            InteractionAssert(bundle == "org.example.vlc" && file is not null, "Upload lost its destination.");
            if (file == blockedFile) await Task.Delay(Timeout.Infinite, token);
            sent.Add(file!);
            return JsonSerializer.SerializeToElement(new { status = "imported" });
        }
        PreviewFileTransferWindow Open()
        {
            var dialog = new PreviewFileTransferWindow("演示 iPhone", files, 0, () => current, Request) { Owner = owner };
            dialog.Show();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
            dialog.UpdateLayout();
            return dialog;
        }
        static void Click(Window dialog, string name) =>
            ((Button)dialog.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var ready = Open();
        try
        {
            InteractionAssert(sent.Count == 0 && ((Button)ready.FindName("SendButton")).IsEnabled,
                "A single app should preselect without starting a transfer.");
            SaveWindowRender(ready, Path.Combine(output, "03-choose-destination.png"));
            Click(ready, "SendButton");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
            InteractionAssert(sent.SequenceEqual(files), "The confirmed files were not sent in order.");
            SaveWindowRender(ready, Path.Combine(output, "04-transfer-complete.png"));
        }
        finally { ready.Close(); }
        blockedFile = files[1];
        var cancelled = Open();
        try
        {
            Click(cancelled, "SendButton");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(160));
            var progress = (ProgressBar)cancelled.FindName("Progress");
            InteractionAssert(progress.IsVisible && !progress.IsIndeterminate && progress.Value == 1 && progress.Maximum == 2 &&
                ((TextBlock)cancelled.FindName("CurrentFile")).Text == Path.GetFileName(files[1]) &&
                ((TextBlock)cancelled.FindName("TransferCount")).Text == LocalizationService.Format("PreviewFilesTransferCount", 1, 2),
                "The progress display lost the completed-file count or the active file name.");
            SaveWindowRender(cancelled, Path.Combine(output, "09-transfer-sending.png"));
            Click(cancelled, "CloseButton");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            InteractionAssert(sent.Count == 3 && progress.Value == 1 && ((TextBlock)cancelled.FindName("StatusText")).Text ==
                LocalizationService.Format("PreviewFilesCancelled", 1, 2), "Cancel failed to retain and report the partial upload.");
        }
        finally { cancelled.Close(); }
        var changed = Open();
        try
        {
            current = false;
            Click(changed, "SendButton");
            InteractionAssert(sent.Count == 3 && ((TextBlock)changed.FindName("StatusText")).Text.Contains(
                LocalizationService.Get("PreviewFilesConnectionUnavailable")), "A changed target accepted the confirmation.");
        }
        finally { changed.Close(); }
    }

    private static void SavePreviewVisual(FrameworkElement visual, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        SavePreviewBitmap(bitmap, path);
    }

    private static void SavePreviewBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendPreviewDrop(nint window, int message, nint wParam, nint lParam);
}
