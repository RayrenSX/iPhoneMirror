using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Updater;
using Wpf.Ui.Controls;
using WpfBorder = System.Windows.Controls.Border;
using WpfButton = System.Windows.Controls.Button;
using WpfFlowDocumentScrollViewer = System.Windows.Controls.FlowDocumentScrollViewer;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using WpfThumb = System.Windows.Controls.Primitives.Thumb;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfSymbolIcon = Wpf.Ui.Controls.SymbolIcon;

namespace IPhoneMirror.App.Runtime.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--live-record", .. var recordingArgs])
                return RunLiveRecordingAsync(recordingArgs).GetAwaiter().GetResult();
            if (args is ["--ui-preview", var themeName, var surface])
                return RunUiPreview(themeName, surface);
            TestUpdateWindowThemeSwitch();
            Console.WriteLine("App runtime tests passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static int RunUiPreview(string themeName, string surface)
    {
        if (!Enum.TryParse<AppTheme>(themeName, ignoreCase: true, out var theme) ||
            theme == AppTheme.System)
            throw new ArgumentException("UI preview theme must be Light or Dark.");

        var application = new App();
        var previewMode = typeof(App).GetProperty("IsUiPreviewMode",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMemberException(typeof(App).FullName, "IsUiPreviewMode");
        previewMode.SetValue(application, true);
        application.InitializeComponent();
        var assembly = typeof(App).Assembly;
        ApplyTheme(assembly, theme);
        Window window;
        if (surface.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            var main = new MainWindow
            {
                Width = 1360,
                Height = 820,
                ShowInTaskbar = true,
            };
            var loaded = typeof(MainWindow).GetMethod("OnLoaded",
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                types: [typeof(object), typeof(RoutedEventArgs)], modifiers: null) ??
                throw new MissingMethodException(typeof(MainWindow).FullName, "OnLoaded");
            main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(
                typeof(RoutedEventHandler), main, loaded);
            window = main;
        }
        else if (surface.Equals("child", StringComparison.OrdinalIgnoreCase))
        {
            window = new IPhoneMirror.App.Windows.AdvancedSettingsWindow(
                1920, 1080, previewOnly: true);
        }
        else
        {
            throw new ArgumentException("UI preview surface must be main or child.");
        }

        window.Title = $"iPhoneMirror UI Audit — {theme} — {surface}";
        application.MainWindow = window;
        window.Closed += (_, _) => application.Shutdown();
        window.Show();
        ApplyTheme(assembly, theme);
        Dispatcher.Run();
        return 0;
    }

    private static async Task<int> RunLiveRecordingAsync(string[] args)
    {
        if (args.Length != 6 ||
            !uint.TryParse(args[1], out var width) ||
            !uint.TryParse(args[2], out var height) ||
            !int.TryParse(args[3], out var frameRate) ||
            !int.TryParse(args[4], out var bitrateKbps) ||
            !int.TryParse(args[5], out var durationSeconds) ||
            width == 0 || height == 0 || frameRate <= 0 || bitrateKbps <= 0 ||
            durationSeconds <= 0)
        {
            Console.Error.WriteLine(
                "Usage: --live-record <output.mp4> <width> <height> <fps> <kbps> <seconds>");
            return 2;
        }

        var destination = Path.GetFullPath(args[0]);
        if (File.Exists(destination))
            throw new IOException($"Live-test output already exists: {destination}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ??
            throw new InvalidOperationException("The output path has no directory."));

        var assembly = typeof(App).Assembly;
        var nativeType = RequireType(assembly, "IPhoneMirror.App.Interop.NativeCore");
        var serviceType = RequireType(assembly,
            "IPhoneMirror.App.Services.MediaOutputService");
        object? core = null;
        object? service = null;
        ulong sessionHandle = 0;
        try
        {
            core = Activator.CreateInstance(nativeType, BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic, null, null, null) ??
                throw new InvalidOperationException("NativeCore could not be created.");
            var getDevices = RequireMethod(nativeType, "GetDevices",
                BindingFlags.Instance | BindingFlags.Public);
            var devices = (IEnumerable)(getDevices.Invoke(core, [false]) ??
                throw new InvalidOperationException("Device enumeration returned no result."));
            var device = devices.Cast<object>().FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(ReadField<string>(candidate, "Udid")) &&
                !string.Equals(ReadField<string>(candidate, "ConnectionType"),
                    "AirPlay", StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidOperationException("No wired Apple device is available.");
            var udid = ReadField<string>(device, "Udid");
            Console.WriteLine($"Device: {ReadField<string>(device, "Name")} " +
                $"{ReadField<string>(device, "ProductType")} " +
                $"({ReadField<string>(device, "ConnectionType")})");

            var createSession = RequireMethod(nativeType, "CreateDeviceSession",
                BindingFlags.Instance | BindingFlags.Public);
            var sessionResult = createSession.Invoke(core,
                [udid, 0U, 0U, 60U, true, 1.0, 0U, 0U, 0U, 0U, 0U]) ??
                throw new InvalidOperationException("Capture session returned no result.");
            if (!ReadProperty<bool>(sessionResult, "Success"))
                throw new InvalidOperationException(
                    $"Capture start failed: {ReadProperty<string>(sessionResult, "Message")}");
            sessionHandle = ReadProperty<ulong>(sessionResult, "Handle");
            Console.WriteLine($"Session: {sessionHandle}");

            var getStatus = RequireMethod(nativeType, "GetDeviceSessionStatus",
                BindingFlags.Instance | BindingFlags.Public);
            var readyDeadline = Stopwatch.StartNew();
            ulong lastFrames = 0;
            while (readyDeadline.Elapsed < TimeSpan.FromSeconds(45))
            {
                var status = getStatus.Invoke(core, [sessionHandle]) ??
                    throw new InvalidOperationException("Capture status returned no result.");
                var state = ReadField<object>(status, "State").ToString();
                var frames = ReadField<ulong>(status, "VideoFrames");
                if (state == "Error")
                    throw new InvalidOperationException(
                        $"Capture failed: {ReadField<string>(status, "Message")}");
                if (state == "Streaming" && frames > lastFrames)
                {
                    Console.WriteLine($"Streaming: {ReadField<uint>(status, "Width")}x" +
                        $"{ReadField<uint>(status, "Height")} " +
                        $"{ReadField<double>(status, "Fps"):F2} fps");
                    break;
                }
                lastFrames = frames;
                await Task.Delay(250);
            }
            if (readyDeadline.Elapsed >= TimeSpan.FromSeconds(45))
                throw new TimeoutException("The wired capture session did not start within 45 seconds.");

            var constructor = serviceType.GetConstructors(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic).Single();
            var parameters = constructor.GetParameters();
            var frameProvider = RequireMethod(nativeType,
                "GetDeviceOutputNv12Frame", BindingFlags.Instance |
                BindingFlags.NonPublic).CreateDelegate(parameters[0].ParameterType, core);
            var audioProvider = RequireMethod(nativeType,
                "GetDeviceOutputAudioPacket", BindingFlags.Instance |
                BindingFlags.NonPublic).CreateDelegate(parameters[1].ParameterType, core);
            service = constructor.Invoke([frameProvider, audioProvider]);

            string outputStatus = string.Empty;
            bool outputFailed = false;
            var statusHandler = new Action<string, bool>((message, failed) =>
            {
                outputStatus = message;
                outputFailed = failed;
                Console.WriteLine($"Output status: {message} (failed={failed})");
            });
            serviceType.GetEvent("StatusChanged", BindingFlags.Instance |
                BindingFlags.NonPublic)?.GetAddMethod(true)?.Invoke(service,
                    [statusHandler]);

            var capabilities = await InvokeAsyncResult(RequireMethod(serviceType,
                "ProbeAsync", BindingFlags.Static | BindingFlags.NonPublic).Invoke(
                    null, [CancellationToken.None]) ??
                throw new InvalidOperationException("FFmpeg probe returned no task.")) ??
                throw new InvalidOperationException("FFmpeg probe returned no capabilities.");
            Console.WriteLine($"Encoder: {ReadProperty<string>(capabilities,
                "PreferredH264Encoder")}");
            var requestType = RequireType(assembly,
                "IPhoneMirror.App.Services.MediaOutputRequest");
            var kindType = RequireType(assembly,
                "IPhoneMirror.App.Services.MediaOutputKind");
            var request = Activator.CreateInstance(requestType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { Enum.Parse(kindType, "Recording"), destination,
                    width, height, frameRate, bitrateKbps, string.Empty }, null) ??
                throw new InvalidOperationException("Recording request could not be created.");
            await InvokeAsyncResult(RequireMethod(serviceType, "StartAsync",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service,
                    [sessionHandle, request, capabilities, CancellationToken.None]) ??
                throw new InvalidOperationException("Recording start returned no task."));

            Process? encoder = null;
            var encoderCpuStart = TimeSpan.Zero;
            var recordingClock = Stopwatch.StartNew();
            var isRunning = serviceType.GetProperty("IsRunning",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new MissingMemberException(serviceType.FullName, "IsRunning");
            while (recordingClock.Elapsed < TimeSpan.FromSeconds(durationSeconds))
            {
                await Task.Delay(1000);
                encoder ??= Process.GetProcessesByName("ffmpeg")
                    .OrderByDescending(candidate => candidate.StartTime).FirstOrDefault();
                if (encoder is not null && encoderCpuStart == TimeSpan.Zero)
                    encoderCpuStart = encoder.TotalProcessorTime;
                var status = getStatus.Invoke(core, [sessionHandle]) ??
                    throw new InvalidOperationException("Capture status returned no result.");
                var encoderMemory = TryGetWorkingSetMb(encoder);
                Console.WriteLine($"t={recordingClock.Elapsed.TotalSeconds,5:F1}s " +
                    $"capture={ReadField<double>(status, "Fps"),6:F2}fps " +
                    $"frames={ReadField<ulong>(status, "VideoFrames")} " +
                    $"encoder_mb={encoderMemory,6:F1}");
                if (!(bool)isRunning.GetValue(service)!)
                    throw new InvalidOperationException(outputFailed
                        ? $"Recording stopped early: {outputStatus}"
                        : "Recording stopped before the requested duration.");
            }

            await InvokeAsyncResult(RequireMethod(serviceType, "StopAsync",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, null) ??
                throw new InvalidOperationException("Recording stop returned no task."));
            if (!File.Exists(destination))
                throw new FileNotFoundException("The finalized recording is missing.",
                    destination);
            var file = new FileInfo(destination);
            Console.WriteLine($"Recording: {file.FullName} ({file.Length / 1048576.0:F2} MiB)");
            if (encoder is not null && encoderCpuStart != TimeSpan.Zero &&
                TryGetTotalProcessorTime(encoder, out var encoderCpuEnd))
            {
                var encoderCpu = encoderCpuEnd - encoderCpuStart;
                var totalPercent = encoderCpu.TotalSeconds /
                    recordingClock.Elapsed.TotalSeconds * 100.0;
                Console.WriteLine($"Encoder CPU: {totalPercent:F1}% total, " +
                    $"{totalPercent / Environment.ProcessorCount:F1}% machine");
            }
            return 0;
        }
        finally
        {
            if (service is not null)
            {
                var dispose = serviceType.GetMethod("DisposeAsync",
                    BindingFlags.Instance | BindingFlags.Public);
                if (dispose?.Invoke(service, null) is ValueTask pendingDispose)
                    await pendingDispose;
            }
            if (core is not null && sessionHandle != 0)
            {
                try
                {
                    RequireMethod(nativeType, "StopDeviceSession",
                        BindingFlags.Instance | BindingFlags.Public).Invoke(core,
                            [sessionHandle]);
                }
                catch (TargetInvocationException error)
                {
                    Console.Error.WriteLine($"Session stop warning: " +
                        $"{error.InnerException?.Message ?? error.Message}");
                }
                RequireMethod(nativeType, "DestroyDeviceSession",
                    BindingFlags.Instance | BindingFlags.Public).Invoke(core,
                        [sessionHandle]);
            }
            (core as IDisposable)?.Dispose();
        }
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true) ??
        throw new TypeLoadException(name);

    private static MethodInfo RequireMethod(Type type, string name,
        BindingFlags flags) => type.GetMethod(name, flags) ??
        throw new MissingMethodException(type.FullName, name);

    private static T ReadField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance) ??
            throw new MissingFieldException(instance.GetType().FullName, name));

    private static T ReadProperty<T>(object instance, string name) =>
        (T)(instance.GetType().GetProperty(name, BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance) ??
            throw new MissingMemberException(instance.GetType().FullName, name));

    private static double TryGetWorkingSetMb(Process? process)
    {
        try
        {
            return process is { HasExited: false }
                ? process.WorkingSet64 / 1048576.0 : 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static bool TryGetTotalProcessorTime(Process process,
        out TimeSpan value)
    {
        try
        {
            value = process.TotalProcessorTime;
            return true;
        }
        catch (InvalidOperationException)
        {
            value = TimeSpan.Zero;
            return false;
        }
    }

    private static async Task<object?> InvokeAsyncResult(object awaitable)
    {
        if (awaitable is not Task task)
            throw new InvalidOperationException("The reflected operation is not a Task.");
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static void TestHongKongLocalizationSwitch(Application application,
        Assembly assembly)
    {
        var localizationService = assembly.GetType(
            "IPhoneMirror.App.Localization.LocalizationService", throwOnError: true)!;
        var applyLanguage = localizationService.GetMethod("ApplyLanguage",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(localizationService.FullName, "ApplyLanguage");

        applyLanguage.Invoke(null, ["zh-HK", false, false]);
        if (application.TryFindResource("StartMirroring") is not string start ||
            !start.Contains("螢幕鏡像", StringComparison.Ordinal) ||
            application.TryFindResource("NavigationTextFontFamily") is not FontFamily font ||
            !font.Source.Equals("Microsoft JhengHei UI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Hong Kong localization dictionary did not load at runtime.");
        applyLanguage.Invoke(null, ["system", false, false]);
    }

    private static void TestUpdateWindowThemeSwitch()
    {
        var application = new App();
        application.InitializeComponent();
        var assembly = typeof(App).Assembly;
        TestHongKongLocalizationSwitch(application, assembly);

        var parserType = assembly.GetType(
            "IPhoneMirror.App.Updater.ReleaseParser", throwOnError: true)!;
        var parseLatest = parserType.GetMethod("ParseLatest",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(parserType.FullName, "ParseLatest");
        const string releaseJson = """
            [{
              "tag_name": "v99.0.0",
              "name": "Update window runtime test",
              "body": "# Changes\nRuntime XAML construction test",
              "published_at": "2026-07-31T00:00:00Z",
              "draft": false,
              "prerelease": false,
              "assets": [{
                "name": "iPhoneMirror-Setup-v99.0.0-x64.exe",
                "size": 1,
                "browser_download_url": "https://github.com/RayrenSX/iPhoneMirror/releases/download/v99.0.0/iPhoneMirror-Setup-v99.0.0-x64.exe"
              }]
            }]
            """;
        var release = parseLatest.Invoke(null, [releaseJson, true, false]) ??
            throw new InvalidOperationException("Release fixture was not parsed.");

        var clientType = assembly.GetType(
            "IPhoneMirror.App.Updater.GitHubReleaseClient", throwOnError: true)!;
        var client = Activator.CreateInstance(clientType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [null, null], culture: null) ??
            throw new InvalidOperationException("Update client was not constructed.");
        var owner = new FluentWindow
        {
            Width = 320,
            Height = 240,
            ShowInTaskbar = false,
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = WindowBackdropType.Mica,
        };
        application.MainWindow = owner;
        owner.Show();
        try
        {
            var windowType = assembly.GetType(
                "IPhoneMirror.App.Windows.UpdateWindow", throwOnError: true)!;
            var window = Activator.CreateInstance(windowType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [release, client, false, true], culture: null) as Window ??
                throw new InvalidOperationException("Update window was not constructed.");
            window.Owner = owner;
            ((FluentWindow)window).WindowBackdropType = WindowBackdropType.Acrylic;
            window.Show();
            try
            {
                var updateButton = window.FindName("UpdateActionButton") as WpfButton ??
                    throw new InvalidOperationException("Update action button was not found.");
                var releaseNotesViewer =
                    window.FindName("ReleaseNotesViewer") as WpfFlowDocumentScrollViewer ??
                    throw new InvalidOperationException("Release notes viewer was not found.");
                window.UpdateLayout();
                ApplyTheme(assembly, AppTheme.Light);
                AssertThemeBrush(window, "TextBrush", Color.FromRgb(0x1D, 0x1D, 0x1F));
                AssertThemeBrush(window, "AboutCheckUpdatesTextBrush", Colors.White);
                AssertPrimaryButtonText(updateButton, Colors.White);
                AssertModernScrollBar(releaseNotesViewer);
                AssertBackdropBackground(window);

                ApplyTheme(assembly, AppTheme.Dark);
                AssertThemeBrush(window, "TextBrush", Color.FromRgb(0xF5, 0xF5, 0xF7));
                AssertThemeBrush(window, "AboutCheckUpdatesTextBrush",
                    Color.FromRgb(0x0F, 0x14, 0x19));
                AssertPrimaryButtonText(updateButton, Color.FromRgb(0x0F, 0x14, 0x19));
                AssertBackdropBackground(window);
            }
            finally
            {
                window.Close();
            }

            TestDeveloperToolsWindow(application, assembly);
            TestMainWindowThemeAndCaptionControls(application, assembly);
        }
        finally
        {
            ((IDisposable)client).Dispose();
            owner.Close();
            application.Shutdown();
        }
    }

    private static void TestDeveloperToolsWindow(Application application,
        Assembly assembly)
    {
        var owner = new MainWindow
        {
            Width = 1280,
            Height = 700,
            ShowInTaskbar = false,
        };
        application.MainWindow = owner;
        owner.Show();
        try
        {
            var versionClick = typeof(MainWindow).GetMethod("OnVersionClick",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new MissingMethodException(typeof(MainWindow).FullName,
                    "OnVersionClick");
            for (var index = 0; index < 5; ++index)
                versionClick.Invoke(owner, [owner, new RoutedEventArgs()]);

            var window = application.Windows.Cast<Window>().FirstOrDefault(
                candidate => candidate.GetType().Name == "DeveloperToolsWindow") ??
                throw new InvalidOperationException(
                    "Five version clicks did not open developer tools.");
            var windowType = window.GetType();
            try
            {
                window.UpdateLayout();
                if (!window.IsVisible || window.ActualWidth < 820 ||
                    window.ActualHeight < 620)
                    throw new InvalidOperationException(
                        "Developer tools window did not open at its minimum size.");
                if (window.Owner is not null || window.Topmost)
                    throw new InvalidOperationException(
                        "Developer tools window must be independent and non-topmost.");
                AssertWindowsOwnOuterCorners(window, "Developer tools");
                AssertCatalogCount(windowType, window, "WorkspaceItems", 6);
                AssertCatalogCount(windowType, window, "WindowItems", 11);
                foreach (var controlName in new[]
                {
                    "ThemeComboBox", "LanguageComboBox", "OpacitySlider",
                    "TopmostCheckBox",
                })
                {
                    if (window.FindName(controlName) is null)
                        throw new InvalidOperationException(
                            $"Developer control was not found: {controlName}");
                }

                var header = window.FindName("HeaderDragSurface") as DependencyObject ??
                    throw new InvalidOperationException(
                        "Developer drag surface was not found.");
                var dragBehavior = assembly.GetType(
                    "IPhoneMirror.UI.Animations.WindowDragBehavior",
                    throwOnError: true)!;
                var getDragEnabled = RequireMethod(dragBehavior, "GetIsEnabled",
                    BindingFlags.Static | BindingFlags.Public);
                if (getDragEnabled.Invoke(null, [header]) is not true)
                    throw new InvalidOperationException(
                        "Developer drag behavior is not enabled.");

                TestDeveloperPreviewActions(application, owner);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            owner.Close();
        }
    }

    private static void TestMainWindowThemeAndCaptionControls(Application application,
        Assembly assembly)
    {
        var window = new MainWindow
        {
            Width = 1280,
            Height = 760,
            ShowInTaskbar = false,
        };
        application.MainWindow = window;
        window.Show();
        try
        {
            window.UpdateLayout();
            var titleButtonStyle = window.TryFindResource("TitleBarButton");
            var closeButtonStyle = window.TryFindResource("TitleBarCloseButton");
            var buttons = FindVisualDescendants<WpfButton>(window)
                .Where(button => ReferenceEquals(button.Style, titleButtonStyle) ||
                                 ReferenceEquals(button.Style, closeButtonStyle))
                .ToArray();
            if (buttons.Length != 3 ||
                buttons.Any(button => button.Width < 40 || button.Height < 38) ||
                buttons.Count(button => ReferenceEquals(button.Style, closeButtonStyle)) != 1)
                throw new InvalidOperationException(
                    "Main title-bar controls do not use the accessible caption-button styles.");

            ApplyTheme(assembly, AppTheme.Light);
            AssertThemeBrush(window, "AccentBrush", Color.FromRgb(0x0F, 0x6C, 0xBD));
            AssertThemeBrush(window, "SuccessBrush", Color.FromRgb(0x10, 0x7C, 0x41));
            AssertThemeBrush(window, "WarningBrush", Color.FromRgb(0x9A, 0x4B, 0x00));
            AssertCaptionIconForeground(window, Color.FromRgb(0x1D, 0x1D, 0x1F));
            ApplyTheme(assembly, AppTheme.Dark);
            AssertThemeBrush(window, "AccentBrush", Color.FromRgb(0x69, 0xB1, 0xF8));
            AssertThemeBrush(window, "SuccessBrush", Color.FromRgb(0x6C, 0xCB, 0x8F));
            AssertCaptionIconForeground(window, Color.FromRgb(0xF5, 0xF5, 0xF7));
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertCaptionIconForeground(Window window, Color expectedColor)
    {
        var titleButtonStyle = window.TryFindResource("TitleBarButton");
        var closeButtonStyle = window.TryFindResource("TitleBarCloseButton");
        var titleButtons = FindVisualDescendants<WpfButton>(window)
            .Where(button => ReferenceEquals(button.Style, titleButtonStyle) ||
                             ReferenceEquals(button.Style, closeButtonStyle))
            .ToArray();
        if (titleButtons.Length != 3)
            throw new InvalidOperationException(
                "Main title-bar caption buttons were not found for icon contrast verification.");
        foreach (var button in titleButtons)
        {
            var icon = FindVisualDescendant<WpfSymbolIcon>(button,
                _ => true) ?? throw new InvalidOperationException(
                    "Caption button does not contain a SymbolIcon.");
            var brush = icon.Foreground as SolidColorBrush;
            if (brush is null || brush.Color != expectedColor)
                throw new InvalidOperationException(
                    $"Caption icon foreground did not follow the active theme: expected {expectedColor}, got {brush?.Color.ToString() ?? "unset"}.");
        }
    }

    private static void TestDeveloperPreviewActions(Application application,
        MainWindow owner)
    {
        var openSurface = RequireMethod(typeof(MainWindow),
            "OpenDeveloperSurface", BindingFlags.Instance | BindingFlags.NonPublic);
        TestDeveloperPreviewAction(application, owner, openSurface,
            "prompt", "AppPromptWindow", "OnConfirmClick");
        TestDeveloperPreviewAction(application, owner, openSurface,
            "prompt", "AppPromptWindow", "OnCancelClick");
        TestDeveloperPreviewAction(application, owner, openSurface,
            "advanced-settings", "AdvancedSettingsWindow", "OnApplyClick");
        TestDeveloperPreviewAction(application, owner, openSurface,
            "advanced-settings", "AdvancedSettingsWindow", "OnDisableClick");
        openSurface.Invoke(owner, ["instance-conflict"]);
        var conflictWindow = application.Windows.Cast<Window>().LastOrDefault(
            candidate => candidate.GetType().Name == "InstanceConflictWindow") ??
            throw new InvalidOperationException(
                "Developer preview did not open: instance-conflict");
        try
        {
            conflictWindow.UpdateLayout();
            AssertWindowsOwnOuterCorners(conflictWindow, "Instance conflict window");
        }
        finally
        {
            conflictWindow.Close();
        }
    }

    private static void AssertWindowsOwnOuterCorners(Window window, string label)
    {
        var windowSurface = window.FindName("WindowSurface") as WpfBorder ??
            throw new InvalidOperationException($"{label} surface was not found.");
        if (windowSurface.CornerRadius != new CornerRadius(0) ||
            windowSurface.BorderThickness != new Thickness(0))
            throw new InvalidOperationException(
                $"{label} must leave outer corners and borders to Windows.");
        var cornerPreference = window.GetType().GetProperty(
            "WindowCornerPreference")?.GetValue(window)?.ToString();
        if (!string.Equals(cornerPreference, "Round", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{label} must use Windows rounded corners.");
    }

    private static void TestDeveloperPreviewAction(Application application,
        MainWindow owner, MethodInfo openSurface, string surfaceKey,
        string windowTypeName, string actionName)
    {
        openSurface.Invoke(owner, [surfaceKey]);
        var preview = application.Windows.Cast<Window>().LastOrDefault(
            candidate => candidate.GetType().Name == windowTypeName) ??
            throw new InvalidOperationException(
                $"Developer preview did not open: {surfaceKey}");
        try
        {
            var action = RequireMethod(preview.GetType(), actionName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            action.Invoke(preview, [preview, new RoutedEventArgs()]);
            if (preview.IsVisible)
                throw new InvalidOperationException(
                    $"Developer preview action did not close: {surfaceKey}/{actionName}");
        }
        finally
        {
            if (preview.IsVisible) preview.Close();
        }
    }

    private static void AssertCatalogCount(Type windowType, object window,
        string propertyName, int expected)
    {
        var value = windowType.GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public)?.GetValue(window) as IEnumerable ??
            throw new InvalidOperationException(
                $"Developer catalog was not found: {propertyName}");
        var actual = value.Cast<object>().Count();
        if (actual != expected)
            throw new InvalidOperationException(
                $"Developer catalog {propertyName} expected {expected}, got {actual}.");
    }

    private static void ApplyTheme(Assembly assembly, AppTheme theme)
    {
        var themeService = assembly.GetType(
            "IPhoneMirror.App.Services.ThemeService", throwOnError: true)!;
        var apply = themeService.GetMethod("Apply",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(themeService.FullName, "Apply");
        apply.Invoke(null, [theme]);
    }

    private static void AssertThemeBrush(Window window, string resourceKey,
        Color expectedColor)
    {
        if (window.TryFindResource(resourceKey) is not SolidColorBrush brush ||
            brush.Color != expectedColor)
            throw new InvalidOperationException(
                $"Child window did not refresh {resourceKey} for the active theme.");
    }

    private static void AssertPrimaryButtonText(WpfButton button, Color expectedColor)
    {
        button.ApplyTemplate();
        if (button.Template.FindName("PrimaryLabel", button) is not WpfTextBlock label ||
            label.Foreground is not SolidColorBrush brush || brush.Color != expectedColor)
            throw new InvalidOperationException(
                $"Primary button text did not use the expected theme color {expectedColor}.");
    }

    private static void AssertModernScrollBar(DependencyObject root)
    {
        var scrollBar = FindVisualDescendant<WpfScrollBar>(root,
            candidate => candidate.Orientation == System.Windows.Controls.Orientation.Vertical) ??
            throw new InvalidOperationException(
                "Release notes did not create a vertical scrollbar.");
        scrollBar.ApplyTemplate();
        if (scrollBar.Width != 6 ||
            scrollBar.RenderTransform is not TranslateTransform { X: 6 } ||
            scrollBar.Template.FindName("ScrollThumb", scrollBar) is not WpfThumb thumb ||
            thumb.Width != 2)
            throw new InvalidOperationException(
                "Release notes did not use the shared modern scrollbar template.");
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && match(candidate)) return candidate;
            var descendant = FindVisualDescendant(child, match);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate) yield return candidate;
            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private static void AssertBackdropBackground(Window window)
    {
        if (window is not FluentWindow fluentWindow ||
            fluentWindow.WindowBackdropType == WindowBackdropType.None ||
            !WindowBackdrop.IsSupported(fluentWindow.WindowBackdropType))
            return;
        if (window.Background is not SolidColorBrush brush ||
            brush.Color != Colors.Transparent)
            throw new InvalidOperationException(
                "Child window backdrop kept a stale themed background after switching themes.");
    }
}
