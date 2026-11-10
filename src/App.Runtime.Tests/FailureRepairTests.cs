using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunFailureRepairTests()
    {
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            TestRepairRotation();
            TestRepairOutputValidation();
            TestRepairPreferences(app);
            TestRepairPreferencesWithBindings(app);
            TestRepairCompactGuidance(app);
            TestRepairStallWarning();
            TestRepairTimelineAndFrames();
            TestRepairSetupLayout(app);
            TestRepairWindowLimits();
            TestRepairPanelGestures();
            TestReverseControlCursorShapes();
            TestParallelMediaOutputUi();
            Console.WriteLine("Failure repair regressions passed: rotation, dimensions, persisted selection, timeline, media frames, cursor and parallel outputs.");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void TestRepairRotation()
    {
        var expected = new[] { (0.2, 0.3), (0.3, 0.8), (0.8, 0.7), (0.7, 0.2) };
        for (var turn = 0; turn < 4; ++turn)
        {
            var point = BluetoothMouseOrientationMapper.MapNormalized(.2, .3, 1280, 720, turn,
                BluetoothMouseDirection.Up, BluetoothMouseDirection.Up, false, false);
            InteractionAssert(Math.Abs(point.X - expected[turn].Item1) < 1e-9 &&
                Math.Abs(point.Y - expected[turn].Item2) < 1e-9,
                $"Rotation {turn} disagrees with the renderer's UV transform.");
            foreach (var direction in Enum.GetValues<BluetoothMouseDirection>())
            foreach (var reverseX in new[] { false, true })
            foreach (var reverseY in new[] { false, true })
            {
                var mapped = BluetoothMouseOrientationMapper.MapNormalized(.2, .3, 1280, 720,
                    turn, direction, direction, reverseX, reverseY);
                var restored = BluetoothMouseOrientationMapper.UnmapNormalized(mapped.X, mapped.Y,
                    1280, 720, turn, direction, direction, reverseX, reverseY);
                InteractionAssert(Math.Abs(restored.X - .2) < 1e-9 && Math.Abs(restored.Y - .3) < 1e-9,
                    "Rotation/calibration inverse failed.");
            }
        }
        var delta = BluetoothMouseOrientationMapper.Map(3, 4, 1280, 720, 1,
            BluetoothMouseDirection.Up, BluetoothMouseDirection.Up, false, false);
        InteractionAssert(delta == (4d, -3d), "Relative input must use the renderer's inverse rotation.");
    }

    private static void TestRepairStallWarning()
    {
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(MainWindow).GetMethod("OnLoaded", KeyboardTestMembers, null,
                [typeof(object), typeof(RoutedEventArgs)], null)!);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        using var fixture = new CaptureReviewFixture();
        try
        {
            window.Show();
            SetKeyboardField(vm, "_selectedDevice", fixture.Device("airplay://repair-stall"));
            var status = (Interop.NativeCaptureStatus)fixture.Status("Streaming");
            status.Message = "AirPlay video stalled";
            KeyboardCall(vm, "ApplyCaptureStatus", status);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            var warning = (TextBlock)window.FindName("CaptureWarningText");
            Console.WriteLine($"Stall warning: VM={vm.CaptureWarningVisibility}, UI={warning.Visibility}, text={warning.Text}, status={vm.CaptureStatus}");
            InteractionAssert(vm.CaptureWarningVisibility == Visibility.Visible &&
                warning.Visibility == Visibility.Visible && warning.Text == vm.CaptureStatus &&
                warning.Text == Localization.LocalizationService.Get("WirelessVideoStalled"),
                "Wireless stall message was hidden behind the native preview.");
            status.Message = "AirPlay video streaming";
            KeyboardCall(vm, "ApplyCaptureStatus", status);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            InteractionAssert(warning.Visibility == Visibility.Collapsed,
                "Recovered video retained the old stall warning.");
        }
        finally { CloseWorkspaceTestWindow(window); WaitReviewTask(vm.ShutdownAsync()); }
    }

    private static void TestRepairCompactGuidance(App app)
    {
        var describe = typeof(MainWindow).GetMethod("CompactLaunchErrorMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        InteractionAssert((string)describe.Invoke(null, [new TimeoutException("opaque timeout"), "device_discovery"])! ==
            Localization.LocalizationService.Get("CompactLaunchDeviceUnavailable") &&
            (string)describe.Invoke(null, [new TimeoutException("preview reason"), "preview_start"])! == "preview reason",
            "Shortcut discovery timeout did not explain the missing device, or hid a preview failure.");
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        using var fixture = new CaptureReviewFixture();
        var device = (DeviceViewModel)fixture.Device("repair-compact-unbound");
        var gate = (SemaphoreSlim)KeyboardField(window, "_compactLaunchGate");
        var options = typeof(App).GetProperty("LaunchOptions", KeyboardTestMembers)!;
        var previous = options.GetValue(app);
        var prompts = 0;
        var rescue = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(50) };
        rescue.Tick += (_, _) =>
        {
            foreach (var prompt in app.Windows.OfType<AppPromptWindow>().ToArray())
            { ++prompts; prompt.Close(); }
        };
        rescue.Start();
        try
        {
            options.SetValue(app, new CompactLaunchOptions(true, device.Udid));
            KeyboardCall(window, "ShowDeviceProfileGuidance", device);
            options.SetValue(app, new CompactLaunchOptions(false, null));
            gate.Wait();
            try { KeyboardCall(window, "ShowDeviceProfileGuidance", device); }
            finally { gate.Release(); }
            InteractionAssert(prompts == 0 &&
                !((HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown")).Contains(device.Udid),
                "Cold or forwarded shortcut discovery opened onboarding and blocked startup.");
        }
        finally
        {
            rescue.Stop();
            options.SetValue(app, previous);
            CloseWorkspaceTestWindow(window);
            WaitReviewTask(vm.ShutdownAsync());
        }
    }

    private static void TestRepairOutputValidation()
    {
        using var fixture = new CaptureReviewFixture();
        var window = new MediaOutputSettingsWindow((MainViewModel)fixture.Vm, previewOnly: true);
        try
        {
            foreach (var (width, height, valid) in new[] {
                (160, 160, true), (3840, 2160, true), (159, 160, false),
                (161, 160, false), (3842, 2160, false), (160, 2162, false) })
            {
                object[] args = [new TextBox { Text = width.ToString() }, new TextBox { Text = height.ToString() },
                    new TextBox { Text = "30" }, new TextBox { Text = "6000" }, 0u, 0u, 0, 0];
                var accepted = (bool)window.GetType().GetMethod("TryReadOutputSettings", KeyboardTestMembers)!
                    .Invoke(window, args)!;
                InteractionAssert(accepted == valid, $"Output size {width}x{height} validation failed.");
            }
            var auth = MediaOutputService.DescribeOutputError("[whip] HTTP error 401 Unauthorized");
            var refused = MediaOutputService.DescribeOutputError("Connection to tcp://test failed: Connection refused");
            var pipe = MediaOutputService.DescribeOutputError("Broken pipe");
            InteractionAssert(auth.Priority > pipe.Priority && refused.Priority > pipe.Priority &&
                auth.Message.Contains("401"), "Network errors must outrank secondary pipe failures.");
            InteractionAssert(MediaOutputService.DescribeOutputError("Error number -138 occurred").Message ==
                    Localization.LocalizationService.Get("StreamingConnectionTimeout") &&
                MediaOutputService.DescribeOutputError("Error number -107 occurred").Message ==
                    Localization.LocalizationService.Get("StreamingConnectionRefused"),
                "Windows FFmpeg numeric network errors were not recognized.");
        }
        finally { window.Close(); }
    }

    private static void TestRepairPreferences(App app)
    {
        using var fixture = new CaptureReviewFixture();
        var vm = (MainViewModel)fixture.Vm;
        const string id = "failure-repair-preferences";
        var preferences = app.UpdateSettings.DeviceVideoPreferences;
        preferences[id] = new DeviceVideoPreferences(1280, 720, 30, DecoderPreference.SoftwareCompatible);
        try
        {
            var observed = false;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(MainViewModel.SelectedDevice)) return;
                observed = true;
                InteractionAssert(vm.SelectedFrameRate == 30 && vm.SelectedResolutionPreset.Width == 1280,
                    "Selection observers saw stale defaults before saved settings were restored.");
            };
            KeyboardCall(vm, "SetSelectedDevice", fixture.Device(id), false);
            var state = (DeviceCaptureState)KeyboardCall(vm, "GetOrCreateDeviceState", vm.SelectedDevice!)!;
            InteractionAssert(observed && state.RenderWidth == 1280 && state.RenderHeight == 720 &&
                state.FrameRate == 30 && state.DecoderPreference == DecoderPreference.SoftwareCompatible,
                "Restored session lost persisted device video settings.");
        }
        finally { preferences.Remove(id); }
    }

    private static void TestRepairTimelineAndFrames()
    {
        var window = new MainWindow { ShowInTaskbar = false };
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        try
        {
            SetKeyboardField(window, "_mediaProgramDuration", 1000d);
            foreach (var rate in new[] { .5, 1d, 2d })
            {
                SetKeyboardField(window, "_mediaTimelineAnchorPosition", 10d);
                SetKeyboardField(window, "_mediaTimelineRunning", true);
                SetKeyboardField(window, "_mediaPlaybackSpeed", rate);
                SetKeyboardField(window, "_mediaTimelineAnchorUtc", DateTime.UtcNow.AddSeconds(-4));
                var actual = (double)KeyboardCall(window, "ReadMediaCastTimelinePosition")!;
                InteractionAssert(Math.Abs(actual - (10 + rate * 4)) < .2, "Media timeline ignored speed.");
            }
            SetKeyboardField(window, "_mediaCastActive", true);
            var ctor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
            SetKeyboardField(vm, "_selectedDevice", ctor.Invoke([DeviceViewModel.MediaCastUdid,
                "media", "", "", "", "", Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]));
            var host = (Grid)window.FindName("MediaCastVideoHost");
            host.Background = Brushes.Red;
            host.Measure(new Size(640, 360));
            host.Arrange(new Rect(0, 0, 640, 360));
            var frame = (Interop.Nv12VideoFrame?)KeyboardCall(window, "CaptureMediaCastNv12Frame", 640u, 360u);
            InteractionAssert(frame is { Width: 640, Height: 360 } && frame.Pixels.Length == 640 * 360 * 3 / 2,
                "Media cast frame provider did not return an owned NV12 frame.");
            InteractionAssert(frame!.Pixels[0] == 54 && frame.Pixels[640 * 360] == 99 &&
                frame.Pixels[640 * 360 + 1] == 255,
                "Media cast rendering lost the known red source pixels.");
            // The production reader runs on a worker. Exercise its dispatcher
            // handoff as well as the direct UI-thread rendering path.
            var read = Task.Run(() => (Interop.Nv12VideoFrame?)KeyboardCall(window,
                "CaptureMediaCastNv12Frame", 640u, 360u));
            WaitReviewTask(read);
            InteractionAssert(read.Result is not null && read.Result.Pixels.SequenceEqual(frame.Pixels),
                "Worker frame capture failed or returned a blank payload.");
        }
        finally
        {
            SetKeyboardField(window, "_mediaCastActive", false);
            CloseWorkspaceTestWindow(window);
            WaitReviewTask(vm.ShutdownAsync());
        }
    }

    private static void TestRepairPreferencesWithBindings(App app)
    {
        const string target = "repair-bound-device";
        app.UpdateSettings.DeviceVideoPreferences[target] = new(1280, 720, 30, DecoderPreference.SoftwareCompatible);
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(MainWindow).GetMethod("OnLoaded", KeyboardTestMembers, null,
                [typeof(object), typeof(RoutedEventArgs)], null)!);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        using var deviceFixture = new CaptureReviewFixture();
        var device = (DeviceViewModel)deviceFixture.Device(target);
        try
        {
            window.Show();
            ((HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown")).Add(target);
            vm.Devices.Add(device);
            KeyboardCall(vm, "SetSelectedDevice", device, false);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            var resolution = (ComboBox)window.FindName("ResolutionComboBox");
            var fps = (ComboBox)window.FindName("FrameRateComboBox");
            Console.WriteLine($"Bound preferences: selected={vm.SelectedDevice?.Udid}; VM={vm.SelectedResolutionPreset.Width}x{vm.SelectedResolutionPreset.Height}/{vm.SelectedFrameRate}; resolution={resolution.SelectedItem}; fps={fps.SelectedItem}; saved={app.UpdateSettings.DeviceVideoPreferences[target]}");
            InteractionAssert(ReferenceEquals(resolution.SelectedItem, vm.SelectedResolutionPreset) &&
                (int)fps.SelectedItem == 30 && vm.SelectedResolutionPreset.Width == 1280,
                "Real main-window bindings replaced restored device settings with defaults.");
            var settings = KeyboardCall(vm, "CaptureSessionStartSettings",
                KeyboardCall(vm, "GetOrCreateDeviceState", device)!)!;
            InteractionAssert(ReadProperty<uint>(settings, "RenderWidth") == 1280 &&
                ReadProperty<uint>(settings, "RenderHeight") == 720 && ReadProperty<int>(settings, "FrameRate") == 30,
                "The native start request did not retain saved dimensions and FPS.");
        }
        finally
        {
            app.UpdateSettings.DeviceVideoPreferences.Remove(target);
            CloseWorkspaceTestWindow(window);
            WaitReviewTask(vm.ShutdownAsync());
        }
    }

    private static void TestRepairSetupLayout(App app)
    {
        SetAuditCulture("ja-JP");
        IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, 2.25, false);
        using var fixture = new CaptureReviewFixture();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "repair-setup-" + Guid.NewGuid().ToString("N"), "state.json");
        var window = new FirstRunSetupWindow((MainViewModel)fixture.Vm,
            new FirstRunSetupStore(path), previewOnly: true)
            { Width = 600, Height = 540, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        try
        {
            window.Show();
            window.Width = 600; window.Height = 540;
            window.UpdateLayout();
            var skip = (Button)window.FindName("Skip");
            var next = (Button)window.FindName("Next");
            var a = skip.TransformToAncestor(window).TransformBounds(new Rect(skip.RenderSize));
            var b = next.TransformToAncestor(window).TransformBounds(new Rect(next.RenderSize));
            InteractionAssert(a.Right <= b.Left && b.Right <= 600 && b.Width >= 112,
                "Japanese skip text pushed the next button out of the minimum-size wizard.");
        }
        finally
        {
            SetKeyboardField(window, "_allowClose", true);
            window.Close();
            IPhoneMirror.UI.Services.AccessibilityAppearance.Apply(app, 1, false);
            SetAuditCulture("en-US");
        }
    }

    private static void TestRepairWindowLimits()
    {
        var window = new MainWindow { ShowInTaskbar = false, MinWidth = 4000, MinHeight = 3000 };
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var controller = typeof(App).Assembly.GetType("IPhoneMirror.SharedUI.Services.WindowWorkAreaController", true)!;
            controller.GetMethod("Attach", KeyboardTestMembers | System.Reflection.BindingFlags.Static)!.Invoke(null, [window]);
            var memory = Marshal.AllocHGlobal(40);
            try
            {
                Marshal.Copy(new byte[40], 0, memory, 40);
                RepairSendMessage(handle, 0x0024, 0, memory);
                var maxWidth = Marshal.ReadInt32(memory, 8);
                var maxHeight = Marshal.ReadInt32(memory, 12);
                var minWidth = Marshal.ReadInt32(memory, 24);
                var minHeight = Marshal.ReadInt32(memory, 28);
                InteractionAssert(maxWidth > 0 && maxHeight > 0 && minWidth <= maxWidth && minHeight <= maxHeight,
                    "Main window minimum tracking size exceeds its monitor's maximize work area.");
                window.MinWidth = window.MinHeight = 0;
                window.MaxWidth = 500;
                window.MaxHeight = 400;
                Marshal.Copy(new byte[40], 0, memory, 40);
                RepairSendMessage(handle, 0x0024, 0, memory);
                var scale = RepairGetDpiForWindow(handle) / 96d;
                InteractionAssert(Marshal.ReadInt32(memory, 8) <= Math.Ceiling(500 * scale) &&
                    Marshal.ReadInt32(memory, 12) <= Math.Ceiling(400 * scale) &&
                    Marshal.ReadInt32(memory, 32) <= Math.Ceiling(500 * scale) &&
                    Marshal.ReadInt32(memory, 36) <= Math.Ceiling(400 * scale),
                    "Work-area constraints overrode an explicit maximum window size.");
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        finally { CloseWorkspaceTestWindow(window); WaitReviewTask(vm.ShutdownAsync()); }
    }

    private static void TestRepairPanelGestures()
    {
        var method = typeof(MainWindow).GetMethod("SendPanelGestureAsync",
            KeyboardTestMembers | System.Reflection.BindingFlags.Static)!;
        foreach (var cancel in new[] { false, true })
        foreach (var bottom in new[] { false, true })
        {
            var current = true;
            var points = new List<(string State, double X, double Y)>();
            var route = new MappedTouchRoute("fixture", () => current, (state, x, y, _) =>
            {
                points.Add((state, x, y));
                if (cancel && state == "move") current = false;
                return Task.CompletedTask;
            }, (x, y) => (x, y));
            WaitReviewTask((Task)method.Invoke(null, [route, true, bottom])!);
            InteractionAssert(points[0].State == "down" && points[^1].State == "up" &&
                points.Count == (cancel ? 3 : 14), "Panel gesture did not release its captured contact.");
            InteractionAssert(bottom ? points[^1].Y < points[0].Y : points[^1].Y > points[0].Y,
                "Panel gesture used the wrong screen edge.");
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint RepairSendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint RepairGetDpiForWindow(nint hwnd);
}
