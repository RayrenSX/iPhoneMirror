using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;
using IPhoneMirror.Shared.Networking;
using IPhoneMirror.SharedUI.Services;

const string delayedExitEnvironment =
    "IPHONE_MIRROR_TEST_DELAYED_PROCESS_EXIT_MS";
if (int.TryParse(Environment.GetEnvironmentVariable(delayedExitEnvironment),
        out var delayedExitMilliseconds) && delayedExitMilliseconds > 0)
{
    await Task.Delay(delayedExitMilliseconds);
    return;
}

var diagnosticTestRoot = Path.Combine(Path.GetTempPath(),
    $"iPhoneMirror-test-logs-{Guid.NewGuid():N}");
Environment.SetEnvironmentVariable("IPHONE_MIRROR_APP_LOG_DIRECTORY",
    diagnosticTestRoot, EnvironmentVariableTarget.Process);

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
}

static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual, string name)
{
    if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            $"{name}: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
}

static void Throws<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}");
}

static async Task ThrowsAsync<TException>(Func<Task> action, string name)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}");
}

static async Task<(int ExitCode, string Output)> RunWindowsPowerShellAsync(
    string script, string zipPath, string installDirectory, string restartExecutable,
    string? expectedSha256 = null, bool skipRestart = false)
{
    expectedSha256 ??= Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath)));
    var start = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "powershell.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in new[]
             {
                 "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                  "-File", script, "-WaitPids", int.MaxValue.ToString(
                      System.Globalization.CultureInfo.InvariantCulture),
                  "-ZipPath", zipPath, "-ExpectedSha256", expectedSha256,
                  "-InstallDirectory", installDirectory,
                 "-RestartExecutable", restartExecutable,
             })
        start.ArgumentList.Add(argument);
    if (skipRestart)
        start.ArgumentList.Add("-SkipRestart");
    using var process = System.Diagnostics.Process.Start(start) ??
        throw new InvalidOperationException("Windows PowerShell test process did not start.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    return (process.ExitCode, await stdout + await stderr);
}

static HttpResponseMessage HttpResponse(HttpRequestMessage request,
    HttpContent content) => new(System.Net.HttpStatusCode.OK)
{
    RequestMessage = request,
    Content = content,
};

static HttpResponseMessage RangeResponse(HttpRequestMessage request,
    byte[] content)
{
    var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
    {
        RequestMessage = request,
        Content = new ByteArrayContent(content),
    };
    response.Content.Headers.ContentRange =
        new System.Net.Http.Headers.ContentRangeHeaderValue(
            0, content.LongLength - 1, content.LongLength);
    return response;
}

var localizationDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "App", "Localization"));
XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
foreach (var localizationPath in Directory.GetFiles(
             localizationDirectory, "Strings.*.xaml"))
{
    var localization = XDocument.Load(localizationPath);
    var duplicateKeys = localization.Descendants()
        .Select(element => (string?)element.Attribute(xaml + "Key"))
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .GroupBy(key => key!, StringComparer.Ordinal)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToArray();
    Equal(0, duplicateKeys.Length,
        $"localization resource keys are unique in {Path.GetFileName(localizationPath)}");
    var navigationFont = localization.Descendants()
        .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"),
            "NavigationTextFontFamily", StringComparison.Ordinal)).Value.Trim();
    var localizationFileName = Path.GetFileName(localizationPath);
    var expectedNavigationFont = localizationFileName.Contains(
            "zh-CN", StringComparison.OrdinalIgnoreCase)
        ? "Microsoft YaHei UI"
        : localizationFileName.Contains("zh-HK", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft JhengHei UI"
            : "Segoe UI";
    Equal(expectedNavigationFont, navigationFont,
        $"navigation font matches the interface language in {Path.GetFileName(localizationPath)}");
    var noPingRecovery = localization.Descendants()
        .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"),
            "CaptureNoPingRecovery", StringComparison.Ordinal)).Value;
    Equal(true,
        noPingRecovery.Contains("Restart", StringComparison.OrdinalIgnoreCase) ||
        noPingRecovery.Contains("重启", StringComparison.Ordinal) ||
        noPingRecovery.Contains("重新啟動", StringComparison.Ordinal),
        $"no-PING recovery asks the user to restart in {Path.GetFileName(localizationPath)}");
    Equal(true,
        noPingRecovery.Contains("MFi", StringComparison.OrdinalIgnoreCase),
        $"no-PING recovery recommends an original or MFi cable in {Path.GetFileName(localizationPath)}");
    var driverSafetyBlocked = localization.Descendants()
        .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"),
            "CaptureDriverSafetyBlocked", StringComparison.Ordinal)).Value;
    Equal(true,
        driverSafetyBlocked.Contains("USB", StringComparison.OrdinalIgnoreCase) &&
        driverSafetyBlocked.Contains("libusb0", StringComparison.OrdinalIgnoreCase),
        $"driver safety guidance explains conservative handling without blocking capture in {Path.GetFileName(localizationPath)}");
    var unsafeDriverStatus = localization.Descendants()
        .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"),
            "DriverUnsafeAppleStack", StringComparison.Ordinal)).Value;
    Equal(false, string.IsNullOrWhiteSpace(unsafeDriverStatus),
        $"unsafe Apple USB stack has a localized status in {Path.GetFileName(localizationPath)}");
    var stopUsbRestoreWarning = localization.Descendants()
        .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"),
            "StopUsbRestoreWarningFormat", StringComparison.Ordinal)).Value;
    Equal(true, stopUsbRestoreWarning.Contains("USB", StringComparison.OrdinalIgnoreCase) &&
        stopUsbRestoreWarning.Contains("{0}", StringComparison.Ordinal),
        $"USB restore warning is localized and includes diagnostic text in {Path.GetFileName(localizationPath)}");
    var usbRecovery = localization.Descendants()
        .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"),
            "CaptureUsbConfigurationRecovery", StringComparison.Ordinal)).Value;
    Equal(true,
        usbRecovery.Contains("restart", StringComparison.OrdinalIgnoreCase) ||
        usbRecovery.Contains("重启", StringComparison.Ordinal) ||
        usbRecovery.Contains("重新啟動", StringComparison.Ordinal),
        $"USB recovery asks the user to restart in {Path.GetFileName(localizationPath)}");
    Equal(true,
        usbRecovery.Contains("cable", StringComparison.OrdinalIgnoreCase) ||
        usbRecovery.Contains("数据线", StringComparison.Ordinal) ||
        usbRecovery.Contains("傳輸線", StringComparison.Ordinal),
        $"USB recovery asks the user to replace/reconnect a cable in {Path.GetFileName(localizationPath)}");
}

Equal(LocalizationService.TraditionalChineseHongKong,
    LocalizationService.ResolveCultureName("zh-HK"),
    "Hong Kong system culture selects the Hong Kong dictionary");
Equal(LocalizationService.TraditionalChineseHongKong,
    LocalizationService.ResolveCultureName("zh-Hant-TW"),
    "other Traditional Chinese cultures prefer the Hong Kong dictionary");
Equal(LocalizationService.TraditionalChineseHongKong,
    LocalizationService.ResolveCultureName("zh-CHT"),
    "legacy Traditional Chinese culture selects the Hong Kong dictionary");
Equal(LocalizationService.SimplifiedChinese,
    LocalizationService.ResolveCultureName("zh-SG"),
    "other Chinese cultures select the Simplified Chinese dictionary");
Equal(LocalizationService.English,
    LocalizationService.ResolveCultureName("fr-FR"),
    "unsupported system cultures use the English dictionary");

var captureRecoveryWindowPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "App", "Windows", "CaptureRecoveryWindow.xaml"));
var captureRecoveryWindow = XDocument.Load(captureRecoveryWindowPath);
Equal("Round", (string?)captureRecoveryWindow.Root?.Attribute("WindowCornerPreference"),
    "capture-recovery window keeps the native Windows corner preference");
Equal(false, captureRecoveryWindow.Descendants()
        .Any(element => string.Equals((string?)element.Attribute("Style"),
            "{StaticResource ModernDialogSurface}", StringComparison.Ordinal)),
    "capture-recovery window does not add a second custom rounded surface");
foreach (var actionKey in new[]
         {
             "CaptureNoPingRestartAction", "CaptureNoPingCableAction",
         })
{
    var action = captureRecoveryWindow.Descendants()
        .SingleOrDefault(element => string.Equals((string?)element.Attribute("Text"),
            $"{{DynamicResource {actionKey}}}", StringComparison.Ordinal));
    Equal(true, action is not null, $"no-PING window contains {actionKey}");
    Equal(true, double.TryParse((string?)action?.Attribute("FontSize"),
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var fontSize) &&
               fontSize >= 18,
        $"{actionKey} remains visually prominent");
    Equal("SemiBold", (string?)action?.Attribute("FontWeight"),
        $"{actionKey} remains emphasized");
}

var mainWindowPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "App", "MainWindow.xaml"));
var mainWindow = XDocument.Load(mainWindowPath);
var defaultWindowLayout = WindowWorkAreaController.CalculateLayout(
    currentLeft: 0, currentTop: 0, currentWidth: 1540, currentHeight: 900,
    workLeft: 0, workTop: 0, workWidth: 2560, workHeight: 1400,
    dpi: 96, designMinWidth: 1280, designMinHeight: 700, center: true);
Equal(510, defaultWindowLayout.Left,
    "main window centers inside a large 100 percent work area");
Equal(250, defaultWindowLayout.Top,
    "main window centers vertically inside a large work area");
Equal(1540, defaultWindowLayout.Width,
    "main window keeps its design width when it fits");
Equal(900, defaultWindowLayout.Height,
    "main window keeps its design height when it fits");
Equal(1280d, defaultWindowLayout.MinWidth,
    "main window keeps its design minimum width when it fits");
Equal(700d, defaultWindowLayout.MinHeight,
    "main window keeps its design minimum height when it fits");

var highDpiWindowLayout = WindowWorkAreaController.CalculateLayout(
    currentLeft: -580, currentTop: -380, currentWidth: 3080, currentHeight: 1800,
    workLeft: 0, workTop: 0, workWidth: 1920, workHeight: 1040,
    dpi: 192, designMinWidth: 1280, designMinHeight: 700, center: true);
Equal(192, highDpiWindowLayout.Left,
    "80 percent high-DPI window is centered horizontally");
Equal(104, highDpiWindowLayout.Top,
    "80 percent high-DPI window is centered vertically");
Equal(1536, highDpiWindowLayout.Width,
    "high-DPI window width is capped at 80 percent of the work area");
Equal(832, highDpiWindowLayout.Height,
    "high-DPI window height is capped at 80 percent of the work area");
Equal(768d, highDpiWindowLayout.MinWidth,
    "minimum width cannot force a 200 percent window above the 80 percent cap");
Equal(416d, highDpiWindowLayout.MinHeight,
    "minimum height cannot force a 200 percent window above the 80 percent cap");

var secondaryMonitorLayout = WindowWorkAreaController.CalculateLayout(
    currentLeft: -2200, currentTop: 200, currentWidth: 1200, currentHeight: 800,
    workLeft: -2560, workTop: 0, workWidth: 2560, workHeight: 1400,
    dpi: 144, designMinWidth: 1280, designMinHeight: 700, center: false);
Equal(-2200, secondaryMonitorLayout.Left,
    "negative-coordinate monitor preserves an already valid horizontal position");
Equal(200, secondaryMonitorLayout.Top,
    "an already valid window is not needlessly repositioned after a DPI change");
Equal(1200, secondaryMonitorLayout.Width,
    "an already smaller window is never enlarged by work-area fitting");
Equal(800d, secondaryMonitorLayout.MinWidth,
    "high-DPI minimum width follows an already smaller window");
Equal(533.3333333333334d, secondaryMonitorLayout.MinHeight,
    "high-DPI minimum height follows an already smaller window");
var previewQuickActions = mainWindow.Descendants()
    .SingleOrDefault(element =>
        string.Equals((string?)element.Attribute(xaml + "Name"),
            "PreviewQuickActions", StringComparison.Ordinal));
var previewToolbar = mainWindow.Descendants()
    .SingleOrDefault(element =>
        string.Equals((string?)element.Attribute(xaml + "Name"),
            "EnvironmentPanel", StringComparison.Ordinal));
Equal("{Binding PreviewAndObsVisibility}",
    (string?)previewQuickActions?.Attribute("Visibility"),
    "preview quick actions follow the active projection session");
Equal(true, previewToolbar?.Descendants().Any(element =>
        string.Equals(element.Name.LocalName, "DataTrigger", StringComparison.Ordinal) &&
        string.Equals((string?)element.Attribute("Binding"),
            "{Binding PreviewAndObsVisibility}", StringComparison.Ordinal) &&
        string.Equals((string?)element.Attribute("Value"), "Visible",
            StringComparison.Ordinal)) == true,
    "the complete preview toolbar is collapsed when the selected device is not mirroring");
Equal("0,0,0,14", (string?)previewToolbar?.Attribute("Margin"),
    "the active preview toolbar owns its spacing so collapsing it removes the gap");
Equal(false, mainWindow.Descendants().Any(element =>
        string.Equals((string?)element.Attribute(xaml + "Name"),
            "EnvironmentGapRow", StringComparison.Ordinal)),
    "no standalone toolbar gap remains above an idle preview");
Equal(false, mainWindow.Descendants().Any(element =>
        string.Equals((string?)element.Attribute("Text"), "{Binding EnvironmentStatus}",
            StringComparison.Ordinal)),
    "the preview toolbar does not show passive environment-probe status text");
Equal(false, mainWindow.Descendants().Any(element =>
        string.Equals((string?)element.Attribute("AutomationProperties.AutomationId"),
            "DecoderStatusText", StringComparison.Ordinal)),
    "the main statistics bar does not show the passive decoder status row");
foreach (var automationId in new[]
         {
             "QuickImageSettingsButton", "QuickPreviewWindowButton",
             "QuickScreenshotButton", "QuickFullScreenButton",
         })
{
    Equal(true, mainWindow.Descendants().Any(element =>
            string.Equals((string?)element.Attribute("AutomationProperties.AutomationId"),
                automationId, StringComparison.Ordinal)),
        $"preview quick actions contain {automationId}");
}
Equal(false, mainWindow.Descendants().Any(element =>
        string.Equals((string?)element.Attribute("AutomationProperties.AutomationId"),
            "QuickRefreshPreviewButton", StringComparison.Ordinal)),
    "preview quick actions omit the redundant refresh button");

var sourceDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", ".."));
var mainViewModelSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "ViewModels", "MainViewModel.cs"));
var mainWindowSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "MainWindow.xaml.cs"));
var previewWindowSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Windows", "NativePreviewWindow.cs"));
var multiDevicePreviewSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Services", "MultiDevicePreviewManager.cs"));
var nativePreviewHostSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Controls", "NativePreviewHost.cs"));
var previewAttachmentCoordinatorSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Controls", "PreviewAttachmentCoordinator.cs"));
Equal(true, mainWindowSource.Contains("MainPreviewHost.Deactivate();",
        StringComparison.Ordinal) &&
    nativePreviewHostSource.Contains(
        "PreviewAttachmentCoordinator.Deactivate(_window);",
        StringComparison.Ordinal) &&
    previewAttachmentCoordinatorSource.Contains(
        "if (wasActive) NativeCore.DetachPreviewWindow();",
        StringComparison.Ordinal),
    "hidden main preview detaches its native renderer and can later reactivate");
Equal(true,
    mainWindowSource.Contains("SetFullScreenPreviewBackground(true)",
        StringComparison.Ordinal) &&
    mainWindowSource.Contains("MediaCastVideoHost.Background = Brushes.Black;",
        StringComparison.Ordinal) &&
    mainWindowSource.Contains("MainPreviewHost.IsFullScreenPresentation = _isFullScreen;",
        StringComparison.Ordinal) &&
    nativePreviewHostSource.Contains("internal bool IsFullScreenPresentation",
        StringComparison.Ordinal) &&
    nativePreviewHostSource.Contains("SetWindowRgn(_window, 0, true)",
        StringComparison.Ordinal),
    "full-screen previews use black WPF fill and a rectangular native surface");
Equal(true, WindowsAutoPlayGuard.ShouldCancel(
        WindowsAutoPlayGuard.QueryCancelAutoPlayMessage, captureActive: true),
    "active capture cancels Windows AutoPlay device claims");
Equal(false, WindowsAutoPlayGuard.ShouldCancel(
        WindowsAutoPlayGuard.QueryCancelAutoPlayMessage, captureActive: false),
    "idle application leaves Windows AutoPlay unchanged");
Equal(false, WindowsAutoPlayGuard.ShouldCancel(0x0219, captureActive: true),
    "ordinary device-change messages are not swallowed as AutoPlay");

var protectedWithoutAudio = ProtectedContentStatus.Parse(
    ProtectedContentStatus.AudioInactiveMarker, 48000, 2);
Equal(true, protectedWithoutAudio.IsProtected,
    "protected content marker is recognized without audio");
Equal(false, protectedWithoutAudio.AudioActive,
    "protected content marker preserves missing audio activity");
var protectedWithAudio = ProtectedContentStatus.Parse(
    ProtectedContentStatus.AudioActiveMarker, 48000, 2);
Equal(true, protectedWithAudio.IsProtected && protectedWithAudio.AudioActive,
    "protected content marker reports recent audio samples independently");
Equal(false, ProtectedContentStatus.Parse("投屏中", 48000, 2).IsProtected,
    "ordinary streaming status is not classified as protected content");
Equal(true, mainViewModelSource.Contains(
        "IsVideoProtected => CurrentDeviceSession?.VideoProtected == true",
        StringComparison.Ordinal),
    "protected state follows the selected session instead of leaking across sources");
Equal(true, mainViewModelSource.Contains(
        "UpdateProtectionState(state, ProtectedContentStatus.Parse(",
        StringComparison.Ordinal) &&
    multiDevicePreviewSource.Contains("DeviceProtectionStateChanged +=",
        StringComparison.Ordinal) &&
    multiDevicePreviewSource.Contains("window.SetProtectedContent(",
        StringComparison.Ordinal),
    "background sessions propagate protected state to independent previews");
Equal(true, mainWindowSource.Contains("WM_QUERYCANCELAUTOPLAY",
        StringComparison.Ordinal) &&
    mainWindowSource.Contains("AddHook(WindowMessageHook)",
        StringComparison.Ordinal) &&
    mainViewModelSource.Contains("HasAnyCaptureSession",
        StringComparison.Ordinal),
    "main window installs the AutoPlay cancellation hook");
Equal(true, previewWindowSource.Contains("autoplay_cancelled",
        StringComparison.Ordinal) &&
    previewWindowSource.Contains("_sessionHandle != 0",
        StringComparison.Ordinal),
    "active native previews cancel AutoPlay even when foreground");
var driverManagerSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Services", "DriverManagerLauncher.cs"));
Equal(true, driverManagerSource.Contains("UseShellExecute = false",
        StringComparison.Ordinal) &&
    driverManagerSource.Contains("Path.GetExtension(executablePath)",
        StringComparison.Ordinal),
    "automatic driver-manager launches cannot be redirected by file associations");
var aboutWindowSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Windows", "AboutWindow.xaml.cs"));
Equal(true, aboutWindowSource.Contains("StartExplorer(path, selectFile: true)",
        StringComparison.Ordinal) &&
    aboutWindowSource.Contains("Uri.UriSchemeHttp", StringComparison.Ordinal) &&
    aboutWindowSource.Contains("UseShellExecute = false", StringComparison.Ordinal),
    "local license and changelog targets bypass document file associations");
Equal(true, mainViewModelSource.Contains(
        "PreviewAndObsVisibility => CurrentSessionHandle != 0",
        StringComparison.Ordinal),
    "a session being cleaned up is not treated as a visible preview toolbar session");
Equal(false, mainViewModelSource.Contains(".IsLibUsb0DeviceAvailable(",
        StringComparison.Ordinal),
    "automatic UI refresh never enters the exact legacy USB probe");
Equal(false, UsbDeviceRefreshPolicy.ShouldEnumerateWiredDevices(false,
        [CaptureState.WaitingForDevice]),
    "wired discovery is suspended while USB re-enumerates");
Equal(false, UsbDeviceRefreshPolicy.ShouldEnumerateWiredDevices(true,
        [CaptureState.Streaming]),
    "managed start or stop ownership suspends wired discovery");
Equal(true, UsbDeviceRefreshPolicy.ShouldEnumerateWiredDevices(false,
        [CaptureState.Streaming]),
    "wired discovery resumes after the session reaches streaming");
Equal(false, UsbDeviceRefreshPolicy.ShouldRefreshMetadata(false, false),
    "automatic wired polling reuses cached Lockdown metadata");
Equal(true, UsbDeviceRefreshPolicy.ShouldRefreshMetadata(true, false),
    "an explicit idle refresh may update Lockdown metadata");
Equal(false, UsbDeviceRefreshPolicy.ShouldRefreshMetadata(true, true),
    "an explicit refresh cannot open Lockdown while a wired session exists");
var captureStartIndex = mainViewModelSource.IndexOf(
    "private async Task StartAsync()", StringComparison.Ordinal);
var captureReuseIndex = captureStartIndex >= 0
    ? mainViewModelSource.IndexOf(
        "capture_start_reused", captureStartIndex, StringComparison.Ordinal)
    : -1;
var capturePreflightIndex = captureStartIndex >= 0
    ? mainViewModelSource.IndexOf(
        "EnsureSourceReadyAsync(device)", captureStartIndex, StringComparison.Ordinal)
    : -1;
Equal(true, captureStartIndex >= 0 && captureReuseIndex > captureStartIndex &&
            capturePreflightIndex > captureReuseIndex,
    "main start reuses a session created by an independent window before USB preflight");
Equal(true, mainViewModelSource.Contains(
        "IPhoneFilterDriverState.UnsafeStack => LocalizationService.Get(\"DriverUnsafeAppleStack\")",
        StringComparison.Ordinal),
    "unsafe Apple USB filter stacks have a dedicated visible driver state");
Equal(true, mainViewModelSource.Contains(
        "driver safety warning: {driverStatus.Diagnostic}", StringComparison.Ordinal) &&
    mainViewModelSource.Contains(
        "CaptureErrorGuidance.StartFailureMessage(\n                            preflight.ErrorCode",
        StringComparison.Ordinal),
    "wired preflight records the filter warning while preserving start error propagation");
Equal(true, mainViewModelSource.Contains(
        "? CaptureFailureKind.UsbConnection\n            : CaptureFailureKind.Driver",
        StringComparison.Ordinal),
    "managed USB preflight distinguishes a missing device from a driver failure");
var stopMethodIndex = mainViewModelSource.IndexOf(
    "private async Task StopAsync()", StringComparison.Ordinal);
var restoreWarningCatchIndex = stopMethodIndex >= 0
    ? mainViewModelSource.IndexOf(
        "catch (UsbConfigurationRestoreWarningException warning)",
        stopMethodIndex, StringComparison.Ordinal)
    : -1;
var stopGenericCatchIndex = restoreWarningCatchIndex >= 0
    ? mainViewModelSource.IndexOf("catch (Exception error)",
        restoreWarningCatchIndex, StringComparison.Ordinal)
    : -1;
Equal(true, restoreWarningCatchIndex > stopMethodIndex &&
        stopGenericCatchIndex > restoreWarningCatchIndex &&
        mainViewModelSource.Contains("StopUsbRestoreWarningFormat",
            StringComparison.Ordinal),
    "USB restore confirmation warnings complete Stop without using the generic error prompt");
Equal(true, stopMethodIndex >= 0 &&
        mainViewModelSource.IndexOf("requestedState.IsStopping = true;", stopMethodIndex,
            StringComparison.Ordinal) > stopMethodIndex &&
        mainViewModelSource.IndexOf("NativeCore.SelectPreviewSession(0);", stopMethodIndex,
            StringComparison.Ordinal) > stopMethodIndex &&
        mainViewModelSource.IndexOf("CaptureCleaningDevice", stopMethodIndex,
            StringComparison.Ordinal) > stopMethodIndex,
    "stop hides the preview and presents device cleanup before native USB teardown");
Equal(true, mainViewModelSource.Contains(
        "private static bool IsSessionPresentable(DeviceCaptureState? session)",
        StringComparison.Ordinal) &&
    mainViewModelSource.Contains(
        "session is { HasSession: true, IsStopping: false }", StringComparison.Ordinal) &&
    mainViewModelSource.Contains("OnPropertyChanged(nameof(CurrentSessionHandle));",
        StringComparison.Ordinal),
    "selected-device transitions do not present a session that is being cleaned up");
Equal(false, mainViewModelSource.Contains("mainWindow.IsEnabled = false;",
        StringComparison.Ordinal) ||
    mainViewModelSource.Contains("mainWindow.IsEnabled = true;", StringComparison.Ordinal),
    "image adjustments keep the main window visually enabled while serializing settings");
Equal(true, mainViewModelSource.Contains(
        "private bool CanQueueSessionLifecycleOperation(DeviceViewModel? device)",
        StringComparison.Ordinal) &&
    mainViewModelSource.Contains(
        "(!IsBusy || HasSessionLifecycleOperationInProgress)", StringComparison.Ordinal) &&
    mainViewModelSource.Contains("TryBeginSessionLifecycleOperation(requestedDevice.Udid)",
        StringComparison.Ordinal) &&
    mainViewModelSource.Contains("EndSessionLifecycleOperation(requestedState.Udid)",
        StringComparison.Ordinal),
    "a lifecycle operation on one device queues rather than disables the selected other device");
Equal(true, captureStartIndex >= 0 &&
    mainViewModelSource.Contains("var device = requestedDevice;", StringComparison.Ordinal) &&
    mainViewModelSource.Contains("? \"CaptureQueued\" : \"StartRequested\"", StringComparison.Ordinal),
    "a queued start remains bound to the clicked device and immediately reports that it is waiting");
var repositoryRoot = Path.GetFullPath(Path.Combine(sourceDirectory, ".."));
var rootCMake = File.ReadAllText(Path.Combine(repositoryRoot, "CMakeLists.txt"));
Equal(true, rootCMake.Contains(
        "IPHONEMIRROR_BUILD_DANGEROUS_USB_TOOLS", StringComparison.Ordinal) &&
    rootCMake.Contains(
        "Build tools that issue real Apple USB configuration and bulk requests\" OFF",
        StringComparison.Ordinal),
    "real-device USB stress tools are disabled in default builds");
var installerScript = File.ReadAllText(Path.Combine(repositoryRoot,
    "installer", "iPhoneMirror.iss"));
Equal(true, installerScript.Contains(
        "CloseApplicationsFilter=iPhoneMirror.exe,iPhoneMirror.Driver.exe",
        StringComparison.Ordinal),
    "installer closes the main app and shared-runtime driver manager");
Equal(false, installerScript.Split('\n').Any(line =>
        line.TrimStart().StartsWith("Flags:", StringComparison.OrdinalIgnoreCase) &&
        line.Contains("restartreplace", StringComparison.OrdinalIgnoreCase)),
    "installer never defers shared runtime replacement until reboot");
Equal(true, installerScript.Contains("[InstallDelete]", StringComparison.Ordinal) &&
            installerScript.Contains(
                "{app}\\tools\\ffmpeg\\ffmpeg.exe", StringComparison.Ordinal),
    "installer removes legacy media-output FFmpeg files before upgrade");
Equal(true, installerScript.Contains(
        "{userprograms}\\{#MyAppName}", StringComparison.Ordinal) &&
    installerScript.Contains("IsAdminInstallMode", StringComparison.Ordinal) &&
    installerScript.Contains("DelTree(ExpandConstant", StringComparison.Ordinal),
    "all-users installs remove a shadowing per-user shortcut group");
Equal(true, installerScript.Contains(
        "Software\\Classes\\AppUserModelId\\{#MyAppUserModelId}",
        StringComparison.Ordinal) &&
    installerScript.Contains("ValueName: \"IconUri\"", StringComparison.Ordinal),
    "installer registers a stable icon source for the application identity");
Equal(true, installerScript.Contains("{param:STARTAPP|0}", StringComparison.Ordinal) &&
            installerScript.Contains("Sleep(1000)", StringComparison.Ordinal),
    "installer restarts only after a post-install handle-release delay");
var buildInstallerScript = File.ReadAllText(Path.Combine(repositoryRoot,
    "scripts", "build_installer.ps1"));
Equal(true, buildInstallerScript.Contains(
        "outputs\\iPhoneMirror.Installer", StringComparison.Ordinal),
    "standalone installer build defaults to the shared-runtime payload");
Equal(true, buildInstallerScript.Contains(
        "iPhoneMirror.Driver.exe", StringComparison.Ordinal),
    "standalone installer build requires the driver manager executable");
var zipUpdateScript = File.ReadAllText(Path.Combine(sourceDirectory, "App",
    "tools", "updater", "Apply-ZipUpdate.ps1"));
Equal(true, zipUpdateScript.Contains("[string]$WaitPids", StringComparison.Ordinal) &&
            zipUpdateScript.Contains("iPhoneMirror.Driver.exe", StringComparison.Ordinal),
    "portable updates wait for the independent driver manager before copying");
Equal(true, zipUpdateScript.Contains("Rollback was incomplete", StringComparison.Ordinal) &&
            zipUpdateScript.Contains("[Security.Cryptography.SHA256]::Create()",
                StringComparison.Ordinal) &&
            zipUpdateScript.Contains("$changes", StringComparison.Ordinal) &&
            zipUpdateScript.Contains("$restartLock", StringComparison.Ordinal) &&
            zipUpdateScript.Contains("Start-RestartProcess", StringComparison.Ordinal) &&
            zipUpdateScript.Contains("Shell.Application", StringComparison.Ordinal) &&
            zipUpdateScript.Contains("New-PrivilegedDirectory $destination",
                StringComparison.Ordinal) &&
            zipUpdateScript.Contains("Enable-DirectoryInheritance $directory",
                StringComparison.Ordinal) &&
            zipUpdateScript.Contains("Sort-Object Length -Descending",
                StringComparison.Ordinal) &&
            zipUpdateScript.Contains("if ($fileChangesCommitted)",
                StringComparison.Ordinal),
    "portable updates protect new topology, verify files, drop elevation, and roll back failures");
var virtualCameraServiceCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Services", "VirtualCameraService.cs"));
var appProjectCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "iPhoneMirror.App.csproj"));
Equal(true, virtualCameraServiceCode.Contains("ElevationPathLock.Acquire(helper, mediaSource)",
                StringComparison.Ordinal) &&
            virtualCameraServiceCode.Contains("GetManifestResourceStream(resourceName)",
                StringComparison.Ordinal) &&
            virtualCameraServiceCode.Contains("CommonApplicationData",
                StringComparison.Ordinal) &&
            virtualCameraServiceCode.Contains("SetAccessRuleProtection($true, $false)",
                StringComparison.Ordinal) &&
            virtualCameraServiceCode.Contains("Copy-VerifiedPayload $payload.HelperPath",
                StringComparison.Ordinal) &&
            virtualCameraServiceCode.Contains("Start-Process -FilePath $helper",
                StringComparison.Ordinal) &&
            appProjectCode.Contains("IPhoneMirror.App.Payload.iPhoneMirror.VirtualCamera.Admin.exe",
                StringComparison.Ordinal) &&
            appProjectCode.Contains("IPhoneMirror.App.Payload.iPhoneMirror.VirtualCamera.dll",
                StringComparison.Ordinal),
    "virtual camera elevation copies locked embedded payloads into an admin-only directory");
Equal(false, zipUpdateScript.Contains("Get-FileHash", StringComparison.Ordinal),
    "portable update verification does not depend on optional PowerShell cmdlets");
Equal(false, zipUpdateScript.Contains("[IO.Path]::GetRelativePath",
        StringComparison.Ordinal),
    "portable update script avoids APIs unavailable in Windows PowerShell 5.1");
var releaseManifestPath = Path.Combine(sourceDirectory, "..", "updates", "releases.json");
var repositoryRelease = ReleaseParser.ParseLatest(
    File.ReadAllText(releaseManifestPath), includeStable: true, includePrerelease: true);
var appProject = XDocument.Load(Path.Combine(sourceDirectory, "App",
    "iPhoneMirror.App.csproj"));
var appVersion = appProject.Descendants("Version").Single().Value.Trim();
Equal(true, SemanticVersion.TryParse(appVersion, out var parsedAppVersion),
    "application project declares a valid semantic version");
Equal(true, repositoryRelease is not null &&
            repositoryRelease.Version <= parsedAppVersion,
    "repository update manifest does not advertise a version newer than the application");
Equal(true, repositoryRelease?.PreferredAsset?.Sha256 is not null,
    "repository update manifest pins the preferred asset SHA256 digest");
var sharedUiDirectory = Path.Combine(sourceDirectory, "SharedUI");
var lightThemePath = Path.Combine(sharedUiDirectory, "Themes", "LightTheme.xaml");
var darkThemePath = Path.Combine(sharedUiDirectory, "Themes", "DarkTheme.xaml");
static string[] ResourceKeys(string path, XNamespace xamlNamespace) =>
    XDocument.Load(path).Descendants()
        .Select(element => (string?)element.Attribute(xamlNamespace + "Key"))
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Select(key => key!)
        .OrderBy(key => key, StringComparer.Ordinal)
        .ToArray();

var lightThemeKeys = ResourceKeys(lightThemePath, xaml);
var darkThemeKeys = ResourceKeys(darkThemePath, xaml);
Sequence(lightThemeKeys, darkThemeKeys,
    "light and dark themes expose the same semantic resources");
static string ResourceColor(string path, XNamespace xamlNamespace, string key) =>
    (string?)XDocument.Load(path).Descendants()
        .Single(element =>
            string.Equals((string?)element.Attribute(xamlNamespace + "Key"), key,
                StringComparison.Ordinal))
        .Attribute("Color") ??
    throw new InvalidOperationException($"Theme resource {key} does not define a color.");

foreach (var blueActionTextBrush in new[]
         {
             "PrimaryActionTextBrush", "AboutCheckUpdatesTextBrush",
             "CaptureActionTextBrush",
         })
{
    Equal("#FFFFFFFF", ResourceColor(lightThemePath, xaml, blueActionTextBrush),
        $"light theme keeps {blueActionTextBrush} white on blue actions");
}

static double RelativeLuminance(string color)
{
    var hex = color.TrimStart('#');
    if (hex.Length == 8) hex = hex[2..];
    var channels = Enumerable.Range(0, 3).Select(index =>
    {
        var value = Convert.ToInt32(hex.Substring(index * 2, 2), 16) / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }).ToArray();
    return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

static double ContrastRatio(string foreground, string background)
{
    var first = RelativeLuminance(foreground);
    var second = RelativeLuminance(background);
    return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
}

foreach (var theme in new[]
         {
             (Name: "light", Path: lightThemePath, Background: "#FFF3F3F5"),
             (Name: "dark", Path: darkThemePath, Background: "#FF1F1F1F"),
         })
{
    foreach (var statusBrush in new[] { "SuccessBrush", "WarningBrush", "ErrorBrush" })
    {
        Equal(true, ContrastRatio(ResourceColor(theme.Path, xaml, statusBrush), theme.Background) >= 4.5,
            $"{theme.Name} {statusBrush} meets normal-text contrast");
    }
}
foreach (var theme in new[]
         {
             (Name: "light", Path: lightThemePath),
             (Name: "dark", Path: darkThemePath),
         })
{
    foreach (var pair in new[]
             {
                 (Foreground: "PrimaryActionTextBrush", Background: "PrimaryActionBrush"),
                 (Foreground: "PrimaryActionTextBrush", Background: "PrimaryActionHoverBrush"),
                 (Foreground: "PrimaryActionTextBrush", Background: "PrimaryActionPressedBrush"),
                 (Foreground: "PrimaryActionDisabledTextBrush", Background: "PrimaryActionDisabledBrush"),
                 (Foreground: "CaptureActionTextBrush", Background: "CaptureStartBrush"),
                 (Foreground: "CaptureActionTextBrush", Background: "CaptureStartHoverBrush"),
                 (Foreground: "CaptureActionTextBrush", Background: "CaptureStopBrush"),
                 (Foreground: "CaptureActionTextBrush", Background: "CaptureStopHoverBrush"),
                 (Foreground: "DangerButtonTextBrush", Background: "DangerBrush"),
                 (Foreground: "DangerButtonTextBrush", Background: "DangerHoverBrush"),
                 (Foreground: "DangerButtonTextBrush", Background: "DangerPressedBrush"),
             })
    {
        Equal(true,
            ContrastRatio(ResourceColor(theme.Path, xaml, pair.Foreground),
                ResourceColor(theme.Path, xaml, pair.Background)) >= 4.5,
            $"{theme.Name} {pair.Foreground} on {pair.Background} meets button-text contrast");
    }
}
foreach (var requiredThemeKey in new[]
         {
              "AppBackgroundBrush", "SidebarBrush", "CardBrush", "CardHoverBrush",
               "ControlFillBrush", "ControlHoverBrush", "AccentBrush", "OnAccentBrush",
               "IconButtonHoverBrush", "IconButtonPressedBrush",
              "SuccessBrush", "WarningBrush", "ErrorBrush", "WarningSurfaceBrush",
              "ErrorSurfaceBrush", "PreviewChromeBrush", "PreviewPanelAltBrush",
              "PreviewBorderBrush", "PreviewTextBrush", "PreviewMutedTextBrush",
             "CaptureStartBrush", "CaptureStopBrush", "CaptureActionTextBrush",
             "PrimaryActionBrush", "PrimaryActionHoverBrush",
             "PrimaryActionPressedBrush", "PrimaryActionTextBrush",
             "AboutCheckUpdatesTextBrush",
              "PrimaryActionFocusBrush", "PrimaryActionDisabledBrush",
              "PrimaryActionDisabledTextBrush",
              "MediaPlayerScrimBrush", "MediaPlayerControlFillBrush",
              "MediaPlayerControlHoverBrush", "MediaPlayerControlPressedBrush",
              "MediaPlayerPrimaryBrush", "MediaPlayerPrimaryHoverBrush",
              "MediaPlayerPrimaryPressedBrush", "MediaPlayerPrimaryBorderBrush",
              "MediaPlayerPrimaryIconBrush",
              "MediaPlayerSecondaryTextBrush", "MediaPlayerTrackBrush",
              "MediaPlayerProgressBrush", "MediaPlayerThumbBrush",
              "MediaPlayerFocusBrush",
              "ScrollTrackBrush", "ScrollTrackHoverBrush", "ScrollThumbBrush",
             "ScrollThumbHoverBrush", "ScrollThumbPressedBrush",
         })
{
    Equal(true, lightThemeKeys.Contains(requiredThemeKey, StringComparer.Ordinal),
        $"theme contains {requiredThemeKey}");
}

var modernControlsPath = Path.Combine(sharedUiDirectory, "Controls",
    "ModernControls.xaml");
var modernControlsText = File.ReadAllText(modernControlsPath);
foreach (var reusableControl in new[]
         {
             "ModernDialogSurface", "SettingsSection", "IconButton",
              "TitleBarButton", "TitleBarCloseButton", "SubWindowCloseButton", "CornerRadius=\"8\"",
              "SymbolIcon",
              "SubWindowPageRoot", "SubWindowHeader", "SubWindowTitle",
               "SubWindowSubtitle", "SubWindowTabControl", "SubWindowTabItem",
               "IconButtonHoverBrush",
             "ModernVerticalScrollThumbStyle", "ModernHorizontalScrollThumbStyle",
             "ModernScrollBarStyle", "ContentEdgeScrollBarStyle",
             "ui:ModernButton", "ui:ModernCard", "ui:ModernDialog",
         })
{
    Equal(true, modernControlsText.Contains(reusableControl, StringComparison.Ordinal),
        $"shared UI contains {reusableControl}");
}
Equal(true,
    modernControlsText.Contains(
        "<Style x:Key=\"ModernScrollBarStyle\" TargetType=\"{x:Type ScrollBar}\">",
        StringComparison.Ordinal) &&
    modernControlsText.Contains(
        "BasedOn=\"{StaticResource ModernScrollBarStyle}\"/>",
        StringComparison.Ordinal) &&
    modernControlsText.Contains("<Setter Property=\"Width\" Value=\"6\"/>",
        StringComparison.Ordinal) &&
    modernControlsText.Contains("<Setter Property=\"Height\" Value=\"6\"/>",
        StringComparison.Ordinal) &&
    modernControlsText.Contains("CornerRadius=\"2\"", StringComparison.Ordinal) &&
    modernControlsText.Contains("MinHeight\" Value=\"28\"", StringComparison.Ordinal) &&
    modernControlsText.Contains("MinWidth\" Value=\"28\"", StringComparison.Ordinal) &&
    modernControlsText.Contains("Storyboard.TargetProperty=\"Width\"",
        StringComparison.Ordinal) &&
    modernControlsText.Contains("Storyboard.TargetProperty=\"Height\"",
        StringComparison.Ordinal) &&
    modernControlsText.Contains("To=\"4\"", StringComparison.Ordinal) &&
    modernControlsText.Contains("QuadraticEase", StringComparison.Ordinal) &&
    modernControlsText.Contains("PageLeftCommand", StringComparison.Ordinal) &&
    modernControlsText.Contains("PageRightCommand", StringComparison.Ordinal),
    "shared scrollbars are thin, rounded, animated, and support both orientations");
Equal(true, modernControlsText.Contains(
        "<Style x:Key=\"TitleBarButton\" TargetType=\"Button\" BasedOn=\"{StaticResource IconButton}\">",
        StringComparison.Ordinal) &&
    !modernControlsText.Contains("x:Name=\"CaptionRoot\"", StringComparison.Ordinal),
    "title-bar controls retain the shared icon-button visual treatment");
Equal(true,
    modernControlsText.Contains("x:Key=\"ButtonSymbolIcon\"",
        StringComparison.Ordinal) &&
    modernControlsText.Contains("AncestorType=Button", StringComparison.Ordinal) &&
    modernControlsText.Contains("Path=Foreground", StringComparison.Ordinal),
    "shared symbol icons follow their button foreground for theme contrast");
var titleBarWindowText = File.ReadAllText(mainWindowPath);
Equal(true, titleBarWindowText.Contains("TitleBarMinimizeToolTip", StringComparison.Ordinal) &&
    titleBarWindowText.Contains("TitleBarMaximizeToolTip", StringComparison.Ordinal) &&
    titleBarWindowText.Contains("TitleBarCloseToolTip", StringComparison.Ordinal) &&
    titleBarWindowText.Contains("<ToolTip FontFamily=\"{DynamicResource NavigationTextFontFamily}\"",
        StringComparison.Ordinal),
    "title-bar tooltips use descriptive text outside the Fluent icon font");
foreach (var appXamlPath in new[]
         {
             Path.Combine(sourceDirectory, "App", "App.xaml"),
             Path.Combine(sourceDirectory, "DriverInstaller", "App.xaml"),
         })
{
    var appXamlText = File.ReadAllText(appXamlPath);
    Equal(true, appXamlText.Contains("Controls/ModernControls.xaml",
            StringComparison.Ordinal),
        $"{Path.GetFileName(Path.GetDirectoryName(appXamlPath))} loads shared controls");
    Equal(false, appXamlText.Contains("TargetType=\"{x:Type ScrollBar}\"",
            StringComparison.Ordinal),
        $"{Path.GetFileName(Path.GetDirectoryName(appXamlPath))} does not override shared scrollbars");
}

var iconSourceDirectories = new[]
{
    Path.Combine(sourceDirectory, "App"),
    Path.Combine(sourceDirectory, "DriverInstaller"),
    Path.Combine(sourceDirectory, "SharedUI"),
};
var systemIconFontNames = new[]
{
    string.Concat("Segoe Fluent", " Icons"),
    string.Concat("Segoe MDL2", " Assets"),
};
var fontIconElementName = string.Concat("<ui:", "FontIcon");
foreach (var iconSourcePath in iconSourceDirectories.SelectMany(directory =>
             Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories))
         .Where(path => Path.GetExtension(path) is ".xaml" or ".cs" &&
                        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase) &&
                        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase)))
{
    var iconSourceText = File.ReadAllText(iconSourcePath);
    Equal(false,
        systemIconFontNames.Any(name => iconSourceText.Contains(name,
            StringComparison.OrdinalIgnoreCase)) ||
        iconSourceText.Contains(fontIconElementName, StringComparison.Ordinal) ||
        System.Text.RegularExpressions.Regex.IsMatch(iconSourceText,
            @"&#x[EeFf][0-9A-Fa-f]{3};|\\[ux][EeFf][0-9A-Fa-f]{3}|[\uE000-\uF8FF]"),
        $"{Path.GetRelativePath(sourceDirectory, iconSourcePath)} uses packaged semantic icons");
}

var themedWindowDirectories = new[]
{
    Path.Combine(sourceDirectory, "App", "Windows"),
    Path.Combine(sourceDirectory, "DriverInstaller", "Windows"),
};
foreach (var windowPath in themedWindowDirectories.SelectMany(directory =>
             Directory.GetFiles(directory, "*.xaml")))
{
    var windowDocument = XDocument.Load(windowPath);
    var windowRoot = windowDocument.Root ?? throw new InvalidOperationException(
        $"{Path.GetFileName(windowPath)} does not have a root element");
    var windowText = File.ReadAllText(windowPath);
    var windowName = Path.GetFileName(windowPath);
    Equal(false,
        windowRoot.Attribute("RenderTransform") is not null ||
        windowRoot.Elements().Any(element =>
            element.Name.LocalName.EndsWith(".RenderTransform",
                StringComparison.Ordinal)),
        $"{windowName} does not set a transform directly on a WPF Window");
    Equal(false, System.Text.RegularExpressions.Regex.IsMatch(windowText,
            @"#[0-9A-Fa-f]{6,8}"),
        $"{windowName} does not hard-code theme colors");
    Equal(true, windowText.Contains("{DynamicResource", StringComparison.Ordinal),
        $"{windowName} uses dynamic theme resources");
    Equal(true,
        windowText.Contains("AppBackgroundBrush", StringComparison.Ordinal) ||
        windowText.Contains("ModernDialogSurface", StringComparison.Ordinal),
        $"{windowName} uses a shared themed window surface");
    Equal(true,
        windowText.Contains("WindowStyle=\"None\"", StringComparison.Ordinal) ||
        windowText.StartsWith("<ui:FluentWindow", StringComparison.Ordinal),
        $"{windowName} uses custom or Fluent window chrome");
    Equal(true, windowText.Contains("SubWindowCloseButton", StringComparison.Ordinal),
        $"{windowName} provides the shared custom close button");
    Equal(true, windowText.Contains("SubWindowTitle", StringComparison.Ordinal),
        $"{windowName} uses the shared child-window title hierarchy");
    Equal(true,
        windowText.Contains("ModernDialogSurface", StringComparison.Ordinal) ||
        windowText.Contains("SubWindowPageRoot", StringComparison.Ordinal),
        $"{windowName} uses shared child-window spacing");
    Equal(true,
        windowText.Contains("ModernDialogSurface", StringComparison.Ordinal) ||
        windowText.Contains("WindowChrome.WindowChrome", StringComparison.Ordinal) ||
        windowText.StartsWith("<ui:FluentWindow", StringComparison.Ordinal),
        $"{windowName} uses a draggable custom window surface");
    Equal(true,
        windowText.Contains("PageTransition.IsEnabled", StringComparison.Ordinal) ||
        windowText.Contains("EntranceTransform", StringComparison.Ordinal),
        $"{windowName} has a restrained entrance transition");
}

var appPromptWindowText = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Windows", "AppPromptWindow.xaml"));
Equal(true,
    appPromptWindowText.Contains("WindowBackdropType=\"None\"", StringComparison.Ordinal) &&
    appPromptWindowText.Contains("Background=\"{DynamicResource WindowBackgroundBrush}\"",
        StringComparison.Ordinal) &&
    appPromptWindowText.Contains("Effect=\"{x:Null}\" CornerRadius=\"0\" BorderThickness=\"0\"",
        StringComparison.Ordinal),
    "application prompts use one native corner without an acrylic halo or outer shadow");
var appPromptWindowSource = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Windows", "AppPromptWindow.xaml.cs"));
var promptRenderedIndex = appPromptWindowSource.IndexOf("prompt.ContentRendered +=",
    StringComparison.Ordinal);
var promptAfterShownIndex = appPromptWindowSource.IndexOf("await afterShown();",
    promptRenderedIndex, StringComparison.Ordinal);
Equal(true, promptRenderedIndex >= 0 && promptAfterShownIndex > promptRenderedIndex,
    "prompt follow-up actions begin only after the warning content is rendered");

var resizableWindowPaths = themedWindowDirectories
    .SelectMany(directory => Directory.GetFiles(directory, "*.xaml"))
    .Concat(new[]
    {
        mainWindowPath,
        Path.Combine(sourceDirectory, "DriverInstaller", "MainWindow.xaml"),
    });
foreach (var windowPath in resizableWindowPaths)
{
    Equal(false,
        File.ReadAllText(windowPath).Contains(
            "ResizeMode=\"CanResizeWithGrip\"", StringComparison.Ordinal),
        $"{Path.GetFileName(windowPath)} hides the bottom-right resize grip");
}

var appWindowDirectory = Path.Combine(sourceDirectory, "App", "Windows");
foreach (var windowPath in themedWindowDirectories.SelectMany(directory =>
             Directory.GetFiles(directory, "*.xaml")))
{
    var windowText = File.ReadAllText(windowPath);
    if (!windowText.Contains("SubWindowHeader", StringComparison.Ordinal)) continue;
    Equal(true,
        windowText.Contains("WindowDragBehavior.IsEnabled=\"True\"",
            StringComparison.Ordinal) ||
        windowText.Contains("MouseLeftButtonDown=", StringComparison.Ordinal),
        $"{Path.GetFileName(windowPath)} exposes a draggable title region");
    if (windowText.Contains("SubWindowCloseButton", StringComparison.Ordinal))
    {
        Equal(true, windowText.Contains("Style=\"{StaticResource ButtonSymbolIcon}\"",
                StringComparison.Ordinal),
            $"{Path.GetFileName(windowPath)} uses the accessible child-window close glyph");
    }
}
var windowDragBehaviorText = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Animations", "WindowDragBehavior.cs"));
Equal(true,
    windowDragBehaviorText.Contains("window.DragMove()", StringComparison.Ordinal) &&
    windowDragBehaviorText.Contains("ResizeMode.CanResizeWithGrip",
        StringComparison.Ordinal),
    "shared child-window drag behavior supports moving and title double-click");

var mainWindowText = File.ReadAllText(mainWindowPath);
foreach (var navigationSymbol in new[]
         {
             "PhoneDesktop24", "ProjectionScreen24", "Settings24",
             "VideoRecording20", "UsbPlug24", "Info24",
         })
{
    Equal(true, mainWindowText.Contains($"Symbol=\"{navigationSymbol}\" FontSize=\"20\"",
            StringComparison.Ordinal),
        $"main navigation uses the semantic {navigationSymbol} icon");
}
Equal(true,
    mainWindowText.Contains("Style=\"{StaticResource TitleBarCloseButton}\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("Symbol=\"Subtract20\" FontSize=\"16\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("Symbol=\"Maximize20\" FontSize=\"16\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("Style=\"{StaticResource ButtonSymbolIcon}\" Symbol=\"Dismiss20\" FontSize=\"17\"",
        StringComparison.Ordinal),
    "main title-bar controls have readable glyph sizes and close-button feedback");
foreach (var navigationKey in new[]
         {
             "NavMirroring", "NavDevices", "NavOutput", "NavSettings",
             "NavDriver", "NavAbout",
         })
{
    Equal(true,
        mainWindowText.Contains($"{{DynamicResource {navigationKey}}}",
            StringComparison.Ordinal),
        $"main navigation contains {navigationKey}");
}
Equal(true, mainWindowText.StartsWith("<ui:FluentWindow", StringComparison.Ordinal) &&
            mainWindowText.Contains("WindowBackdropType=\"Mica\"",
                StringComparison.Ordinal),
    "main window uses FluentWindow with a Mica backdrop");
Equal(true, mainWindowText.Contains("<ui:NavigationView", StringComparison.Ordinal) &&
            mainWindowText.Contains("PaneDisplayMode=\"LeftMinimal\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("CompactPaneLength=\"48\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("OpenPaneLength=\"208\"",
                StringComparison.Ordinal),
    "main navigation uses the standard compact overlay pane");
Equal(false, mainWindowText.Contains("PaneTitle=", StringComparison.Ordinal),
    "compact navigation does not repeat the product title inside the pane");
Equal(true, mainWindowText.Contains("x:Key=\"ShellNavigationItem\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains(
        "Value=\"{DynamicResource NavigationTextFontFamily}\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("FontWeight\" Value=\"Normal\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("x:Name=\"PaneItemText\"", StringComparison.Ordinal) &&
    mainWindowText.Contains("TextOptions.TextFormattingMode=\"Display\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("x:Name=\"ActiveIndicator\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("<Condition Property=\"IsPaneOpen\" Value=\"True\"/>",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("<ColumnDefinition Width=\"48\"/>",
        StringComparison.Ordinal),
    "navigation items use a light compact selection indicator and restrained labels");
Equal(false, mainWindowText.Contains("NavigationColumn", StringComparison.Ordinal) ||
             mainWindowText.Contains("NavigationLabel", StringComparison.Ordinal),
    "main navigation no longer mixes column-width and label visibility animations");
Equal(true, mainWindowText.Contains("x:Name=\"MirroringPanelToggle\"",
        StringComparison.Ordinal),
    "mirroring navigation can expand its common actions");
Equal(true, mainWindowText.Contains("x:Name=\"ThemeComboBox\"",
        StringComparison.Ordinal),
    "main settings place theme selection beside language");
Equal(false, mainWindowText.Contains("x:Name=\"LogExpander\"",
        StringComparison.Ordinal),
    "live logs are no longer rendered in the main preview workspace");
Equal(true, mainWindowText.Contains("x:Name=\"MediaCastSeekSlider\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("x:Name=\"MediaCastPlayPauseButton\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("x:Name=\"MediaCastVolumeSlider\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("x:Name=\"MediaCastFullScreenButton\"",
                StringComparison.Ordinal),
    "video casting exposes conventional playback, seek, and volume controls");
Equal(true, mainWindowText.Contains("x:Key=\"MediaPlayerIconButtonStyle\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("x:Key=\"MediaPlayerPrimaryButtonStyle\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("x:Key=\"MediaPlayerSliderStyle\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("{DynamicResource MediaPlayerScrimBrush}",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("{DynamicResource MediaPlayerPrimaryBorderBrush}",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("Foreground=\"{DynamicResource MediaPlayerPrimaryIconBrush}\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("OnMediaCastPlayerMouseMove",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("OnMediaCastPlayerSizeChanged",
                StringComparison.Ordinal),
    "cast playback controls share one themed, responsive interaction system");
Equal(true,
    mainWindowText.Contains("x:Key=\"MediaPlayerSpeedComboBoxStyle\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("x:Key=\"MediaPlayerSpeedComboBoxItemStyle\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("x:Name=\"PART_Popup\"", StringComparison.Ordinal) &&
    mainWindowText.Contains("PlacementTarget=\"{Binding RelativeSource={RelativeSource TemplatedParent}}\"",
        StringComparison.Ordinal) &&
    mainWindowText.Contains("MediaCastSeekBackwardButton", StringComparison.Ordinal) &&
    mainWindowText.Contains("MediaCastSeekForwardButton", StringComparison.Ordinal) &&
    mainWindowText.Contains("Foreground=\"{DynamicResource MediaOverlayTextBrush}\"",
        StringComparison.Ordinal),
    "cast skip controls and speed selector keep white overlay text in light theme");
foreach (var iconName in new[] { "MediaCastVolumeIcon", "MediaCastFullScreenIcon" })
{
    var iconStart = mainWindowText.IndexOf($"x:Name=\"{iconName}\"",
        StringComparison.Ordinal);
    var iconEnd = iconStart >= 0
        ? mainWindowText.IndexOf("/>", iconStart, StringComparison.Ordinal)
        : -1;
    Equal(true, iconStart >= 0 && iconEnd > iconStart &&
                mainWindowText[iconStart..iconEnd].Contains(
                    "Style=\"{StaticResource ButtonSymbolIcon}\"",
                    StringComparison.Ordinal),
        $"{iconName} inherits the themed media button foreground");
}
var mediaPlayerButtonStyleStart = mainWindowText.IndexOf(
    "<Style x:Key=\"MediaPlayerIconButtonStyle\"", StringComparison.Ordinal);
var mediaPlayerButtonStyleEnd = mainWindowText.IndexOf("</Style>",
    mediaPlayerButtonStyleStart, StringComparison.Ordinal);
var mediaPlayerButtonStyle = mainWindowText[mediaPlayerButtonStyleStart..
    (mediaPlayerButtonStyleEnd + "</Style>".Length)];
Equal(true, mediaPlayerButtonStyle.Contains(
                "Value=\"{DynamicResource NavigationTextFontFamily}\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("x:Name=\"MediaCastPlayPauseIcon\"",
                StringComparison.Ordinal) &&
            mainWindowText.Contains("Symbol=\"Pause20\"",
                StringComparison.Ordinal),
    "cast controls use a CJK-capable UI font and packaged SymbolIcon glyphs");
Equal(false, mainWindowText.Contains("CloseMediaCastButton",
        StringComparison.Ordinal),
    "video casting preview has no redundant corner close button");

var mainWindowCodePath = Path.Combine(sourceDirectory, "App", "MainWindow.xaml.cs");
var mainWindowCode = File.ReadAllText(mainWindowCodePath);
var bluetoothHidCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Services", "BluetoothHidMouseService.cs"));
Equal(true,
    bluetoothHidCode.Contains("00002a22-0000-1000-8000-00805f9b34fb",
        StringComparison.OrdinalIgnoreCase) &&
    bluetoothHidCode.Contains("00002a33-0000-1000-8000-00805f9b34fb",
        StringComparison.OrdinalIgnoreCase) &&
    bluetoothHidCode.Contains("HasTargetSubscriber(_bootMouseInput)",
        StringComparison.Ordinal) &&
    bluetoothHidCode.Contains("HasTargetSubscriber(_bootKeyboardInput)",
        StringComparison.Ordinal),
    "Bluetooth HID exposes and targets Boot Protocol keyboard and mouse reports");
Equal(true, mainWindowCode.Contains("_mediaControlsHideTimer",
                StringComparison.Ordinal) &&
            mainWindowCode.Contains("SetMediaCastControlsVisible",
                StringComparison.Ordinal) &&
            mainWindowCode.Contains("width >= 430", StringComparison.Ordinal) &&
            mainWindowCode.Contains("width >= 620", StringComparison.Ordinal),
    "cast playback controls fade when idle and adapt at compact widths");
Equal(true, mainWindowCode.Contains("AddHandler(Mouse.PreviewMouseDownEvent",
                StringComparison.Ordinal) &&
            mainWindowCode.Contains("handledEventsToo: true",
                StringComparison.Ordinal) &&
            mainWindowCode.Contains("RetryPendingMediaCastSeek",
                StringComparison.Ordinal),
    "cast progress clicks survive handled slider events and confirm the seek");
var appStartupCode = File.ReadAllText(Path.Combine(sourceDirectory, "App", "App.xaml.cs"));
var appIdentityCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Services", "AppIdentity.cs"));
Equal(true, appStartupCode.Contains("AppIdentity.Attach(MainWindow)",
        StringComparison.Ordinal) &&
    appIdentityCode.Contains("ExtractIconExW", StringComparison.Ordinal) &&
    appIdentityCode.Contains("WmSetIcon", StringComparison.Ordinal),
    "main window publishes its executable icon to the Windows taskbar");
Equal(true,
    mainWindowCode.Contains("private enum LeftWorkspacePanel", StringComparison.Ordinal) &&
    mainWindowCode.Contains("private bool _isSettingsPanelVisible;",
        StringComparison.Ordinal) &&
    mainWindowCode.Contains("SetSettingsPanelVisible(!_isSettingsPanelVisible)",
        StringComparison.Ordinal) &&
    mainWindowCode.Contains("var showSettings = _isSettingsPanelVisible;",
        StringComparison.Ordinal) &&
    !mainWindowCode.Contains("WorkspacePanel.Settings", StringComparison.Ordinal),
    "left workspace pages stay exclusive while settings toggles independently");
Equal(true,
    mainWindowCode.Contains(
        "_leftWorkspacePanel == LeftWorkspacePanel.Devices)",
        StringComparison.Ordinal) &&
    mainWindowCode.Contains("workspace_left_panel_auto_opened",
        StringComparison.Ordinal),
    "source auto-open telemetry is emitted only when the panel actually changes");
Equal(true, mainWindowCode.Contains("AnimateWorkspaceSurface", StringComparison.Ordinal) &&
            mainWindowCode.Contains("BeginAnimation(WidthProperty", StringComparison.Ordinal),
    "workspace panels animate layout width so preview resizing stays continuous");
Equal(true,
    mainWindowCode.Contains(
        "SetWorkspaceSurfaceImmediate(LeftPanelHost, visible: false, width: 300)",
        StringComparison.Ordinal) &&
    mainWindowCode.Contains(
        "SetWorkspaceSurfaceImmediate(ControlPanel, visible: false, width: 336)",
        StringComparison.Ordinal),
    "entering full screen cancels pending workspace panel animations");
var closeMethodIndex = mainWindowCode.IndexOf(
    "private async void OnClosing", StringComparison.Ordinal);
var explicitShutdownModeIndex = mainWindowCode.IndexOf(
    "application.ShutdownMode = ShutdownMode.OnExplicitShutdown;",
    closeMethodIndex, StringComparison.Ordinal);
var hideWpfWindowsIndex = mainWindowCode.IndexOf(
    "window.Hide();", explicitShutdownModeIndex, StringComparison.Ordinal);
var hideNativeWindowsIndex = mainWindowCode.IndexOf(
    "_secondaryMirrors.HideForShutdown();", hideWpfWindowsIndex,
    StringComparison.Ordinal);
var yieldAfterHideIndex = mainWindowCode.IndexOf(
    "await Dispatcher.Yield(DispatcherPriority.Background);",
    hideNativeWindowsIndex, StringComparison.Ordinal);
var coreShutdownIndex = mainWindowCode.IndexOf(
    "_viewModel.ShutdownAsync();", yieldAfterHideIndex, StringComparison.Ordinal);
var explicitApplicationExitIndex = mainWindowCode.IndexOf(
    "application.Shutdown(0);", coreShutdownIndex, StringComparison.Ordinal);
Equal(true, closeMethodIndex >= 0 &&
            explicitShutdownModeIndex > closeMethodIndex &&
            hideWpfWindowsIndex > explicitShutdownModeIndex &&
            hideNativeWindowsIndex > hideWpfWindowsIndex &&
            yieldAfterHideIndex > hideNativeWindowsIndex &&
            coreShutdownIndex > yieldAfterHideIndex &&
            explicitApplicationExitIndex > coreShutdownIndex,
    "window close hides every UI surface before background cleanup and exits explicitly");
var nativePreviewWindowCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Windows", "NativePreviewWindow.cs"));
var aspectRatioControllerCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Services", "AspectRatioWindowController.cs"));
var previewRendererCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "Core", "src", "Renderer", "D3D11PreviewRenderer.cpp"));
var multiPreviewManagerCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Services", "MultiDevicePreviewManager.cs"));
Equal(true,
    nativePreviewWindowCode.Contains("internal void HideForShutdown()",
        StringComparison.Ordinal) &&
    nativePreviewWindowCode.Contains("ShowWindow(_handle, SwHide)",
        StringComparison.Ordinal) &&
    multiPreviewManagerCode.Contains("internal void HideForShutdown()",
        StringComparison.Ordinal) &&
    multiPreviewManagerCode.Contains("_disposing = true;",
        StringComparison.Ordinal),
    "native preview windows hide immediately and reject openings during shutdown");
Equal(true,
    aspectRatioControllerCode.Contains("internal bool ApplyInitialBounds()",
        StringComparison.Ordinal) &&
    nativePreviewWindowCode.Contains("candidate.ShowInitially();",
        StringComparison.Ordinal) &&
    nativePreviewWindowCode.Contains(
        "SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow",
        StringComparison.Ordinal) &&
    !nativePreviewWindowCode.Contains("ShowWindow(candidate._handle, SwShow)",
        StringComparison.Ordinal),
    "native previews apply final bounds while hidden and become visible in one step");
Equal(true,
    nativePreviewWindowCode.Contains("private bool _isTopMost;",
        StringComparison.Ordinal) &&
    nativePreviewWindowCode.Contains("SetWindowPos(_handle, HwndNoTopMost",
        StringComparison.Ordinal) &&
    !nativePreviewWindowCode.Contains("_isTopMost = SetWindowPos(_handle, HwndTopMost",
        StringComparison.Ordinal),
    "independent previews start non-topmost while retaining the manual pin command");
Equal(true,
    mainViewModelSource.Contains("private bool _bluetoothControlStopping;",
        StringComparison.Ordinal) &&
    mainViewModelSource.Contains("if (_bluetoothControlStopping ||",
        StringComparison.Ordinal) &&
    mainWindowCode.Contains("IsBluetoothControlTarget(_viewModel.SelectedDevice?.Udid)",
        StringComparison.Ordinal) &&
    mainWindowCode.Contains("private readonly SemaphoreSlim _bluetoothRouteGate",
        StringComparison.Ordinal) &&
    mainWindowCode.Contains("IsBluetoothControlActiveFor(string? udid)",
        StringComparison.Ordinal) &&
    mainWindowCode.Contains("_secondaryMirrors.Activate(_activeControlUdid)",
        StringComparison.Ordinal) &&
    bluetoothHidCode.Contains("NotifyValueAsync(buffer, targetClient)",
        StringComparison.Ordinal) &&
    bluetoothHidCode.Contains("BluetoothSubscribedClientSelector.Select",
        StringComparison.Ordinal) &&
    multiPreviewManagerCode.Contains("internal bool Activate(string? udid)",
        StringComparison.Ordinal),
    "Bluetooth control serializes routing and targets only the selected mirrored device and GATT client");
var busyStateStart = mainViewModelSource.IndexOf("public bool IsBusy", StringComparison.Ordinal);
var busyStateEnd = busyStateStart >= 0
    ? mainViewModelSource.IndexOf("private bool IsSettingsInteractionBlocked", busyStateStart,
        StringComparison.Ordinal)
    : -1;
var busyStateCode = busyStateStart >= 0 && busyStateEnd > busyStateStart
    ? mainViewModelSource[busyStateStart..busyStateEnd] : string.Empty;
var mediaSelectionStart = mainViewModelSource.IndexOf(
    "if (value?.IsMediaCast == true)", StringComparison.Ordinal);
var mediaSelectionEnd = mediaSelectionStart >= 0
    ? mainViewModelSource.IndexOf("return;", mediaSelectionStart,
        StringComparison.Ordinal)
    : -1;
var mediaSelectionCode = mediaSelectionStart >= 0 && mediaSelectionEnd > mediaSelectionStart
    ? mainViewModelSource[mediaSelectionStart..mediaSelectionEnd] : string.Empty;
Equal(true,
    busyStateCode.Contains("OnPropertyChanged(nameof(CanToggleBluetoothControl));",
        StringComparison.Ordinal) &&
    mediaSelectionCode.Contains("OnPropertyChanged(nameof(CanToggleBluetoothControl));",
        StringComparison.Ordinal) &&
    mainViewModelSource.Contains("private void NotifyCaptureSessionChanged()", StringComparison.Ordinal),
    "Bluetooth action availability refreshes for busy, media-source, and capture-session changes");
Equal(true,
    previewRendererCode.Contains("horizontal_gap >= 0.0F && horizontal_gap < 1.0F",
        StringComparison.Ordinal) &&
    previewRendererCode.Contains("vertical_gap >= 0.0F && vertical_gap < 1.0F",
        StringComparison.Ordinal) &&
    previewRendererCode.Contains("aspect_error <= pixel_error_limit",
        StringComparison.Ordinal) &&
    previewRendererCode.Contains("viewport.Height = static_cast<float>(target_height);",
        StringComparison.Ordinal),
    "native preview removes only sub-pixel aspect-rounding bars");
var themeServiceText = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Services", "ThemeService.cs"));
Equal(true,
    themeServiceText.Contains("WindowBackdropType.None,", StringComparison.Ordinal) &&
    themeServiceText.Contains("WindowBackgroundManager.UpdateBackground(window",
        StringComparison.Ordinal) &&
    themeServiceText.Contains("fluentWindow.WindowBackdropType",
        StringComparison.Ordinal),
    "theme changes refresh every open window with its own backdrop type");
Equal(true, mainWindowText.Contains(
        "Background=\"{DynamicResource PreviewChromeBrush}\"", StringComparison.Ordinal),
    "main preview surface follows the active light/dark theme");
Equal(false, mainWindowText.Contains("Background=\"#050505\"",
        StringComparison.Ordinal),
    "main preview does not hard-code a dark background");
var aboutWindowPath = Path.Combine(sourceDirectory, "App", "Windows", "AboutWindow.xaml");
var aboutWindowText = File.ReadAllText(aboutWindowPath);
var aboutWindowCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Windows", "AboutWindow.xaml.cs"));
Equal(true,
    mainWindowText.Contains("Background=\"{DynamicResource AppBackgroundBrush}\"",
        StringComparison.Ordinal) &&
    aboutWindowText.Contains("Background=\"{DynamicResource AppBackgroundBrush}\"",
        StringComparison.Ordinal),
    "main and about windows share the same themed Mica background surface");
Equal(true, aboutWindowText.StartsWith("<ui:FluentWindow", StringComparison.Ordinal) &&
            aboutWindowText.Contains("WindowBackdropType=\"Mica\"",
                StringComparison.Ordinal),
    "about window uses FluentWindow with Mica");
Equal(true, aboutWindowText.Contains("{DynamicResource CheckForUpdates}",
                StringComparison.Ordinal) &&
            aboutWindowText.Contains(
                "Style=\"{StaticResource AboutCheckUpdatesButtonStyle}\"",
                StringComparison.Ordinal) &&
            aboutWindowText.Contains(
                "Value=\"{DynamicResource AboutCheckUpdatesTextBrush}\"",
                StringComparison.Ordinal),
    "check for updates keeps an explicit theme-aware foreground");
Equal(true,
    mainWindowText.Contains(
        "BasedOn=\"{StaticResource ContentEdgeScrollBarStyle}\"",
        StringComparison.Ordinal),
    "settings scrollbar uses the shared right-edge style");
var updateWindowScrollText = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Windows", "UpdateWindow.xaml"));
Equal(true,
    updateWindowScrollText.Contains("x:Name=\"ReleaseNotesViewer\"",
        StringComparison.Ordinal) &&
    updateWindowScrollText.Contains(
        "BasedOn=\"{StaticResource ContentEdgeScrollBarStyle}\"",
        StringComparison.Ordinal),
    "update release notes use the same modern right-edge scrollbar style");
var conflictWindowText = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Windows", "InstanceConflictWindow.xaml"));
Equal(true,
    conflictWindowText.StartsWith("<ui:FluentWindow", StringComparison.Ordinal) &&
    conflictWindowText.Contains("WindowBackdropType=\"None\"",
        StringComparison.Ordinal) &&
    conflictWindowText.Contains("WindowCornerPreference=\"Round\"",
        StringComparison.Ordinal) &&
    conflictWindowText.Contains("Style=\"{StaticResource ModernDialogSurface}\"",
        StringComparison.Ordinal) &&
    conflictWindowText.Contains("CloseOtherInstancesButton",
        StringComparison.Ordinal) &&
    conflictWindowText.Contains("CloseCurrentInstanceButton",
        StringComparison.Ordinal) &&
    conflictWindowText.Contains("Style=\"{StaticResource PrimaryButton}\"",
        StringComparison.Ordinal) &&
    conflictWindowText.Contains("IsDefault=\"True\"", StringComparison.Ordinal),
    "instance conflict uses the shared DWM-rounded decision dialog");
Equal(false, aboutWindowText.Contains("SettingsSection", StringComparison.Ordinal),
    "about content uses lightweight unframed sections instead of a large nested card");
Equal(true, aboutWindowText.Contains("DiagnosticPath, Mode=OneWay",
        StringComparison.Ordinal),
    "about diagnostics never binds a read-only path two-way");
Equal(true, aboutWindowText.Contains("x:Name=\"LiveLogTextBox\"",
        StringComparison.Ordinal),
    "about diagnostics owns the live log view");
Equal(true, aboutWindowText.Contains("SubWindowTabControl", StringComparison.Ordinal),
    "about navigation uses the shared child-window tab language");
Equal(false,
    aboutWindowText.Contains("{Binding ThemeChoices}", StringComparison.Ordinal) ||
    aboutWindowText.Contains("{Binding SelectedTheme}", StringComparison.Ordinal) ||
    aboutWindowCode.Contains("ThemeChoice", StringComparison.Ordinal) ||
    aboutWindowCode.Contains("SelectedTheme", StringComparison.Ordinal),
    "theme selection lives in main settings instead of the about window");
var mediaOutputWindowText = File.ReadAllText(Path.Combine(sourceDirectory, "App", "Windows",
    "MediaOutputSettingsWindow.xaml"));
Equal(true, mediaOutputWindowText.Contains("SubWindowTabControl", StringComparison.Ordinal),
    "media output uses the shared child-window tab language");
var updateWindowText = File.ReadAllText(Path.Combine(sourceDirectory, "App", "Windows",
    "UpdateWindow.xaml"));
Equal(true, updateWindowText.Contains("PageTransition.IsEnabled=\"True\"",
        StringComparison.Ordinal),
    "update window animates an inner layout element instead of the Window");
Equal(true, updateWindowText.Contains(
        "Value=\"{Binding ProgressValue, Mode=OneWay}\"", StringComparison.Ordinal),
    "update progress does not write back to a read-only view-model property");
var usbInfoWindowText = File.ReadAllText(Path.Combine(sourceDirectory, "App", "Windows",
    "UsbProjectionModeInfoWindow.xaml"));
Equal(true, usbInfoWindowText.Contains("Height=\"620\"", StringComparison.Ordinal) &&
            usbInfoWindowText.Contains("ScrollViewer Grid.Row=\"2\"", StringComparison.Ordinal),
    "USB mode guidance is tall enough and scrolls long localized content");
Equal(true, modernControlsText.Contains("FocusVisualStyle", StringComparison.Ordinal),
    "shared icon controls replace the default rectangular focus adorner");
var driverMainWindowText = File.ReadAllText(Path.Combine(sourceDirectory,
    "DriverInstaller", "MainWindow.xaml"));
Equal(true, driverMainWindowText.Contains("x:Name=\"DriverWindowChrome\"",
        StringComparison.Ordinal),
    "driver manager names its shared window chrome for maximize frame policy");
Equal(true, driverMainWindowText.Contains(
        "Background=\"{DynamicResource WindowBackgroundBrush}\"",
        StringComparison.Ordinal),
    "driver manager uses an opaque themed background in light mode");
var driverThemeServiceText = File.ReadAllText(Path.Combine(sourceDirectory,
    "DriverInstaller", "Services", "DriverThemeService.cs"));
Equal(true, driverThemeServiceText.Contains("DwmBorderColor", StringComparison.Ordinal) &&
            driverThemeServiceText.Contains("DwmColorDefault", StringComparison.Ordinal),
    "driver manager applies the same DWM border color policy as the main window");
Equal(true, driverThemeServiceText.Contains(
        "Window.BackgroundProperty, \"WindowBackgroundBrush\"",
        StringComparison.Ordinal),
    "driver child windows keep an opaque background when the theme changes");

Equal(true, SemanticVersion.Parse("v1.2.0") > SemanticVersion.Parse("v1.1.9"),
    "semantic version compares numeric minor and patch components");
Equal(true, SemanticVersion.Parse("1.3.0") >
    SemanticVersion.Parse("1.3.0-beta.9"),
    "stable release is newer than prerelease with the same core version");
Equal(true, SemanticVersion.Parse("1.3.0-beta.10") >
    SemanticVersion.Parse("1.3.0-beta.2"),
    "numeric prerelease identifiers compare numerically");
Equal(true, SemanticVersion.Parse("1.3.0-beta.100000000000000000000") >
    SemanticVersion.Parse("1.3.0-beta.99999999999999999999"),
    "arbitrarily large numeric prerelease identifiers compare numerically");
Equal(false, SemanticVersion.TryParse("1.02.0", out _),
    "semantic version rejects leading zeroes");
Equal(false, SemanticVersion.TryParse("1.2.0-beta.02", out _),
    "semantic version rejects leading zeroes in numeric prerelease identifiers");
Equal(true, StartupDiagnostics.UserMessage(new DllNotFoundException(), true)
    .Contains("原生组件", StringComparison.Ordinal),
    "startup diagnostics explain native dependency load failures");
Equal(true, StartupDiagnostics.UserMessage(new FileNotFoundException(), false)
    .Contains("native component", StringComparison.OrdinalIgnoreCase),
    "startup preflight missing-file failures use native dependency guidance");
Equal(true, StartupDiagnostics.UserMessage(new DllNotFoundException(), "zh-HK")
    .Contains("原生元件", StringComparison.Ordinal),
    "Hong Kong startup diagnostics use localized native dependency guidance");
Equal(true, CaptureErrorGuidance.IsNoPingTimeout(
        "QuickTime endpoint opened but iPhone sent no PING; keep the device unlocked"),
    "capture guidance recognizes the native no-PING timeout");
Equal(true, CaptureErrorGuidance.IsNoPingTimeout(
        "quicktime endpoint opened but iphone SENT NO ping"),
    "capture guidance recognizes no-PING diagnostics case-insensitively");
Equal(false, CaptureErrorGuidance.IsNoPingTimeout(
        "QuickTime endpoint opened; waiting PING"),
    "capture guidance does not treat an in-progress handshake as a timeout");
Equal(true, CaptureErrorGuidance.IsUsbConfigurationFailure(
        "open QuickTime USB interface: libusb0-dll:err [set_configuration] could not set config 5: win error: ����"),
    "capture guidance recognizes the libusb0 configuration failure despite localized text");
Equal(false, CaptureErrorGuidance.IsUsbConfigurationFailure(
        "QuickTime endpoint opened but iPhone sent no PING; keep the device unlocked"),
    "capture guidance does not confuse a no-PING timeout with a USB configuration failure");
var compactCaptureGuidance = CaptureErrorGuidance.UserMessage(
    CaptureFailureKind.NoVideoFrames, CaptureFailureStage.VideoStream, -42,
    "decoder failed while opening a 1920x1080 frame");
Equal(LocalizationService.Get("CaptureActionVideoRetry"), compactCaptureGuidance,
    "capture failure prompts tell the user how to recover");
Equal(false, compactCaptureGuidance.Contains("-42", StringComparison.Ordinal) ||
    compactCaptureGuidance.Contains("1920x1080", StringComparison.Ordinal),
    "capture failure prompts do not expose native codes or diagnostic details");
var deviceSessionClosedStatus = new NativeCaptureStatus
{
    FailureKind = CaptureFailureKind.SystemClosed,
    FailureStage = CaptureFailureStage.VideoStream,
    ErrorCode = -2109,
    Message = "wired media silence",
};
Equal(LocalizationService.Get("DeviceSessionClosedWarningBody"),
    CaptureErrorGuidance.UserMessage(deviceSessionClosedStatus),
    "a phone-side mirroring stop explains the Control Center action and required restart");
Equal(true, CaptureErrorGuidance.IsDeviceSessionClosedWarning(deviceSessionClosedStatus),
    "phone-side mirroring stops use the dedicated warning presentation");
Equal(false, CaptureErrorGuidance.IsDeviceSessionClosedWarning(
        deviceSessionClosedStatus with { ErrorCode = -2110 }),
    "USB disconnects do not use the phone-side stop warning presentation");
foreach (var cultureFile in new[] { "Strings.zh-CN.xaml", "Strings.zh-HK.xaml", "Strings.en-US.xaml" })
{
    Equal(true, File.ReadAllText(Path.Combine(sourceDirectory, "App", "Localization", cultureFile))
            .Contains("DeviceSessionClosedWarningTitleFormat", StringComparison.Ordinal) &&
        File.ReadAllText(Path.Combine(sourceDirectory, "App", "Localization", cultureFile))
            .Contains("DeviceSessionClosedWarningBody", StringComparison.Ordinal),
        $"{cultureFile} localizes the phone-side mirroring stop warning");
}
Equal(LocalizationService.Get("CaptureActionWaitForCleanup"),
    CaptureErrorGuidance.UserMessage(CaptureFailureKind.ExistingSession,
        CaptureFailureStage.SessionTeardown, (int)NativeResult.SessionAlreadyExists, "duplicate"),
    "existing-session prompts wait for lifecycle cleanup instead of suggesting concurrent retries");
Equal(CaptureFailureKind.ExistingSession,
    CaptureErrorGuidance.StartFailureKind((int)NativeResult.SessionAlreadyExists),
    "duplicate native sessions are presented as an existing-session failure");
Equal(CaptureFailureKind.Driver,
    CaptureErrorGuidance.StartFailureKind((int)NativeResult.DriverSafetyBlocked),
    "unsafe Apple/libusb0 stacks are presented as a driver failure");
Equal(CaptureFailureKind.UsbConnection,
    CaptureErrorGuidance.StartFailureKind((int)NativeResult.TransportUnavailable),
    "native USB transport failures are presented as a USB connection failure");
Equal(CaptureFailureKind.UsbConnection,
    CaptureErrorGuidance.StartFailureKind((int)NativeResult.DeviceNotFound),
    "native missing-device failures are presented as a USB connection failure");
Equal(true, new IPhoneFilterDriverStatus(
        IPhoneFilterDriverState.UnsafeStack, "test", "unsafe").CanStartCapture,
    "managed driver preflight keeps wired capture available on the diagnosed filter stack");
Equal(true, File.ReadAllText(Path.Combine(sourceDirectory, "App", "Services",
        "IPhoneFilterDriverService.cs")).Contains(
        "(status & DnStarted) != 0", StringComparison.Ordinal),
    "managed driver preflight accepts only a started Apple USB parent node");
Equal("-9 (0xFFFFFFF7)", CaptureErrorGuidance.ErrorCodeText(
        (int)NativeResult.DriverSafetyBlocked),
    "driver safety blocks retain their decimal and hexadecimal code");
Equal("-8 (0xFFFFFFF8)", CaptureErrorGuidance.ErrorCodeText(
        (int)NativeResult.SessionAlreadyExists),
    "duplicate native session errors retain their decimal and hexadecimal code");
Equal(CaptureFailureKind.UsbConnection,
    CaptureErrorGuidance.StartFailureKind((int)NativeResult.UsbConfigurationNotReady),
    "an unfinished previous USB configuration is presented as a connection lifecycle failure");
Equal("-11 (0xFFFFFFF5)", CaptureErrorGuidance.ErrorCodeText(
        (int)NativeResult.SessionTeardownFailed),
    "teardown failures retain their decimal and hexadecimal code");
Equal("-12 (0xFFFFFFF4)", CaptureErrorGuidance.ErrorCodeText(
        (int)NativeResult.UsbConfigurationRestoreWarning),
    "USB restore warnings retain their decimal and hexadecimal code");
Equal(464, System.Runtime.InteropServices.Marshal.SizeOf<NativeCaptureStatus>(),
    "managed capture status matches the native API v18 layout");

const string releaseFixture = """
[
  {
    "tag_name": "v1.4.0-beta.1",
    "name": "Beta",
    "body": "beta notes",
    "published_at": "2026-07-28T10:00:00Z",
    "draft": false,
    "prerelease": true,
    "assets": [
      { "name": "setup.exe", "size": 30,
        "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "browser_download_url": "https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.4.0-beta.1/setup.exe" }
    ]
  },
  {
    "tag_name": "v1.3.1",
    "name": "Stable",
    "body": "# Added\nFeature",
    "published_at": "2026-07-27T10:00:00Z",
    "draft": false,
    "prerelease": false,
    "assets": [
      { "name": "app.zip", "size": 20,
        "browser_download_url": "https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.3.1/app.zip" },
      { "name": "setup.exe", "size": 10,
        "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "browser_download_url": "https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.3.1/setup.exe" },
      { "name": "SHA256SUMS.txt", "size": 5,
        "browser_download_url": "https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.3.1/SHA256SUMS.txt" }
    ]
  }
]
""";
var stableRelease = ReleaseParser.ParseLatest(releaseFixture,
    includeStable: true, includePrerelease: false);
Equal("v1.3.1", stableRelease?.TagName,
    "stable update channel ignores prereleases");
Equal("setup.exe", stableRelease?.PreferredAsset?.Name,
    "release parser prefers x64 Setup EXE over ZIP");
Equal("app.zip", stableRelease?.SelectAsset(preferInstaller: false)?.Name,
    "portable deployment selects the ZIP asset");
Equal("setup.exe", stableRelease?.SelectAsset(preferInstaller: true)?.Name,
    "installed deployment selects the Setup asset");
Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    stableRelease?.PreferredAsset?.Sha256,
    "release parser preserves GitHub's SHA256 asset digest");
var betaRelease = ReleaseParser.ParseLatest(releaseFixture,
    includeStable: true, includePrerelease: true);
Equal("v1.4.0-beta.1", betaRelease?.TagName,
    "beta update channel selects the newest allowed prerelease");
Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    ReleaseParser.FindExpectedSha256(
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  iPhoneMirror-Setup-v1.3.1-x64.exe\n",
        "iPhoneMirror-Setup-v1.3.1-x64.exe"),
    "checksum parser matches the selected asset exactly");
const string nonInstallerExeFixture = """
[
  {
    "tag_name": "v1.3.2",
    "draft": false,
    "prerelease": false,
    "assets": [
      { "name": "iPhoneMirror.Driver.exe", "size": 10,
        "browser_download_url": "https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.3.2/iPhoneMirror.Driver.exe" },
      { "name": "app.zip", "size": 20,
        "browser_download_url": "https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.3.2/app.zip" }
    ]
  }
]
""";
Equal("app.zip",
    ReleaseParser.ParseLatest(nonInstallerExeFixture, true, false)?.PreferredAsset?.Name,
    "release parser never launches a non-installer EXE as an update");

const string mismatchedAssetNameFixture = """
[
  {
    "tag_name": "v1.3.3",
    "draft": false,
    "prerelease": false,
    "assets": [
      { "name": "safe-setup.exe", "size": 10,
        "browser_download_url": "https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.3.3/different.exe" },
      { "name": "foreign.zip", "size": 10,
        "browser_download_url": "https://github.com/another/repository/releases/download/v1.3.3/foreign.zip" }
    ]
  }
]
""";
Equal<ReleaseAsset?>(null,
    ReleaseParser.ParseLatest(mismatchedAssetNameFixture, true, false)?.PreferredAsset,
    "release parser rejects mismatched paths and foreign GitHub repositories");
var mirroredAsset = new ReleaseAsset("setup.exe",
    new Uri("https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.3.3/setup.exe"),
    10, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
var mirrorCandidates = GitHubReleaseClient.BuildDownloadCandidates(
    mirroredAsset, allowMirrorFallback: true);
Equal(116, mirrorCandidates.Count,
    "verified update assets include all 115 MoreTools mirrors and GitHub");
Equal("gh-proxy.net", mirrorCandidates[0].Host,
    "mirror candidates preserve the MoreTools source order");
Equal("github.com", mirrorCandidates[^1].Host,
    "official GitHub remains the final trusted fallback");
Equal(116, mirrorCandidates.Select(candidate => candidate.Host)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
    "download candidate hosts are unique");
Equal(true, mirrorCandidates.Take(mirrorCandidates.Count - 1).All(candidate =>
        candidate.Scheme == Uri.UriSchemeHttps &&
        candidate.AbsoluteUri.EndsWith(mirroredAsset.DownloadUri.AbsoluteUri,
            StringComparison.Ordinal)),
    "all mirrors proxy the exact trusted GitHub asset URL over HTTPS");
Equal(1, GitHubReleaseClient.BuildDownloadCandidates(
        mirroredAsset with { Sha256 = null }, allowMirrorFallback: true).Count,
    "unverified assets never use third-party download mirrors");
Equal(1, GitHubReleaseClient.BuildDownloadCandidates(
        mirroredAsset, allowMirrorFallback: false).Count,
    "disabled mirror fallback keeps the official download only");

var deploymentLayoutRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-layout-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(deploymentLayoutRoot);
    Equal(false, DeploymentLayout.UsesSharedRuntime(deploymentLayoutRoot),
        "portable layout has no external managed runtime markers");
    foreach (var marker in new[]
             {
                 "iPhoneMirror.dll", "iPhoneMirror.deps.json",
                 "iPhoneMirror.runtimeconfig.json",
             })
        File.WriteAllText(Path.Combine(deploymentLayoutRoot, marker), string.Empty);
    Equal(true, DeploymentLayout.UsesSharedRuntime(deploymentLayoutRoot),
        "installed layout is recognized by all external runtime markers");
    foreach (var marker in new[]
             {
                 "iPhoneMirror.dll", "iPhoneMirror.deps.json",
                 "iPhoneMirror.runtimeconfig.json",
             })
        File.Delete(Path.Combine(deploymentLayoutRoot, marker));
    var legacyInstalledExecutable = Path.Combine(deploymentLayoutRoot,
        "iPhoneMirror.exe");
    File.WriteAllBytes(legacyInstalledExecutable, []);
    Equal(true, DeploymentLayout.UsesSharedRuntime(deploymentLayoutRoot,
            registeredExecutablePath: legacyInstalledExecutable),
        "legacy single-file Setup layout is recognized by its App Paths registration");
    Equal(false, DeploymentLayout.UsesSharedRuntime(deploymentLayoutRoot,
            registeredExecutablePath: Path.Combine(deploymentLayoutRoot, "other.exe")),
        "portable layout is not promoted by an unrelated installation registration");
    var spacedRoot = Path.Combine(deploymentLayoutRoot, "Program Files", "iPhoneMirror");
    Directory.CreateDirectory(spacedRoot);
    var spacedExecutable = Path.Combine(spacedRoot, "iPhoneMirror.exe");
    File.WriteAllBytes(spacedExecutable, []);
    Equal(true, DeploymentLayout.IsRegisteredInstall(spacedExecutable,
            registeredExecutablePath: spacedExecutable),
        "installation registration comparison preserves paths containing spaces");
}
finally
{
    if (Directory.Exists(deploymentLayoutRoot))
        Directory.Delete(deploymentLayoutRoot, recursive: true);
}

var segmentedDownloadRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-segmented-download-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(segmentedDownloadRoot);
    var segmentedPayload = Enumerable.Range(0, 256 * 1024)
        .Select(index => (byte)(index * 31 % 251)).ToArray();
    var activeRangeRequests = 0;
    var maximumActiveRangeRequests = 0;
    var rangeRequestCount = 0;
    using var segmentedHttpClient = new HttpClient(new StubHttpMessageHandler(
        async (request, cancellationToken) =>
        {
            var range = request.Headers.Range?.Ranges.SingleOrDefault() ??
                throw new InvalidOperationException("Range request was expected.");
            var start = range.From ?? 0;
            var end = range.To ?? segmentedPayload.LongLength - 1;
            Interlocked.Increment(ref rangeRequestCount);
            var active = Interlocked.Increment(ref activeRangeRequests);
            while (true)
            {
                var observed = Volatile.Read(ref maximumActiveRangeRequests);
                if (active <= observed || Interlocked.CompareExchange(
                        ref maximumActiveRangeRequests, active, observed) == observed)
                    break;
            }
            try
            {
                await Task.Delay(25, cancellationToken);
                var content = new ByteArrayContent(segmentedPayload[
                    checked((int)start)..checked((int)end + 1)]);
                content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                    start, end, segmentedPayload.LongLength);
                return new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
                {
                    RequestMessage = request,
                    Content = content,
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeRangeRequests);
            }
        }));
    var segmentedPath = Path.Combine(segmentedDownloadRoot, "segmented.bin");
    var segmentedResult = await SegmentedHttpDownloader.DownloadAsync(
        segmentedHttpClient, new Uri("https://downloads.example.test/package.bin"),
        segmentedPath, new SegmentedDownloadOptions(
            MaximumBytes: segmentedPayload.LongLength,
            ExpectedBytes: segmentedPayload.LongLength,
            MaximumConcurrency: 4,
            MinimumSegmentBytes: 32 * 1024,
            BufferSize: 4096),
        finalUri => finalUri.Host.Equals("downloads.example.test",
            StringComparison.OrdinalIgnoreCase));
    Equal(4, segmentedResult.SegmentCount,
        "range-capable downloads use the configured parallel segment count");
    Equal(true, maximumActiveRangeRequests >= 2,
        "segmented downloader overlaps multiple HTTP range requests");
    Equal(5, rangeRequestCount,
        "segmented downloader sends one probe and four data ranges");
    Equal(true, File.ReadAllBytes(segmentedPath).SequenceEqual(segmentedPayload),
        "parallel random-access writes preserve the exact payload");

    var redirectedRequestHosts = new List<string>();
    using var redirectedHttpClient = new HttpClient(new StubHttpMessageHandler(
        (request, _) =>
        {
            redirectedRequestHosts.Add(request.RequestUri?.Host ?? string.Empty);
            var range = request.Headers.Range?.Ranges.SingleOrDefault() ??
                throw new InvalidOperationException("Range request was expected.");
            var start = range.From ?? 0;
            var end = range.To ?? segmentedPayload.LongLength - 1;
            var content = new ByteArrayContent(segmentedPayload[
                checked((int)start)..checked((int)end + 1)]);
            content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(
                    start, end, segmentedPayload.LongLength);
            var responseRequest = request;
            if (redirectedRequestHosts.Count == 1)
            {
                responseRequest = new HttpRequestMessage(request.Method,
                    "https://cdn.example.test/package.bin");
            }
            return Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.PartialContent)
            {
                RequestMessage = responseRequest,
                Content = content,
            });
        }));
    var redirectedPath = Path.Combine(segmentedDownloadRoot, "redirected.bin");
    var redirectedResult = await SegmentedHttpDownloader.DownloadAsync(
        redirectedHttpClient,
        new Uri("https://origin.example.test/package.bin"), redirectedPath,
        new SegmentedDownloadOptions(segmentedPayload.LongLength,
            segmentedPayload.LongLength, MaximumConcurrency: 4,
            MinimumSegmentBytes: 32 * 1024),
        finalUri => finalUri.Host.EndsWith(".example.test",
            StringComparison.OrdinalIgnoreCase));
    Equal(4, redirectedResult.SegmentCount,
        "redirected range downloads remain parallel");
    Equal("origin.example.test", redirectedRequestHosts[0],
        "range probe starts at the configured origin");
    Equal(true, redirectedRequestHosts.Skip(1).All(host =>
            host.Equals("cdn.example.test", StringComparison.OrdinalIgnoreCase)),
        "data ranges reuse the trusted final CDN URL from the probe");
    Equal(true, File.ReadAllBytes(redirectedPath).SequenceEqual(segmentedPayload),
        "redirected parallel download preserves the exact payload");

    var singleRequestCount = 0;
    using var singleHttpClient = new HttpClient(new StubHttpMessageHandler(
        (request, _) =>
        {
            Interlocked.Increment(ref singleRequestCount);
            return Task.FromResult(HttpResponse(request,
                new ByteArrayContent(segmentedPayload)));
        }));
    var singlePath = Path.Combine(segmentedDownloadRoot, "single.bin");
    var singleResult = await SegmentedHttpDownloader.DownloadAsync(singleHttpClient,
        new Uri("https://downloads.example.test/package.bin"), singlePath,
        new SegmentedDownloadOptions(segmentedPayload.LongLength,
            segmentedPayload.LongLength, MaximumConcurrency: 4,
            MinimumSegmentBytes: 32 * 1024),
        finalUri => finalUri.Host.Equals("downloads.example.test",
            StringComparison.OrdinalIgnoreCase));
    Equal(1, singleResult.SegmentCount,
        "servers that ignore Range automatically use a single stream");
    Equal(1, singleRequestCount,
        "the full probe response is reused for single-stream fallback");
    Equal(true, File.ReadAllBytes(singlePath).SequenceEqual(segmentedPayload),
        "single-stream fallback preserves the exact payload");

    var inconsistentRangeRequests = 0;
    var fullFallbackRequests = 0;
    using var inconsistentHttpClient = new HttpClient(new StubHttpMessageHandler(
        (request, _) =>
        {
            if (request.Headers.Range is not null)
            {
                var requestNumber = Interlocked.Increment(
                    ref inconsistentRangeRequests);
                if (requestNumber == 1)
                {
                    var content = new ByteArrayContent(segmentedPayload[..1]);
                    content.Headers.ContentRange =
                        new System.Net.Http.Headers.ContentRangeHeaderValue(
                            0, 0, segmentedPayload.LongLength);
                    return Task.FromResult(new HttpResponseMessage(
                        System.Net.HttpStatusCode.PartialContent)
                    {
                        RequestMessage = request,
                        Content = content,
                    });
                }
            }
            else
            {
                Interlocked.Increment(ref fullFallbackRequests);
            }
            return Task.FromResult(HttpResponse(request,
                new ByteArrayContent(segmentedPayload)));
        }));
    var inconsistentPath = Path.Combine(segmentedDownloadRoot, "inconsistent.bin");
    var inconsistentResult = await SegmentedHttpDownloader.DownloadAsync(
        inconsistentHttpClient,
        new Uri("https://downloads.example.test/package.bin"), inconsistentPath,
        new SegmentedDownloadOptions(segmentedPayload.LongLength,
            segmentedPayload.LongLength, MaximumConcurrency: 4,
            MinimumSegmentBytes: 32 * 1024),
        finalUri => finalUri.Host.Equals("downloads.example.test",
            StringComparison.OrdinalIgnoreCase));
    Equal(1, inconsistentResult.SegmentCount,
        "a server that stops honoring Range falls back to one full stream");
    Equal(true, inconsistentRangeRequests >= 2,
        "range inconsistency is detected after the successful probe");
    Equal(1, fullFallbackRequests,
        "range inconsistency triggers exactly one full fallback request");
    Equal(true, File.ReadAllBytes(inconsistentPath).SequenceEqual(segmentedPayload),
        "range inconsistency fallback preserves the exact payload");

    var failedPath = Path.Combine(segmentedDownloadRoot, "failed.bin");
    using var failedHttpClient = new HttpClient(new StubHttpMessageHandler(
        (request, _) => Task.FromResult(HttpResponse(request,
            new UnknownLengthContent(segmentedPayload[..1024])))));
    await ThrowsAsync<EndOfStreamException>(() =>
        SegmentedHttpDownloader.DownloadAsync(failedHttpClient,
            new Uri("https://downloads.example.test/package.bin"), failedPath,
            new SegmentedDownloadOptions(segmentedPayload.LongLength,
                segmentedPayload.LongLength),
            finalUri => finalUri.Host.Equals("downloads.example.test",
                StringComparison.OrdinalIgnoreCase)),
        "incomplete download throws when the response ends early");
    Equal(false, File.Exists(failedPath),
        "failed downloads remove their incomplete destination file");
}
finally
{
    if (Directory.Exists(segmentedDownloadRoot))
        Directory.Delete(segmentedDownloadRoot, recursive: true);
}

var updateSettingsRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-update-settings-{Guid.NewGuid():N}");
try
{
    var settingsStore = new UpdateSettingsStore(
        Path.Combine(updateSettingsRoot, "settings.json"));
    var savedSettings = new UpdateSettings
    {
        CheckOnStartup = false,
        AutoDownload = true,
        AllowMirrorFallback = false,
        NotifyStableReleases = true,
        NotifyPrereleaseReleases = true,
        Language = "zh-CN",
        Theme = AppTheme.Light,
        BluetoothMouseSensitivity = 135,
        BluetoothMouseSensitivitySchema = 1,
        BluetoothLandscapeMouseOrientationTurns = 3,
        BluetoothMouseOrientationSchema = 1,
        BluetoothWheelSensitivity = 180,
        BluetoothLandscapeMouseMode = 4,
        BluetoothMouseSettingsSchema = 1,
        BluetoothPortraitMouseDirection = 2,
        BluetoothLandscapeMouseDirection = 3,
        BluetoothMouseReverseHorizontal = true,
        BluetoothMouseReverseVertical = false,
        BluetoothMouseDirectionSchema = 1,
    };
    settingsStore.Save(savedSettings);
    var loadedSettings = settingsStore.Load();
    Equal(false, loadedSettings.CheckOnStartup,
        "update settings preserve startup check preference");
    Equal(true, loadedSettings.AutoDownload,
        "update settings preserve auto-download preference");
    Equal(false, loadedSettings.AllowMirrorFallback,
        "update settings preserve mirror fallback preference");
    Equal(AppTheme.Light, loadedSettings.Theme,
        "update settings preserve theme preference");
    Equal("zh-CN", loadedSettings.Language,
        "update settings preserve language preference");
    Equal(135d, loadedSettings.BluetoothMouseSensitivity,
        "update settings preserve Bluetooth mouse sensitivity");
    Equal(3, loadedSettings.BluetoothLandscapeMouseOrientationTurns,
        "update settings preserve Bluetooth landscape mouse orientation");
    Equal(180d, loadedSettings.BluetoothWheelSensitivity,
        "update settings preserve Bluetooth wheel sensitivity");
    Equal(4, loadedSettings.BluetoothLandscapeMouseMode,
        "update settings preserve Bluetooth landscape mouse mode");
    Equal(2, loadedSettings.BluetoothPortraitMouseDirection,
        "update settings preserve portrait mouse direction");
    Equal(3, loadedSettings.BluetoothLandscapeMouseDirection,
        "update settings preserve landscape mouse direction");
    Equal(true, loadedSettings.BluetoothMouseReverseHorizontal,
        "update settings preserve horizontal reversal");
    Equal(false, loadedSettings.BluetoothMouseReverseVertical,
        "update settings preserve vertical reversal");
    settingsStore.Update(settings => settings.Language = "en-US");
    var languageUpdatedSettings = settingsStore.Load();
    Equal(true, languageUpdatedSettings.AutoDownload,
        "language updates preserve unrelated update preferences");
    Equal(AppTheme.Light, languageUpdatedSettings.Theme,
        "language updates preserve the selected theme");
    Equal("en-US", languageUpdatedSettings.Language,
        "language updates persist the selected language");
}
finally
{
    if (Directory.Exists(updateSettingsRoot))
        Directory.Delete(updateSettingsRoot, recursive: true);
}

var defaultSettingsRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-default-settings-{Guid.NewGuid():N}");
try
{
    var defaults = new UpdateSettingsStore(
        Path.Combine(defaultSettingsRoot, "settings.json")).Load();
    Equal(500d, defaults.BluetoothMouseSensitivity,
        "Bluetooth mouse sensitivity defaults to 500 percent");
    Equal(1000d, defaults.BluetoothWheelSensitivity,
        "Bluetooth wheel sensitivity defaults to 1000 percent");
    Equal(0, defaults.BluetoothPortraitMouseDirection,
        "Bluetooth portrait direction defaults to up");
    Equal(1, defaults.BluetoothLandscapeMouseDirection,
        "Bluetooth landscape direction defaults to right");
    Equal(false, defaults.BluetoothMouseReverseHorizontal,
        "Bluetooth horizontal reversal defaults off");
    Equal(false, defaults.BluetoothMouseReverseVertical,
        "Bluetooth vertical reversal defaults off");

    var legacyPath = Path.Combine(defaultSettingsRoot, "legacy.json");
    Directory.CreateDirectory(defaultSettingsRoot);
    File.WriteAllText(legacyPath, "{\"BluetoothMouseSensitivity\":1000,\"BluetoothMouseSensitivitySchema\":1,\"BluetoothWheelSensitivity\":100,\"BluetoothMouseSettingsSchema\":1}");
    var migrated = new UpdateSettingsStore(legacyPath).Load();
    Equal(1000d, migrated.BluetoothMouseSensitivity,
        "legacy Bluetooth mouse sensitivity is never overwritten by a new default");
    Equal(100d, migrated.BluetoothWheelSensitivity,
        "legacy Bluetooth wheel sensitivity is never overwritten by a new default");
}
finally
{
    if (Directory.Exists(defaultSettingsRoot))
        Directory.Delete(defaultSettingsRoot, recursive: true);
}

Equal(BluetoothDeviceOrientation.Portrait,
    BluetoothMouseOrientationMapper.Detect(1206, 2622),
    "Bluetooth orientation detects portrait source frames");
Equal(BluetoothDeviceOrientation.Landscape,
    BluetoothMouseOrientationMapper.Detect(2622, 1206),
    "Bluetooth orientation detects landscape source frames");
Equal(BluetoothDeviceOrientation.Unknown,
    BluetoothMouseOrientationMapper.Detect(0, 0),
    "Bluetooth orientation remains unknown before the first frame");
var bluetoothNoticePolicy = new BluetoothControlNoticePolicy();
Equal(true, bluetoothNoticePolicy.ShouldShowForDevice("00008101-TEST-A"),
    "Bluetooth guidance is shown for the first device use in this application run");
Equal(false, bluetoothNoticePolicy.ShouldShowForDevice("00008101-test-a"),
    "Bluetooth guidance is not repeated for the same device in one application run");
Equal(true, bluetoothNoticePolicy.ShouldShowForDevice("00008101-TEST-B"),
    "Bluetooth guidance remains available for a different device");
Equal(false, bluetoothNoticePolicy.ShouldShowForDevice("  "),
    "Bluetooth guidance requires a stable device identifier");
Equal("client-a", BluetoothSubscribedClientSelector.Select(null,
    [("client-a", "Unknown device")]),
    "a single Bluetooth client is selected only when the mirrored name is unavailable");
Equal<string?>(null, BluetoothSubscribedClientSelector.Select("Ray's iPhone",
    [("client-a", "Work iPhone")]),
    "a lone Bluetooth client with the wrong name is rejected");
Equal("client-b", BluetoothSubscribedClientSelector.Select("Ray's iPhone",
    [("client-a", "Work iPhone"), ("client-b", "Ray's iPhone")]),
    "multiple Bluetooth clients are matched to the selected mirrored device name");
Equal<string?>(null, BluetoothSubscribedClientSelector.Select("Ray's iPhone",
    [("client-a", "iPhone"), ("client-b", "iPhone")]),
    "ambiguous Bluetooth clients are rejected instead of receiving broadcast input");
Equal<string?>(null, BluetoothSubscribedClientSelector.Select("Ray's iPhone",
    [("client-a", "Work iPhone"), ("client-b", "Travel iPhone")]),
    "unmatched Bluetooth clients are rejected instead of controlling another device");
var rightFromUp = BluetoothMouseOrientationMapper.Map(0, -10, 1206, 2622, 0,
    BluetoothMouseDirection.Up, BluetoothMouseDirection.Right, false, false);
Equal((0d, -10d), rightFromUp,
    "portrait up direction keeps upward movement unchanged");
var landscapeRightFromUp = BluetoothMouseOrientationMapper.Map(0, -10, 2622, 1206, 0,
    BluetoothMouseDirection.Up, BluetoothMouseDirection.Right, false, false);
Equal((10d, 0d), landscapeRightFromUp,
    "landscape right direction maps upward movement to the right");
var reversed = BluetoothMouseOrientationMapper.Map(3, -4, 1206, 2622, 0,
    BluetoothMouseDirection.Up, BluetoothMouseDirection.Up, true, true);
Equal((-3d, 4d), reversed,
    "Bluetooth axis reversal applies after direction mapping");

var updateNetworkRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-update-network-{Guid.NewGuid():N}");
try
{
    var releaseRequests = new List<string>();
    using var releaseHttpClient = new HttpClient(new StubHttpMessageHandler(
        (request, _) =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            releaseRequests.Add(host);
            if (host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("simulated GitHub API outage"));
            if (host.Equals("raw.githubusercontent.com",
                    StringComparison.OrdinalIgnoreCase) &&
                request.RequestUri?.AbsolutePath.Contains("/docs/releases/",
                    StringComparison.Ordinal) == true)
                return Task.FromResult(HttpResponse(request,
                    new StringContent("# Stable release\n\n完整中文说明\n\n### Fixed\n\nFull English notes.",
                        System.Text.Encoding.UTF8, "text/markdown")));
            if (host.Equals("raw.githubusercontent.com",
                    StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(HttpResponse(request,
                    new StringContent(releaseFixture,
                        System.Text.Encoding.UTF8, "application/json")));
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException($"unexpected endpoint {host}"));
        }));
    using var releaseClient = new GitHubReleaseClient(releaseHttpClient,
        Path.Combine(updateNetworkRoot, "release-check"));
    var fallbackRelease = await releaseClient.GetLatestAsync(new UpdateSettings
    {
        AllowMirrorFallback = true,
        NotifyStableReleases = true,
        NotifyPrereleaseReleases = false,
    });
    Equal("v1.3.1", fallbackRelease?.TagName,
        "release check falls back to official GitHub Raw metadata when the API fails");
    Sequence(["api.github.com", "raw.githubusercontent.com"], releaseRequests,
        "release metadata lookup does not fetch full notes before version comparison");
    fallbackRelease = await releaseClient.EnrichReleaseNotesAsync(fallbackRelease!);
    Equal(true, fallbackRelease.Body.Contains("完整中文说明",
            StringComparison.Ordinal) == true,
        "fallback metadata is enriched with the complete version release notes");
    Sequence(["api.github.com", "raw.githubusercontent.com", "raw.githubusercontent.com"], releaseRequests,
        "release check uses only official GitHub metadata endpoints");

    await ThrowsAsync<HttpRequestException>(async () =>
        await releaseClient.GetLatestAsync(new UpdateSettings
        {
            AllowMirrorFallback = false,
            NotifyStableReleases = true,
        }), "disabled release mirror fallback does not contact alternate endpoints");
    Equal(2, releaseRequests.Count(host => host.Equals("raw.githubusercontent.com",
            StringComparison.OrdinalIgnoreCase)),
        "disabled metadata fallback does not add another GitHub Raw request");

    var oversizedNotes = new string('x', 1024 * 1024 + 1);
    using var oversizedNotesHttpClient = new HttpClient(new StubHttpMessageHandler(
        (request, _) => Task.FromResult(HttpResponse(request,
            new StringContent(oversizedNotes, System.Text.Encoding.UTF8,
                "text/markdown")))));
    using var oversizedNotesClient = new GitHubReleaseClient(oversizedNotesHttpClient,
        Path.Combine(updateNetworkRoot, "oversized-notes"));
    var oversizedNotesRelease = fallbackRelease with { Body = "metadata notes" };
    var limitedNotesRelease = await oversizedNotesClient.EnrichReleaseNotesAsync(
        oversizedNotesRelease);
    Equal("metadata notes", limitedNotesRelease.Body,
        "oversized release notes fall back to the bounded metadata body");

    var oversizedReleaseList = System.Text.Encoding.UTF8.GetBytes(
        new string('x', 4 * 1024 * 1024 + 1));
    using var oversizedReleaseListHttpClient = new HttpClient(
        new StubHttpMessageHandler((request, _) => Task.FromResult(
            HttpResponse(request, new UnknownLengthContent(oversizedReleaseList)))));
    using var oversizedReleaseListClient = new GitHubReleaseClient(
        oversizedReleaseListHttpClient,
        Path.Combine(updateNetworkRoot, "oversized-release-list"));
    await ThrowsAsync<HttpRequestException>(() =>
        oversizedReleaseListClient.GetLatestAsync(new UpdateSettings
        {
            AllowMirrorFallback = false,
            NotifyStableReleases = true,
        }), "unknown-length release metadata is rejected at the streaming limit");

    var payload = System.Text.Encoding.UTF8.GetBytes("verified mirror payload");
    var payloadHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
    var downloadAsset = new ReleaseAsset(
        "iPhoneMirror-Setup-v9.9.9-x64.exe",
        new Uri("https://github.com/RayrenSX/iPhoneMirror/releases/download/v9.9.9/iPhoneMirror-Setup-v9.9.9-x64.exe"),
        payload.LongLength, payloadHash);
    var downloadRelease = new ReleaseInfo("v9.9.9", "Mirror test", string.Empty,
        DateTimeOffset.UtcNow, SemanticVersion.Parse("v9.9.9"), false,
        downloadAsset, null, null);

    var checksumAsset = new ReleaseAsset("SHA256SUMS.txt",
        new Uri("https://github.com/RayrenSX/iPhoneMirror/releases/download/v9.9.9/SHA256SUMS.txt"),
        0);
    var checksumRelease = downloadRelease with
    {
        InstallerAsset = downloadAsset with { Sha256 = null },
        ChecksumAsset = checksumAsset,
    };
    var oversizedChecksum = new byte[1024 * 1024 + 1];
    using var oversizedChecksumHttpClient = new HttpClient(
        new StubHttpMessageHandler((request, _) =>
        {
            if (request.Headers.Range is not null)
                return Task.FromResult(HttpResponse(request, new ByteArrayContent(payload)));
            return Task.FromResult(HttpResponse(request,
                new UnknownLengthContent(oversizedChecksum)));
        }));
    using var oversizedChecksumClient = new GitHubReleaseClient(
        oversizedChecksumHttpClient,
        Path.Combine(updateNetworkRoot, "oversized-checksum"));
    await ThrowsAsync<InvalidDataException>(() => oversizedChecksumClient.DownloadAsync(
            checksumRelease, allowMirrorFallback: false, preferInstaller: true),
        "unknown-length checksum manifest is rejected at the streaming limit");

    var pingRequests = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var throughputRequests =
        new System.Collections.Concurrent.ConcurrentQueue<string>();
    var packageRequests = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var downloadHttpClient = new HttpClient(new StubHttpMessageHandler(
        async (request, cancellationToken) =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            if (request.Method == HttpMethod.Head)
            {
                pingRequests.Enqueue(host);
                return HttpResponse(request, new ByteArrayContent([]));
            }
            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            if (range?.To == 0)
            {
                packageRequests.Enqueue(host);
                return HttpResponse(request, new ByteArrayContent(payload));
            }

            throughputRequests.Enqueue(host);
            var delay = host.ToLowerInvariant() switch
            {
                "github.cnxiaobai.com" => 5,
                // The probe fan-out covers every configured mirror. Keep a
                // meaningful gap here so thread-pool scheduling jitter cannot
                // turn this throughput-ranking test into a timing race.
                "github.com" => 100,
                _ => 500,
            };
            await Task.Delay(delay, cancellationToken);
            return RangeResponse(request, payload);
        }));
    using var downloadClient = new GitHubReleaseClient(downloadHttpClient,
        Path.Combine(updateNetworkRoot, "downloads"));
    var downloaded = await downloadClient.DownloadAsync(downloadRelease,
        cancellationToken: default, allowMirrorFallback: true,
        preferInstaller: true);
    Equal(true, downloaded.HashVerified,
        "mirror download is accepted only after SHA256 verification");
    Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
        downloaded.VerifiedSha256,
        "download result retains the digest verified from release metadata");
    Equal(true, File.ReadAllBytes(downloaded.Path).SequenceEqual(payload),
        "mirror download preserves the verified payload exactly");
    Sequence(["github.cnxiaobai.com"], packageRequests,
        "asset download uses the fastest responsive mirror");
    Equal(116, pingRequests.Count,
        "availability probing includes GitHub and all mirrors");
    Equal(116, throughputRequests.Count,
        "throughput probing includes every reachable candidate");
    Equal(true, throughputRequests.Contains("github.com"),
        "official GitHub participates in throughput ranking");

    var officialPingRequests =
        new System.Collections.Concurrent.ConcurrentQueue<string>();
    var officialThroughputRequests =
        new System.Collections.Concurrent.ConcurrentQueue<string>();
    var officialPackageRequests =
        new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var officialFallbackHttpClient = new HttpClient(
        new StubHttpMessageHandler((request, _) =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            if (request.Method == HttpMethod.Head)
            {
                officialPingRequests.Enqueue(host);
                return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    ? Task.FromResult(HttpResponse(request,
                        new ByteArrayContent([])))
                    : Task.FromException<HttpResponseMessage>(
                        new HttpRequestException("simulated unreachable mirror"));
            }
            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            if (range?.To == 0)
            {
                officialPackageRequests.Enqueue(host);
                return Task.FromResult(HttpResponse(request,
                    new ByteArrayContent(payload)));
            }
            officialThroughputRequests.Enqueue(host);
            return Task.FromResult(RangeResponse(request, payload));
        }));
    using var officialFallbackClient = new GitHubReleaseClient(
        officialFallbackHttpClient,
        Path.Combine(updateNetworkRoot, "official-fallback"));
    var officialDownload = await officialFallbackClient.DownloadAsync(
        downloadRelease, cancellationToken: default,
        allowMirrorFallback: true, preferInstaller: true);
    Equal(true, officialDownload.HashVerified,
        "official fallback remains SHA256 verified");
    Equal(116, officialPingRequests.Count,
        "the reachability stage pings every configured candidate");
    Sequence(["github.com"], officialThroughputRequests,
        "only reachable candidates receive a throughput sample");
    Sequence(["github.com"], officialPackageRequests,
        "unreachable mirrors leave official GitHub as the download route");

    var failoverPackageRequests =
        new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var failoverHttpClient = new HttpClient(new StubHttpMessageHandler(
        async (request, cancellationToken) =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            if (request.Method == HttpMethod.Head)
                return HttpResponse(request, new ByteArrayContent([]));
            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            if (range?.To == 0)
            {
                failoverPackageRequests.Enqueue(host);
                if (host.Equals("gh-proxy.net",
                        StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException(
                        "simulated fastest mirror download failure");
                return HttpResponse(request, new ByteArrayContent(payload));
            }
            var delay = host.ToLowerInvariant() switch
            {
                "gh-proxy.net" => 5,
                "github.com" => 100,
                _ => 500,
            };
            await Task.Delay(delay, cancellationToken);
            return RangeResponse(request, payload);
        }));
    using var failoverClient = new GitHubReleaseClient(failoverHttpClient,
        Path.Combine(updateNetworkRoot, "ranked-failover"));
    var failoverDownload = await failoverClient.DownloadAsync(downloadRelease,
        cancellationToken: default, allowMirrorFallback: true,
        preferInstaller: true);
    Equal(true, failoverDownload.HashVerified,
        "ranked failover download remains SHA256 verified");
    Sequence(["gh-proxy.net", "github.com"], failoverPackageRequests,
        "a failed fastest mirror immediately switches to the next ranked route");
    Throws<InvalidDataException>(() => UpdateInstallerLauncher.Launch(
            downloaded with { HashVerified = false }),
        "installer launcher refuses an update without verified integrity");
}
finally
{
    if (Directory.Exists(updateNetworkRoot))
        Directory.Delete(updateNetworkRoot, recursive: true);
}

var updateCleanupRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-update-cleanup-{Guid.NewGuid():N}");
var updateCleanupOutside = updateCleanupRoot + "-outside";
var updateCleanupLink = Path.Combine(updateCleanupRoot, "linked");
try
{
    Directory.CreateDirectory(Path.Combine(updateCleanupRoot, "release"));
    Directory.CreateDirectory(updateCleanupOutside);
    var localPartial = Path.Combine(updateCleanupRoot, "release", "local.download");
    var localCompleted = Path.Combine(updateCleanupRoot, "release", "local.zip");
    var outsidePartial = Path.Combine(updateCleanupOutside, "outside.download");
    File.WriteAllText(localPartial, "partial");
    File.WriteAllText(localCompleted, "complete");
    File.WriteAllText(outsidePartial, "outside");
    try
    {
        Directory.CreateSymbolicLink(updateCleanupLink, updateCleanupOutside);
    }
    catch (Exception error) when (error is UnauthorizedAccessException ||
                                  (error is IOException &&
                                   (error.HResult & 0xffff) == 1314))
    {
        updateCleanupLink = string.Empty;
    }

    GitHubReleaseClient.CleanupInterruptedDownloads(updateCleanupRoot);
    Equal(false, File.Exists(localPartial),
        "interrupted update cleanup deletes local partial downloads");
    Equal(true, File.Exists(localCompleted),
        "interrupted update cleanup preserves completed downloads");
    Equal(true, File.Exists(outsidePartial),
        "interrupted update cleanup never follows directory reparse points");

    var cleanup = GitHubReleaseClient.CleanupOldDownloads(updateCleanupRoot,
        includeCompleted: true);
    Equal(1, cleanup.DeletedFiles,
        "manual update cleanup counts only files inside the cache root");
    Equal(true, File.Exists(outsidePartial),
        "manual update cleanup never deletes through directory reparse points");
}
finally
{
    if (!string.IsNullOrEmpty(updateCleanupLink) && Directory.Exists(updateCleanupLink))
        Directory.Delete(updateCleanupLink);
    if (Directory.Exists(updateCleanupRoot))
        Directory.Delete(updateCleanupRoot, recursive: true);
    if (Directory.Exists(updateCleanupOutside))
        Directory.Delete(updateCleanupOutside, recursive: true);
}

var zipUpdateTestRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-zip-update-{Guid.NewGuid():N}");
var zipUpdatePreviousDelayedExit = Environment.GetEnvironmentVariable(
    delayedExitEnvironment);
try
{
    var successInstall = Path.Combine(zipUpdateTestRoot, "success-install");
    var successPayload = Path.Combine(zipUpdateTestRoot, "success-payload");
    var successZip = Path.Combine(zipUpdateTestRoot, "success.zip");
    Directory.CreateDirectory(successInstall);
    Directory.CreateDirectory(successPayload);
    File.WriteAllText(Path.Combine(successInstall, "iPhoneMirror.exe"), "old-app");
    File.WriteAllText(Path.Combine(successInstall, "iPhoneMirror.Driver.exe"), "old-driver");
    File.WriteAllText(Path.Combine(successPayload, "iPhoneMirror.exe"), "new-app");
    File.WriteAllText(Path.Combine(successPayload, "iPhoneMirror.Driver.exe"), "new-driver");
    Directory.CreateDirectory(Path.Combine(successPayload, "nested"));
    File.WriteAllText(Path.Combine(successPayload, "nested", "runtime.dll"), "runtime");
    ZipFile.CreateFromDirectory(successPayload, successZip,
        CompressionLevel.NoCompression, includeBaseDirectory: false);
    Environment.SetEnvironmentVariable(delayedExitEnvironment, "50");
    var successResult = await RunWindowsPowerShellAsync(script: Path.Combine(
            sourceDirectory, "App", "tools", "updater", "Apply-ZipUpdate.ps1"),
        zipPath: successZip, installDirectory: successInstall,
        restartExecutable: Environment.ProcessPath!, skipRestart: true);
    Equal(0, successResult.ExitCode,
        $"Windows PowerShell portable update succeeds: {successResult.Output}");
    Equal("new-app", File.ReadAllText(Path.Combine(successInstall, "iPhoneMirror.exe")),
        "Windows PowerShell portable update replaces the application");
    Equal("new-driver", File.ReadAllText(Path.Combine(successInstall,
            "iPhoneMirror.Driver.exe")),
        "Windows PowerShell portable update replaces the driver manager");
    Equal("runtime", File.ReadAllText(Path.Combine(successInstall,
            "nested", "runtime.dll")),
        "Windows PowerShell portable update copies nested payload files");

    var tamperedInstall = Path.Combine(zipUpdateTestRoot, "tampered-install");
    Directory.CreateDirectory(tamperedInstall);
    File.WriteAllText(Path.Combine(tamperedInstall, "iPhoneMirror.exe"), "old-app");
    File.WriteAllText(Path.Combine(tamperedInstall, "iPhoneMirror.Driver.exe"), "old-driver");
    var tamperedResult = await RunWindowsPowerShellAsync(Path.Combine(
            sourceDirectory, "App", "tools", "updater", "Apply-ZipUpdate.ps1"),
        successZip, tamperedInstall, Environment.ProcessPath!, new string('0', 64));
    Equal(true, tamperedResult.ExitCode != 0 && tamperedResult.Output.Contains(
            "changed after verification", StringComparison.Ordinal),
        $"portable updater rejects a ZIP whose digest changed: {tamperedResult.Output}");
    Equal("old-app", File.ReadAllText(Path.Combine(tamperedInstall,
            "iPhoneMirror.exe")),
        "rejected tampered ZIP leaves the installed application unchanged");

    var unsafePathInstall = Path.Combine(zipUpdateTestRoot, "unsafe-path-install");
    var unsafePathZip = Path.Combine(zipUpdateTestRoot, "unsafe-path.zip");
    Directory.CreateDirectory(unsafePathInstall);
    File.WriteAllText(Path.Combine(unsafePathInstall, "iPhoneMirror.exe"), "old-app");
    File.WriteAllText(Path.Combine(unsafePathInstall, "iPhoneMirror.Driver.exe"),
        "old-driver");
    using (var archive = ZipFile.Open(unsafePathZip, ZipArchiveMode.Create))
    {
        foreach (var name in new[]
                 {
                     "iPhoneMirror.exe", "iPhoneMirror.Driver.exe",
                     "iPhoneMirror.exe:payload",
                 })
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("payload");
        }
    }
    var unsafePathResult = await RunWindowsPowerShellAsync(Path.Combine(
            sourceDirectory, "App", "tools", "updater", "Apply-ZipUpdate.ps1"),
        unsafePathZip, unsafePathInstall, Environment.ProcessPath!);
    Equal(true, unsafePathResult.ExitCode != 0 && unsafePathResult.Output.Contains(
            "unsafe path", StringComparison.Ordinal),
        $"Windows PowerShell portable update rejects NTFS stream paths: " +
        unsafePathResult.Output);
    Equal("old-app", File.ReadAllText(Path.Combine(unsafePathInstall,
            "iPhoneMirror.exe")),
        "rejected unsafe ZIP path leaves the installed application unchanged");

    var ratioInstall = Path.Combine(zipUpdateTestRoot, "ratio-install");
    var ratioZip = Path.Combine(zipUpdateTestRoot, "ratio.zip");
    Directory.CreateDirectory(ratioInstall);
    File.WriteAllText(Path.Combine(ratioInstall, "iPhoneMirror.exe"), "old-app");
    File.WriteAllText(Path.Combine(ratioInstall, "iPhoneMirror.Driver.exe"), "old-driver");
    using (var archive = ZipFile.Open(ratioZip, ZipArchiveMode.Create))
    {
        foreach (var name in new[] { "iPhoneMirror.exe", "iPhoneMirror.Driver.exe" })
        {
            var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
            using var stream = entry.Open();
            stream.Write(new byte[2 * 1024 * 1024]);
        }
    }
    var ratioResult = await RunWindowsPowerShellAsync(Path.Combine(
            sourceDirectory, "App", "tools", "updater", "Apply-ZipUpdate.ps1"),
        ratioZip, ratioInstall, Environment.ProcessPath!);
    Equal(true, ratioResult.ExitCode != 0 && ratioResult.Output.Contains(
            "compression-ratio limit", StringComparison.Ordinal),
        $"Windows PowerShell portable update rejects high-ratio ZIPs: {ratioResult.Output}");
    Equal("old-app", File.ReadAllText(Path.Combine(ratioInstall,
            "iPhoneMirror.exe")),
        "rejected high-ratio ZIP leaves the installed application unchanged");

    var rollbackInstall = Path.Combine(zipUpdateTestRoot, "rollback-install");
    var rollbackPayload = Path.Combine(zipUpdateTestRoot, "rollback-payload");
    var rollbackZip = Path.Combine(zipUpdateTestRoot, "rollback.zip");
    var obsoleteDirectory = Path.Combine(rollbackInstall, "tools", "ffmpeg");
    Directory.CreateDirectory(obsoleteDirectory);
    Directory.CreateDirectory(rollbackPayload);
    File.WriteAllText(Path.Combine(rollbackInstall, "iPhoneMirror.exe"), "old-app");
    File.WriteAllText(Path.Combine(rollbackInstall, "iPhoneMirror.Driver.exe"), "old-driver");
    File.WriteAllText(Path.Combine(rollbackPayload, "iPhoneMirror.exe"), "new-app");
    File.WriteAllText(Path.Combine(rollbackPayload, "iPhoneMirror.Driver.exe"), "new-driver");
    var lockedObsolete = Path.Combine(obsoleteDirectory, "ffmpeg.exe");
    File.WriteAllText(lockedObsolete, "locked-runtime");
    ZipFile.CreateFromDirectory(rollbackPayload, rollbackZip,
        CompressionLevel.NoCompression, includeBaseDirectory: false);
    await using (var lockStream = new FileStream(lockedObsolete, FileMode.Open,
                     FileAccess.ReadWrite, FileShare.None))
    {
        var rollbackResult = await RunWindowsPowerShellAsync(Path.Combine(
                sourceDirectory, "App", "tools", "updater", "Apply-ZipUpdate.ps1"),
            rollbackZip, rollbackInstall, Environment.ProcessPath!);
        Equal(true, rollbackResult.ExitCode != 0,
            "Windows PowerShell portable update reports an injected copy failure");
    }
    Equal("old-app", File.ReadAllText(Path.Combine(rollbackInstall,
            "iPhoneMirror.exe")),
        "failed Windows PowerShell update restores the application");
    Equal("old-driver", File.ReadAllText(Path.Combine(rollbackInstall,
            "iPhoneMirror.Driver.exe")),
        "failed Windows PowerShell update restores the driver manager");
}
finally
{
    Environment.SetEnvironmentVariable(delayedExitEnvironment,
        zipUpdatePreviousDelayedExit);
    if (Directory.Exists(zipUpdateTestRoot))
        Directory.Delete(zipUpdateTestRoot, recursive: true);
}
Equal(true, UpdateInstallerLauncher.BuildInstallerArguments()
        .Contains("/STARTAPP=1", StringComparison.Ordinal),
    "one-click installer update requests a delayed application restart");
Equal(false, UpdateInstallerLauncher.BuildInstallerArguments()
        .Contains("/RESTARTAPP=1", StringComparison.Ordinal),
    "one-click installer update does not use Inno's immediate restart parameter");
Equal(true, UpdateInstallerLauncher.BuildInstallerArguments()
        .Contains("/LOG=", StringComparison.Ordinal),
    "one-click installer update persists an installer log");
var elevatedUpdateStart = UpdateInstallerLauncher.BuildElevatedPowerShellStartInfo(
    Path.GetTempPath(), Convert.ToBase64String(Encoding.Unicode.GetBytes("exit 0")));
Equal(true, elevatedUpdateStart.UseShellExecute &&
            elevatedUpdateStart.Verb.Equals("runas", StringComparison.OrdinalIgnoreCase),
    "update helper preserves UAC elevation through the Windows shell");
var unelevatedUpdateStart = UpdateInstallerLauncher.BuildUnelevatedPowerShellStartInfo(
    Path.GetTempPath(), Convert.ToBase64String(Encoding.Unicode.GetBytes("exit 0")));
Equal(true, unelevatedUpdateStart.UseShellExecute &&
            string.IsNullOrEmpty(unelevatedUpdateStart.Verb),
    "writable portable copies keep the update helper at caller integrity");
Equal(true, UpdateInstallerLauncher.CanUpdateDirectoryWithoutElevation(
        Path.GetTempPath()),
    "portable updater detects a writable installation directory without UAC");
var writablePortableRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-elevation-check-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(writablePortableRoot);
    Equal(false, UpdateInstallerLauncher.CanSafelyElevateDirectoryTree(
            writablePortableRoot),
        "portable updater never elevates a user-writable installation tree");
}
finally
{
    if (Directory.Exists(writablePortableRoot))
        Directory.Delete(writablePortableRoot, recursive: true);
}
var topologyWritableRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-topology-check-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(topologyWritableRoot);
    var currentUser = WindowsIdentity.GetCurrent().User ??
        throw new InvalidOperationException("The test user SID is unavailable.");
    var topologySecurity = new DirectorySecurity();
    topologySecurity.SetOwner(currentUser);
    topologySecurity.SetAccessRuleProtection(isProtected: true,
        preserveInheritance: false);
    topologySecurity.AddAccessRule(new FileSystemAccessRule(currentUser,
        FileSystemRights.ReadAndExecute | FileSystemRights.CreateDirectories |
        FileSystemRights.Delete, AccessControlType.Allow));
    new DirectoryInfo(topologyWritableRoot).SetAccessControl(topologySecurity);
    Equal(false, UpdateInstallerLauncher.CanUpdateDirectoryWithoutElevation(
            topologyWritableRoot),
        "topology-only ACL does not pass the ordinary file-write probe");
    Equal(false, UpdateInstallerLauncher.CanSafelyElevateDirectoryTree(
            topologyWritableRoot),
        "portable updater rejects topology rights hidden from the file-write probe");
}
finally
{
    if (Directory.Exists(topologyWritableRoot))
        Directory.Delete(topologyWritableRoot, recursive: true);
}
Equal(System.Diagnostics.ProcessWindowStyle.Hidden, elevatedUpdateStart.WindowStyle,
    "update helper asks Windows to hide the elevated PowerShell host");
var elevatedUpdateArguments = elevatedUpdateStart.ArgumentList.ToArray();
var windowStyleArgument = Array.IndexOf(elevatedUpdateArguments, "-WindowStyle");
Equal(true, windowStyleArgument >= 0 &&
            windowStyleArgument + 1 < elevatedUpdateArguments.Length &&
            elevatedUpdateArguments[windowStyleArgument + 1].Equals(
                "Hidden", StringComparison.OrdinalIgnoreCase),
    "update helper passes PowerShell an explicit hidden-window mode");
var updateLauncherCode = File.ReadAllText(Path.Combine(sourceDirectory, "App",
    "Updater", "UpdateInstallerLauncher.cs"));
Equal(true, updateLauncherCode.Contains("SeeMaskNoConsole", StringComparison.Ordinal) &&
            updateLauncherCode.Contains("ShellExecuteExW", StringComparison.Ordinal) &&
            updateLauncherCode.Contains("Mask = SeeMaskNoCloseProcess | SeeMaskNoConsole",
                StringComparison.Ordinal),
    "elevated update helper requests ShellExecuteEx no-console mode");
Equal(true, updateLauncherCode.Contains("FileAddSubdirectory",
                StringComparison.Ordinal) &&
            updateLauncherCode.Contains("FileDeleteChild", StringComparison.Ordinal) &&
            updateLauncherCode.Contains("WriteDac", StringComparison.Ordinal) &&
            updateLauncherCode.Contains("Directory.GetParent(current)",
                StringComparison.Ordinal) &&
            updateLauncherCode.Contains("FileFlagOpenReparsePoint",
                StringComparison.Ordinal),
    "portable updater checks topology and ancestor mutation rights before UAC");
Equal(true, updateLauncherCode.IndexOf("if (IsCurrentProcessElevated())",
                StringComparison.Ordinal) <
            updateLauncherCode.IndexOf("var helperBytes = ReadZipHelperBytes();",
                StringComparison.Ordinal),
    "portable updater validates its privilege boundary before creating a helper");
var frameExchangeCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "VirtualCamera", "src", "FrameExchange.cpp"));
Equal(true, !frameExchangeCode.Contains("(A;;GR;;;AC)",
                StringComparison.Ordinal) &&
            frameExchangeCode.Contains("FILE_FLAG_FIRST_PIPE_INSTANCE",
                StringComparison.Ordinal),
    "virtual camera frame channel excludes arbitrary AppContainers");
var updateWindowCode = File.ReadAllText(Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "App", "Windows",
    "UpdateWindow.xaml.cs")));
Equal(false, updateWindowCode.Contains("MainWindow?.Close()", StringComparison.Ordinal),
    "installer update leaves the current version open until Setup closes it");
UpdateInstallerLauncher.ValidateAssetForDeployment("setup.exe",
    sharedRuntime: true);
UpdateInstallerLauncher.ValidateAssetForDeployment("portable.zip",
    sharedRuntime: false);
Throws<InvalidOperationException>(() =>
        UpdateInstallerLauncher.ValidateAssetForDeployment("portable.zip",
            sharedRuntime: true),
    "installed deployment refuses a ZIP overlay update");
Throws<InvalidOperationException>(() =>
        UpdateInstallerLauncher.ValidateAssetForDeployment("setup.exe",
            sharedRuntime: false),
    "portable deployment refuses migration to Setup during automatic update");
var zipWaitArguments = UpdateInstallerLauncher.BuildZipArguments(
    "update.zip", "C:\\install",
    "C:\\install\\iPhoneMirror.exe", new string('a', 64), [17, 29, 17]);
Equal(true, zipWaitArguments.Contains("-WaitPids", StringComparer.Ordinal) &&
            zipWaitArguments.Contains("17;29", StringComparer.Ordinal),
    "portable update helper receives both main and driver process IDs");
Equal(true, zipWaitArguments.Contains("-ExpectedSha256", StringComparer.Ordinal) &&
            zipWaitArguments.Contains(new string('a', 64), StringComparer.Ordinal),
    "portable update helper receives the verified package digest");

var bootstrapRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-update-bootstrap-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(bootstrapRoot);
    var bootstrapScript = Path.Combine(bootstrapRoot, "helper.ps1");
    var bootstrapOutput = Path.Combine(bootstrapRoot, "output.txt");
    File.WriteAllText(bootstrapScript,
        "param([string]$Output,[string]$Value) " +
        "[IO.File]::WriteAllText($Output,$Value)", new UTF8Encoding(false));
    var bootstrapDigest = Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(bootstrapScript)));
    var encodedBootstrap = UpdateInstallerLauncher.BuildVerifiedScriptBootstrap(
        bootstrapScript, bootstrapDigest,
        ["-Output", bootstrapOutput, "-Value", "verified"]);
    var bootstrapStart = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var argument in new[]
             {
                 "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                 "-EncodedCommand", encodedBootstrap,
             })
        bootstrapStart.ArgumentList.Add(argument);
    using (var process = System.Diagnostics.Process.Start(bootstrapStart) ??
                         throw new InvalidOperationException(
                             "verified helper bootstrap did not start"))
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Equal(0, process.ExitCode,
            "verified helper bootstrap executes the expected script");
    }
    Equal("verified", File.ReadAllText(bootstrapOutput),
        "verified helper bootstrap preserves structured arguments");

    File.AppendAllText(bootstrapScript, "#tampered");
    var rejectedBootstrap = UpdateInstallerLauncher.BuildVerifiedScriptBootstrap(
        bootstrapScript, bootstrapDigest,
        ["-Output", bootstrapOutput, "-Value", "tampered"]);
    var rejectedStart = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var argument in new[]
             {
                 "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                 "-EncodedCommand", rejectedBootstrap,
             })
        rejectedStart.ArgumentList.Add(argument);
    using (var process = System.Diagnostics.Process.Start(rejectedStart) ??
                         throw new InvalidOperationException(
                             "tampered helper bootstrap did not start"))
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Equal(true, process.ExitCode != 0,
            "verified helper bootstrap rejects a changed script");
    }
    Equal("verified", File.ReadAllText(bootstrapOutput),
        "rejected helper script cannot run after it is changed");
}
finally
{
    if (Directory.Exists(bootstrapRoot))
        Directory.Delete(bootstrapRoot, recursive: true);
}

var packageLockRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-update-lock-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(packageLockRoot);
    var package = Path.Combine(packageLockRoot, "update.bin");
    File.WriteAllText(package, "verified update");
    var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package)));
    using (UpdateInstallerLauncher.LockAndValidatePackage(package, digest))
    {
        Throws<IOException>(() => File.AppendAllText(package, "tampered"),
            "verified update lock blocks writes");
        Throws<IOException>(() => File.Delete(package),
            "verified update lock blocks replacement");
    }
    File.AppendAllText(package, " released");
    Throws<InvalidDataException>(() =>
            UpdateInstallerLauncher.LockAndValidatePackage(package, digest).Dispose(),
        "update launcher rejects a package changed after download verification");
}
finally
{
    if (Directory.Exists(packageLockRoot))
        Directory.Delete(packageLockRoot, recursive: true);
}

// usbmux may reverse its enumeration order on every poll. Existing cards must
// never move, while a newly connected phone is appended exactly once.
Sequence(["phone-a", "phone-b"],
    StableDeviceSelection.MergeVisibleOrder(["phone-a", "phone-b"], ["phone-b", "phone-a"]),
    "reversed discovery keeps visible order");
Sequence(["phone-a", "phone-b", "phone-c"],
    StableDeviceSelection.MergeVisibleOrder(["phone-a", "phone-b"], ["phone-c", "phone-b", "phone-a"]),
    "new phone appends without moving selection");
Sequence(["phone-b", "phone-c"],
    StableDeviceSelection.MergeVisibleOrder(["phone-a", "phone-b", "phone-c"], ["phone-c", "phone-b"]),
    "disconnected phone is removed without reordering survivors");
Equal("phone-b", StableDeviceSelection.ChooseUdid(["phone-a", "phone-b"], "PHONE-B", "phone-a"),
    "previous selection wins case-insensitively");
Equal("phone-a", StableDeviceSelection.ChooseUdid(["phone-a", "phone-b"], "missing", "PHONE-A"),
    "active capture is fallback selection");
Equal("airplay://phone-b", StableDeviceSelection.ChooseUdid(
        ["phone-a", "airplay://phone-b"], "phone-a", "phone-a", "AIRPLAY://PHONE-B"),
    "new wireless connection is selected once");
Equal("media-cast://active", StableDeviceSelection.ChooseUdid(
        ["media-cast://active", "airplay://phone-b"], "media-cast://active", null,
        "airplay://phone-b", preferNewlyConnectedWireless: false),
    "active media cast is not displaced by its AirPlay connection");
Equal("airplay://phone-b", StableDeviceSelection.FindNewlyConnected(
        ["airplay://phone-a"], ["airplay://phone-a", "airplay://phone-b"]),
    "new wireless edge is detected");
Equal<string?>(null, StableDeviceSelection.FindNewlyConnected(
        ["airplay://phone-a"], ["AIRPLAY://PHONE-A"]),
    "known wireless device is not selected repeatedly");
Equal(2, StableDeviceSelection.CalculateDropIndex(3, 0, 2, true),
    "dragging first device after last moves it to the end");
Equal(0, StableDeviceSelection.CalculateDropIndex(3, 2, 0, false),
    "dragging last device before first moves it to the start");

Equal("iPhone 12 mini", AppleProductNames.Resolve("iPhone13,1"),
    "known ProductType uses the real model name");
Equal("iPhone99,9", AppleProductNames.Resolve("iPhone99,9"),
    "unknown ProductType remains visible for diagnostics");

// Display outlines are selected from ProductType when available. Legacy
// Home-button displays remain rectangular, while metadata-less startup can
// use a conservative decoded-frame aspect fallback.
Equal("iphone-dynamic-island",
    DeviceCornerProfileResolver.Resolve("iPhone18,3", 1206, 2622).Id,
    "iPhone17 family profile");
Equal("iphone-x",
    DeviceCornerProfileResolver.Resolve("iPhone10,3", 1125, 2436).Id,
    "iPhone X profile");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve("iPhone10,1", 750, 1334).Id,
    "iPhone 8 remains rectangular");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve("iPhone14,6", 750, 1334).Id,
    "iPhone SE 3 remains rectangular");
Equal("iphone-notch",
    DeviceCornerProfileResolver.Resolve("iPhone17,5", 1170, 2532).Id,
    "iPhone 16e retains notch profile");
Equal("iphone-12-mini",
    DeviceCornerProfileResolver.Resolve("iPhone13,1", 1080, 2340).Id,
    "iPhone 12 mini has a tighter device-specific curve");
Equal("iphone-13-mini",
    DeviceCornerProfileResolver.Resolve("iPhone14,4", 1080, 2340).Id,
    "iPhone 13 mini has a device-specific curve");
Equal("ipad-pro-rounded",
    DeviceCornerProfileResolver.Resolve("iPad8,1", 1668, 2388).Id,
    "2018 iPad Pro profile");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve("iPad12,1", 1620, 2160).Id,
    "Home-button iPad remains rectangular");
Equal("ipad-mini-rounded",
    DeviceCornerProfileResolver.Resolve("iPad14,1", 1488, 2266).Id,
    "iPad mini profile");
Equal("ipad-rounded",
    DeviceCornerProfileResolver.Resolve("iPad15,7", 1640, 2360).Id,
    "iPad A16 base profile");
Equal("ipad-pro-rounded",
    DeviceCornerProfileResolver.Resolve("iPad17,1", 1668, 2420).Id,
    "iPad M5 Pro profile");
Equal("iphone-dynamic-island",
    DeviceCornerProfileResolver.Resolve(null, 1206, 2622).Id,
    "unknown phone geometry fallback");
Equal("ipad-rounded",
    DeviceCornerProfileResolver.Resolve(null, 1640, 2360).Id,
    "unknown tablet geometry fallback");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve(null, 1000, 1000).Id,
    "ambiguous geometry does not clip");
Equal(0, DeviceCornerProfile.Rectangular.GetGdiRadius(1206),
    "rectangular fallback radius");

Equal("iPhoneMirror AirPlay",
    WirelessReceiverConfiguration.SanitizeReceiverName("  iPhoneMirror AirPlay  "),
    "wireless receiver name is trimmed");
Equal("iPhoneMirror AirPlay",
    WirelessReceiverConfiguration.SanitizeReceiverName("\r\n[];"),
    "invalid wireless receiver name falls back");
Equal(63,
    WirelessReceiverConfiguration.SanitizeReceiverName(new string('a', 80)).Length,
    "wireless receiver name respects the mDNS label limit");
Equal("1080p", WirelessReceiverConfiguration.DefaultDisplayProfile.Id,
    "wireless receiver defaults to the balanced 1080p profile");
Equal(true, WirelessReceiverConfiguration.RequiresOriginalQualityWarning(
        WirelessReceiverConfiguration.DisplayProfiles[0]),
    "wireless original-quality profile requires a stability warning");
Equal(false, WirelessReceiverConfiguration.RequiresOriginalQualityWarning(
        WirelessReceiverConfiguration.DisplayProfiles[1]),
    "wireless 1080p profile does not require the original-quality warning");
Equal(true, WirelessReceiverConfiguration.IsSupportedDisplayProfile(1280, 720, 30),
    "wireless 720p weak-network profile is supported");
Equal(false, WirelessReceiverConfiguration.IsSupportedDisplayProfile(1280, 720, 60),
    "unsupported wireless profile combinations are rejected");
Equal(true, WirelessReceiverService.IsCodeIntegrityError(4551),
    "wireless runtime recognizes Windows system integrity policy violations");
Equal(true, WirelessReceiverService.IsCodeIntegrityError(577),
    "wireless runtime recognizes invalid image hash policy failures");
Equal(WirelessRuntimeProbeStatus.CodeIntegrityBlocked,
    WirelessRuntimeProbeResult.FromExitCode(40).Status,
    "wireless runtime preflight maps policy blocks to a dedicated status");
Equal(WirelessRuntimeProbeStatus.Incompatible,
    WirelessRuntimeProbeResult.FromExitCode(42).Status,
    "wireless runtime preflight maps missing exports to incompatibility");
Equal(true, MediaSourceClassifier.IsLikelyLive(
        new Uri("https://example.test/video.m3u8")),
    "HLS extension is classified as live");
Equal(true, MediaSourceClassifier.IsLikelyLive(
        new Uri("https://example.test/live/playlist")),
    "extensionless live playlist is classified as live");
Equal(true, MediaSourceClassifier.IsLikelyLive(
        new Uri("https://example.test/watch?id=1&format=m3u8")),
    "HLS query hint is classified as live");
Equal(false, MediaSourceClassifier.IsLikelyLive(
        new Uri("https://example.test/library/video.mp4")),
    "ordinary MP4 remains on-demand media");
Equal(false, MediaCastPlaybackControls.IsReliableDuration(
        segmented: true, duration: 5.8),
    "a short HLS segment duration is not reported as the program duration");
Equal(0d, MediaCastPlaybackControls.ReportedDuration(
        segmented: true, duration: 5.8),
    "a short HLS segment reports an unknown total duration");
Equal(true, MediaCastPlaybackControls.IsReliableDuration(
        segmented: true, duration: 2400),
    "a long HLS duration can be used when the backend reports the full program");
Equal(false, MediaCastPlaybackControls.IsReliableDuration(
        segmented: true, duration: 95443.718),
    "a bogus multi-day HLS duration remains unknown");
var hlsBridgeArguments = HlsMediaPlaybackBridge.BuildArguments(
    new Uri("https://example.test/episode.m3u8?token=secret"),
    new Uri("http://127.0.0.1:18081/stream.ts"));
Equal(true,
    hlsBridgeArguments.Contains("-protocol_whitelist", StringComparer.Ordinal) &&
    hlsBridgeArguments.Contains("http,https,tcp,tls,crypto",
        StringComparer.Ordinal) &&
    !hlsBridgeArguments.Contains("-reconnect_at_eof", StringComparer.Ordinal) &&
    hlsBridgeArguments.Contains("mpegts", StringComparer.Ordinal) &&
    hlsBridgeArguments[^1] == "http://127.0.0.1:18081/stream.ts",
    "HLS playback uses FFmpeg playlist recovery and a continuous MPEG-TS bridge");
var hlsSeekArguments = HlsMediaPlaybackBridge.BuildArguments(
    new Uri("https://example.test/episode.m3u8"),
    new Uri("http://127.0.0.1:18081/stream.ts"), 123.5);
Equal(true,
    hlsSeekArguments.Contains("-ss", StringComparer.Ordinal) &&
    hlsSeekArguments.Contains("123.5", StringComparer.Ordinal) &&
    Array.IndexOf(hlsSeekArguments.ToArray(), "-ss") <
        Array.IndexOf(hlsSeekArguments.ToArray(), "-i"),
    "HLS bridge seeks the demuxer from the requested programme position");
Equal(true,
    hlsSeekArguments.Contains("-output_ts_offset", StringComparer.Ordinal) &&
    hlsSeekArguments.Contains("-123.5", StringComparer.Ordinal),
    "HLS bridge resets timestamps after an input seek");
var mediaAudioArguments = MediaCastAudioDecoder.BuildArguments(
    new Uri("https://example.test/episode.m3u8"), 123.5, 1.5);
Equal(true,
    mediaAudioArguments.Contains("-map", StringComparer.Ordinal) &&
    mediaAudioArguments.Contains("0:a:0?", StringComparer.Ordinal) &&
    mediaAudioArguments.Contains("pcm_s16le", StringComparer.Ordinal) &&
    mediaAudioArguments.Contains("atempo=1.5", StringComparer.Ordinal) &&
    mediaAudioArguments.Contains("123.5", StringComparer.Ordinal) &&
    !mediaAudioArguments.Contains("wasapi", StringComparer.Ordinal),
    "media output decodes the cast source audio instead of system loopback");
Equal(true, HlsMediaPlaybackBridge.TryParseDuration(
        "  Duration: 00:42:03.125, start: 1.400000, bitrate: N/A",
        out var parsedHlsDuration) && Math.Abs(parsedHlsDuration - 2523.125) < 0.001,
    "HLS bridge extracts the programme duration reported by FFmpeg");
Equal(false, HlsMediaPlaybackBridge.TryParseDuration(
        "  Duration: N/A, start: 12.000000, bitrate: N/A", out _),
    "HLS bridge does not invent a duration for a genuine live playlist");
Equal(0d, MediaCastPlaybackControls.ClampPosition(double.NaN, 100),
    "media controls reject a non-finite seek position");
Equal(100d, MediaCastPlaybackControls.ClampPosition(150, 100),
    "media controls clamp seeking to the known duration");
Equal("01:05", MediaCastPlaybackControls.FormatTime(65),
    "media controls format ordinary playback time");
Equal("1:01:01", MediaCastPlaybackControls.FormatTime(3661),
    "media controls retain hours for long videos");
Equal(true, MediaCastPlaybackControls.CanSeek(
        opened: true, isLive: false, duration: 30),
    "opened on-demand media can be scrubbed");
Equal(false, MediaCastPlaybackControls.CanSeek(
        opened: true, isLive: true, duration: 30),
    "live media does not expose a misleading fixed seek range");
Equal(true, MediaCastPlaybackControls.ShouldRetainPendingSeek(
        actualPosition: 10, targetPosition: 40,
        elapsed: TimeSpan.FromMilliseconds(250)),
    "a direct track click is not overwritten during the seek handoff");
Equal(true, MediaCastPlaybackControls.ShouldRetainPendingSeek(
        actualPosition: 40, targetPosition: 40,
        elapsed: TimeSpan.FromMilliseconds(250)),
    "an immediate position echo does not prematurely clear a pending seek");
Equal(false, MediaCastPlaybackControls.ShouldRetainPendingSeek(
        actualPosition: 41, targetPosition: 40,
        elapsed: TimeSpan.FromSeconds(1)),
    "a converged seek yields to the advancing playback clock");
Equal(false, MediaCastPlaybackControls.ShouldRetainPendingSeek(
        actualPosition: 10, targetPosition: 40,
        elapsed: TimeSpan.FromSeconds(11)),
    "a failed seek eventually releases its optimistic progress position");
Equal(true, MediaCastPlaybackControls.ShouldRetryPendingSeek(
        actualPosition: 10, targetPosition: 40,
        sinceLastAttempt: TimeSpan.FromMilliseconds(500), attemptCount: 1,
        buffering: false),
    "an ignored media seek is retried after the backend acknowledgement window");
Equal(false, MediaCastPlaybackControls.ShouldRetryPendingSeek(
        actualPosition: 10, targetPosition: 40,
        sinceLastAttempt: TimeSpan.FromSeconds(1), attemptCount: 1,
        buffering: true),
    "seek confirmation waits while the media backend is buffering");
Equal(false, MediaCastPlaybackControls.ShouldRetryPendingSeek(
        actualPosition: 10, targetPosition: 40,
        sinceLastAttempt: TimeSpan.FromSeconds(1), attemptCount: 20,
        buffering: false),
    "seek confirmation has a bounded retry count");
Equal(true, MediaCastPlaybackControls.ShouldRetryPendingSeek(
        actualPosition: 10, targetPosition: 40,
        sinceLastAttempt: TimeSpan.FromSeconds(1), attemptCount: 4,
        buffering: false),
    "slow media keeps retrying a pending seek beyond the original short window");
Equal(false, MediaCastPlaybackControls.ShouldRevealVideo(
        shouldPlay: true, buffering: true, openingPosition: 0,
        currentPosition: 1, openedFor: TimeSpan.FromSeconds(2)),
    "the loading card remains visible while the player is buffering");
Equal(true, MediaCastPlaybackControls.ShouldRevealVideo(
        shouldPlay: true, buffering: false, openingPosition: 0,
        currentPosition: 0.1, openedFor: TimeSpan.FromMilliseconds(100)),
    "the loading card yields when the first frame clock advances");

var ffmpegCapabilities = MediaOutputService.CreateCapabilities("ffmpeg.exe",
    " V..... h264_mf\n V..... libx264\n A..... aac\n A..... libopus ",
    "Input:\nrtmp\nOutput:\nrtmp\nsrt", " E flv\n E mpegts\n E whip");
Equal("h264_mf", ffmpegCapabilities.PreferredH264Encoder,
    "hardware encoding is preferred over libx264 when both are available");
Equal(true, ffmpegCapabilities.Supports(MediaOutputKind.Recording),
    "recording requires FFmpeg and an H.264 encoder");
Equal(true, ffmpegCapabilities.Supports(MediaOutputKind.Rtmp),
    "RTMP requires its protocol and FLV muxer");
Equal(true, ffmpegCapabilities.Supports(MediaOutputKind.Srt),
    "SRT requires its protocol and MPEG-TS muxer");
Equal(true, ffmpegCapabilities.Supports(MediaOutputKind.Whip),
    "WHIP requires its muxer");

var mediaFoundationOnly = MediaOutputService.CreateCapabilities("ffmpeg.exe",
    " V..... h264_mf\n A..... aac\n A..... libopus ", "rtmp", "flv");
Equal("h264_mf", mediaFoundationOnly.PreferredH264Encoder,
    "Media Foundation encoder is the hardware fallback");
var hardwareEncoderCandidates = MediaOutputService.FindH264EncoderCandidates(
    " V..... libx264 V..... h264_mf V..... h264_qsv " +
    "V..... h264_amf V..... h264_nvenc ");
Sequence(["h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libx264"],
    hardwareEncoderCandidates,
    "H.264 candidates prefer dedicated hardware and retain software fallback");
Equal(false, mediaFoundationOnly.Supports(MediaOutputKind.Srt),
    "missing SRT capability is gated");
Equal(string.Empty, MediaOutputService.SelectPreferredH264Encoder(
        " V..... libx264rgb "),
    "encoder selection requires an exact FFmpeg encoder token");
var noEncoder = MediaOutputService.CreateCapabilities("ffmpeg.exe",
    " V..... hevc_mf ", "rtmp srt", "flv mpegts whip");
Equal(false, noEncoder.Supports(MediaOutputKind.Recording),
    "protocol support cannot bypass a missing H.264 encoder");
Equal(false, noEncoder.Supports(MediaOutputKind.Rtmp),
    "RTMP is gated when no compatible encoder exists");
Equal(false, noEncoder.Supports((MediaOutputKind)999),
    "unknown output kinds are never reported as supported");

var videoOnlyCapabilities = MediaOutputService.CreateCapabilities("ffmpeg.exe",
    " V..... libx264 ", "rtmp srt", "flv mpegts whip");
Equal(true, videoOnlyCapabilities.Supports(MediaOutputKind.Recording),
    "video-only recording does not require an AAC encoder");
Equal(true, videoOnlyCapabilities.Supports(MediaOutputKind.Rtmp),
    "video-only RTMP does not require an AAC encoder");
Equal(true, videoOnlyCapabilities.Supports(MediaOutputKind.Whip),
    "video-only WHIP does not require an Opus encoder");
Equal(true, MediaOutputService.CapabilityScore(ffmpegCapabilities) >
            MediaOutputService.CapabilityScore(videoOnlyCapabilities),
    "FFmpeg discovery prefers the candidate with the most complete output support");
var protocolOnlyCapabilities = MediaOutputService.CreateCapabilities("protocol-only.exe",
    " A..... aac\n A..... libopus ", "rtmp srt", "flv mpegts whip");
Equal(videoOnlyCapabilities,
    MediaOutputService.SelectBestCapabilities([
        protocolOnlyCapabilities, videoOnlyCapabilities]),
    "FFmpeg discovery never prefers a candidate without H.264");
var hardwareTieCapabilities = MediaOutputService.CreateCapabilities("hardware.exe",
    " V..... h264_mf ", "rtmp srt", "flv mpegts whip");
Equal(hardwareTieCapabilities,
    MediaOutputService.SelectBestCapabilities([
        videoOnlyCapabilities, hardwareTieCapabilities]),
    "equally capable FFmpeg builds prefer a working hardware encoder");

var ffmpegLocationRoot = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-ffmpeg-locations-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(ffmpegLocationRoot);
    var firstFfmpeg = Path.Combine(ffmpegLocationRoot, "first-ffmpeg.exe");
    var secondFfmpeg = Path.Combine(ffmpegLocationRoot, "second-ffmpeg.exe");
    File.WriteAllBytes(firstFfmpeg, [1]);
    File.WriteAllBytes(secondFfmpeg, [2]);
    Sequence([firstFfmpeg, secondFfmpeg],
        MediaOutputService.ParseFfmpegLocations(
            $"{firstFfmpeg}\r\n{secondFfmpeg}\r\n{firstFfmpeg}\r\n"),
        "FFmpeg discovery keeps every unique where.exe result");
}
finally
{
    if (Directory.Exists(ffmpegLocationRoot))
        Directory.Delete(ffmpegLocationRoot, recursive: true);
}

var recordingArguments = MediaOutputService.BuildArguments(
    new MediaOutputRequest(MediaOutputKind.Recording, "capture.mp4", 1280, 720, 30, 6000),
    ffmpegCapabilities);
Sequence(["-hide_banner", "-loglevel", "warning", "-nostdin",
        "-thread_queue_size", "512", "-f", "s16le", "-ar", "48000", "-ac", "2",
        "-i", @"\\.\pipe\iphoneMirror-audio-test", "-f", "rawvideo",
        "-pixel_format", "nv12", "-video_size", "1280x720", "-framerate", "30",
        "-i", "pipe:0", "-map", "1:v:0", "-map", "0:a:0", "-c:v", "h264_mf",
        "-hw_encoding", "1", "-scenario", "archive", "-pix_fmt", "nv12",
        "-g", "60", "-b:v", "6000k",
        "-maxrate", "6000k", "-bufsize", "12000k", "-c:a", "aac",
        "-b:a", "192k", "-movflags", "+faststart",
        "-y", "capture.mp4"],
    recordingArguments,
    "recording opens the audio pipe before stdin video and keeps stable mapping");

var softwareRecordingArguments = MediaOutputService.BuildArguments(
    new MediaOutputRequest(MediaOutputKind.Recording, "software.mp4", 1280, 720,
        30, 6000), videoOnlyCapabilities, audioPipePath: null,
    includeAudio: false);
Sequence(["-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p"],
    softwareRecordingArguments
        .SkipWhile(argument => argument != "-preset").Take(6),
    "libx264 remains the compatible software fallback");

var silentRecordingArguments = MediaOutputService.BuildArguments(
    new MediaOutputRequest(MediaOutputKind.Recording, "silent.mp4", 1280, 720, 30, 6000),
    ffmpegCapabilities, audioPipePath: null, includeAudio: false);
Equal(true, silentRecordingArguments.Contains("-an", StringComparer.Ordinal),
    "recording can start without waiting for projection audio");
Equal(false, silentRecordingArguments.Contains("s16le", StringComparer.Ordinal),
    "video-only recording does not create an unavailable audio input");
Sequence(["-movflags", "+faststart", "-y", "silent.mp4"],
    silentRecordingArguments.TakeLast(4),
    "video-only recording still finalizes a seekable MP4");

var lateAudioNormalizer = new MediaOutputService.Pcm16AudioNormalizer(48000, 2);
var lateAudio = lateAudioNormalizer.Convert(
    new IPhoneMirror.App.Interop.AudioPacket(1, 44100, 1, 16,
        new byte[] { 0, 0, 0xFF, 0x7F }));
Equal(8, lateAudio.Length,
    "late audio is normalized to the fixed FFmpeg stereo sample rate");
Equal(0, lateAudio[0],
    "late audio normalization preserves the first PCM sample");

Equal(30L, MediaOutputService.CalculateDueVideoFrames(
        TimeSpan.FromSeconds(1), 30, 0),
    "video output schedules one second of frames after one second");
Equal(20L, MediaOutputService.CalculateDueVideoFrames(
        TimeSpan.FromSeconds(1), 30, 10),
    "video output catches up frames missed by a slow encoder");
Equal(0L, MediaOutputService.CalculateDueVideoFrames(
        TimeSpan.FromSeconds(1), 30, 30),
    "video output does not exceed the elapsed wall-clock duration");
Equal(8L, MediaOutputService.CalculateDueVideoFrames(
        TimeSpan.FromMilliseconds(250), 30, 0),
    "video output rounds fractional frame intervals to the nearest frame");
var normalVideoSchedule = MediaOutputService.CalculateVideoWritePlan(
    TimeSpan.FromSeconds(1), 30, 10);
Equal(20L, normalVideoSchedule.FramesToWrite,
    "video output preserves ordinary short catch-up behavior");
Equal(10L, normalVideoSchedule.FramesWrittenBaseline,
    "ordinary video catch-up does not move the output baseline");
var boundedVideoSchedule = MediaOutputService.CalculateVideoWritePlan(
    TimeSpan.FromSeconds(10), 30, 0);
Equal(60L, boundedVideoSchedule.FramesToWrite,
    "video output bounds catch-up to two seconds of repeated frames");
Equal(240L, boundedVideoSchedule.FramesWrittenBaseline,
    "video output discards the oldest backlog before resuming current output");
Throws<ArgumentOutOfRangeException>(() =>
        MediaOutputService.CalculateDueVideoFrames(
            TimeSpan.FromSeconds(1), 0, 0),
    "video output rejects an invalid frame rate");

var mediaOutputServiceCode = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Services", "MediaOutputService.cs"));
Equal(true, mediaOutputServiceCode.Contains("_runTask = Task.Run(",
        StringComparison.Ordinal),
    "media output frame and audio pumps are scheduled off the WPF dispatcher");

var queuedAudio = new[]
{
    new IPhoneMirror.App.Interop.AudioPacket(41, 48000, 2, 16, new byte[4]),
    new IPhoneMirror.App.Interop.AudioPacket(42, 48000, 2, 16, new byte[4]),
    new IPhoneMirror.App.Interop.AudioPacket(43, 48000, 2, 16, new byte[4]),
};
var newestAudio = MediaOutputService.ReadNewestAvailableAudioPacket(
    afterSequence => queuedAudio.FirstOrDefault(packet => packet.Sequence > afterSequence),
    0);
Equal(43UL, newestAudio?.Sequence ?? 0,
    "output startup drains queued PCM and starts from the newest packet");
Throws<InvalidDataException>(() =>
        MediaOutputService.ReadNewestAvailableAudioPacket(
            _ => new IPhoneMirror.App.Interop.AudioPacket(
                7, 48000, 2, 16, new byte[4]), 7),
    "non-advancing audio sequence is rejected");

var pendingDirectory = Path.Combine(Path.GetTempPath(),
    $"iphone-mirror-pending-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(pendingDirectory);
try
{
    var olderPending = Path.Combine(pendingDirectory, "older.mp4");
    var newerPending = Path.Combine(pendingDirectory, "newer.mp4");
    var emptyPending = Path.Combine(pendingDirectory, "incomplete.mp4");
    var partialPending = PendingRecordingStore.CreateStagingPath(
        Path.Combine(pendingDirectory, "crashed.mp4"));
    File.WriteAllBytes(olderPending, [1]);
    File.WriteAllBytes(newerPending, [2]);
    File.WriteAllBytes(emptyPending, []);
    File.WriteAllBytes(partialPending, [3]);
    File.SetLastWriteTimeUtc(olderPending, DateTime.UtcNow.AddMinutes(-2));
    File.SetLastWriteTimeUtc(newerPending, DateTime.UtcNow.AddMinutes(-1));
    File.SetLastWriteTimeUtc(emptyPending, DateTime.UtcNow);
    File.SetLastWriteTimeUtc(partialPending, DateTime.UtcNow.AddMinutes(1));
    Equal(newerPending, PendingRecordingStore.FindLatest(pendingDirectory),
        "restart recovery ignores a newer non-empty partial recording");
}
finally
{
    Directory.Delete(pendingDirectory, recursive: true);
}

var rtmpArguments = MediaOutputService.BuildArguments(
    new MediaOutputRequest(MediaOutputKind.Rtmp, "rtmps://example.test/live", 1920, 1080,
        60, 9000), mediaFoundationOnly);
Equal(false, rtmpArguments.Contains("-preset", StringComparer.Ordinal),
    "h264_mf does not receive libx264-only tuning arguments");
Equal(true, rtmpArguments.Contains("-hw_encoding", StringComparer.Ordinal),
    "h264_mf explicitly requires hardware encoding");
Sequence(["-pix_fmt", "nv12"], rtmpArguments
        .SkipWhile(argument => argument != "-pix_fmt").Take(2),
    "hardware H.264 encoding consumes NV12 without a BGRA or planar conversion");
Sequence(["-f", "flv", "rtmps://example.test/live"], rtmpArguments.TakeLast(3),
    "RTMP output uses the FLV muxer");

var srtArguments = MediaOutputService.BuildArguments(
    new MediaOutputRequest(MediaOutputKind.Srt, "srt://example.test:9000", 1280, 720,
        30, 5000), ffmpegCapabilities);
Sequence(["-f", "mpegts", "srt://example.test:9000"], srtArguments.TakeLast(3),
    "SRT output uses the MPEG-TS muxer");
var whipArguments = MediaOutputService.BuildArguments(
    new MediaOutputRequest(MediaOutputKind.Whip, "https://example.test/whip", 1280, 720,
        30, 5000, " Bearer secret "), ffmpegCapabilities);
Sequence(["-authorization", "secret", "-f", "whip",
        "https://example.test/whip"], whipArguments.TakeLast(5),
    "WHIP output passes the token expected by FFmpeg without a duplicate bearer prefix");
Sequence(["-c:a", "libopus", "-ac", "2", "-b:a", "128k"],
    whipArguments.SkipWhile(argument => argument != "-c:a").Take(6),
    "WHIP output always converts source audio to RTC-compatible stereo Opus");
Equal("opaque-token", MediaOutputService.NormalizeWhipToken(" opaque-token "),
    "WHIP token normalization preserves an opaque token");
Equal(string.Empty, MediaOutputService.NormalizeWhipToken("Bearer"),
    "an empty bearer authorization is omitted");
Throws<ArgumentOutOfRangeException>(() => MediaOutputService.BuildArguments(
        new MediaOutputRequest((MediaOutputKind)999, "invalid", 1280, 720, 30, 5000),
        ffmpegCapabilities),
    "unknown output kind is rejected during argument construction");

var nv12Pixels = Enumerable.Range(1, 24).Select(value => (byte)value).ToArray();
var nv12Payload = MediaOutputService.GetNv12FramePayload(
    new IPhoneMirror.App.Interop.Nv12VideoFrame(4, 4, 4, 1, nv12Pixels), 4, 4);
Equal(24, nv12Payload.Length,
    "NV12 output writes one-and-a-half bytes per pixel");
Equal(true, nv12Payload.Span.SequenceEqual(nv12Pixels),
    "NV12 output is forwarded without a managed color conversion or copy");
Throws<InvalidDataException>(() => MediaOutputService.GetNv12FramePayload(
        new IPhoneMirror.App.Interop.Nv12VideoFrame(4, 4, 8, 2, nv12Pixels),
        4, 4),
    "NV12 output rejects a non-tightly-packed native frame");
Throws<InvalidDataException>(() => MediaOutputService.GetNv12FramePayload(
        new IPhoneMirror.App.Interop.Nv12VideoFrame(4, 4, 4, 3, new byte[23]),
        4, 4),
    "NV12 output rejects a truncated native frame");

var processTestRequest = new MediaOutputRequest(MediaOutputKind.Recording,
    Path.Combine(Path.GetTempPath(), $"process-test-{Guid.NewGuid():N}.mp4"),
    160, 160, 10, 500);
await using (var immediateExitOutput = new MediaOutputService((_, _, _) => null,
    (_, afterSequence) => afterSequence == 0
        ? new IPhoneMirror.App.Interop.AudioPacket(
            1, 48000, 2, 16, new byte[4])
        : null))
{
    var previousDelayedExit = Environment.GetEnvironmentVariable(
        delayedExitEnvironment, EnvironmentVariableTarget.Process);
    try
    {
        Environment.SetEnvironmentVariable(delayedExitEnvironment, "750",
            EnvironmentVariableTarget.Process);
        var delayedExitCapabilities = ffmpegCapabilities with
        {
            FfmpegPath = Environment.ProcessPath ??
                throw new InvalidOperationException(
                    "The test process executable path is unavailable."),
        };
        await ThrowsAsync<InvalidOperationException>(() =>
                immediateExitOutput.StartAsync(1, processTestRequest,
                    delayedExitCapabilities),
            "an output process that exits during the audio handshake is rejected");
    }
    finally
    {
        Environment.SetEnvironmentVariable(delayedExitEnvironment,
            previousDelayedExit, EnvironmentVariableTarget.Process);
    }
    Equal(false, immediateExitOutput.IsRunning,
        "startup process exit does not publish a running output");
    Equal(0UL, immediateExitOutput.SessionHandle,
        "startup process exit does not retain the session handle");
}

await using (var failedStartOutput = new MediaOutputService((_, _, _) => null,
    (_, afterSequence) => afterSequence == 0
        ? new IPhoneMirror.App.Interop.AudioPacket(
            1, 48000, 2, 16, new byte[4])
        : null))
{
    var missingExecutableCapabilities = ffmpegCapabilities with
    {
        FfmpegPath = Path.Combine(Path.GetTempPath(),
            $"missing-ffmpeg-{Guid.NewGuid():N}.exe"),
    };
    await ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            failedStartOutput.StartAsync(1, processTestRequest,
                missingExecutableCapabilities),
        "Process.Start failure is propagated after cleanup");
    Equal(false, failedStartOutput.IsRunning,
        "Process.Start failure leaves no running output");
    Equal(0UL, failedStartOutput.SessionHandle,
        "Process.Start failure leaves no retained session handle");
}

var installedFfmpegCapabilities = await MediaOutputService.ProbeAsync();
if (installedFfmpegCapabilities.Supports(MediaOutputKind.Recording))
{
    var silentRecordingPath = Path.Combine(Path.GetTempPath(),
        $"iphone-mirror-silent-recording-{Guid.NewGuid():N}.mp4");
    long recordingTimestamp = 0;
    var lateAudioAvailableAt = Environment.TickCount64 + 600;
    await using var silentRecordingOutput = new MediaOutputService(
        (_, width, height) => new IPhoneMirror.App.Interop.Nv12VideoFrame(
            width, height, width,
            Interlocked.Add(ref recordingTimestamp, 1_000_000),
            new byte[checked((int)((ulong)width * height * 3U / 2U))]),
        (_, afterSequence) => afterSequence == 0 &&
                Environment.TickCount64 >= lateAudioAvailableAt
            ? new IPhoneMirror.App.Interop.AudioPacket(
                1, 48000, 2, 16, new byte[3840])
            : null);
    try
    {
        await silentRecordingOutput.StartAsync(1,
            new MediaOutputRequest(MediaOutputKind.Recording,
                silentRecordingPath, 160, 160, 10, 500),
            installedFfmpegCapabilities);
        Equal(true, silentRecordingOutput.IsRunning,
            "video-only recording starts without a PCM packet");
        await Task.Delay(700);
        var silentStagingPath = PendingRecordingStore.CreateStagingPath(
            silentRecordingPath);
        Equal(false, File.Exists(silentRecordingPath),
            "an active recording is not exposed as a completed MP4");
        Equal(true, File.Exists(silentStagingPath),
            "an active recording writes to the partial MP4 path");
        await silentRecordingOutput.StopAsync();
        Equal(false, silentRecordingOutput.IsRunning,
            "video-only recording stops after FFmpeg finalization");
        var silentMp4 = File.ReadAllBytes(silentRecordingPath);
        Equal(true, silentMp4.AsSpan().IndexOf("ftyp"u8) >= 0,
            "video-only recording writes an MP4 file type box");
        Equal(true, silentMp4.AsSpan().IndexOf("moov"u8) >= 0,
            "video-only recording writes the finalized MP4 index");
        if (installedFfmpegCapabilities.HasAacEncoder)
            Equal(true, silentMp4.AsSpan().IndexOf("soun"u8) >= 0,
                "audio that arrives after recording start is included in the MP4 track");
        Equal(false, File.Exists(silentStagingPath),
            "successful finalization atomically promotes and removes the partial MP4");
    }
    finally
    {
        try { File.Delete(silentRecordingPath); } catch { }
    }

    var audioRecordingPath = Path.Combine(Path.GetTempPath(),
        $"iphone-mirror-audio-recording-{Guid.NewGuid():N}.mp4");
    long audioRecordingTimestamp = 0;
    long audioSequence = 0;
    long nextAudioPacketAt = 0;
    await using var audioRecordingOutput = new MediaOutputService(
        (_, width, height) => new IPhoneMirror.App.Interop.Nv12VideoFrame(
            width, height, width,
            Interlocked.Add(ref audioRecordingTimestamp, 1_000_000),
            new byte[checked((int)((ulong)width * height * 3U / 2U))]),
        (_, afterSequence) =>
        {
            var now = Environment.TickCount64;
            if (afterSequence != 0 && now < Interlocked.Read(ref nextAudioPacketAt))
                return null;
            Interlocked.Exchange(ref nextAudioPacketAt, now + 20);
            return new IPhoneMirror.App.Interop.AudioPacket(
                (ulong)Interlocked.Increment(ref audioSequence),
                48000, 2, 16, new byte[3840]);
        });
    try
    {
        using var startupTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await audioRecordingOutput.StartAsync(1,
            new MediaOutputRequest(MediaOutputKind.Recording,
                audioRecordingPath, 160, 160, 10, 500),
            installedFfmpegCapabilities, startupTimeout.Token);
        Equal(true, audioRecordingOutput.IsRunning,
            "recording with PCM audio completes the FFmpeg pipe handshake");
        await Task.Delay(700);
        await audioRecordingOutput.StopAsync();
        Equal(false, audioRecordingOutput.IsRunning,
            "recording with PCM audio stops after FFmpeg finalization");
        var audioMp4 = File.ReadAllBytes(audioRecordingPath);
        Equal(true, audioMp4.AsSpan().IndexOf("ftyp"u8) >= 0,
            "recording with PCM audio writes an MP4 file type box");
        Equal(true, audioMp4.AsSpan().IndexOf("moov"u8) >= 0,
            "recording with PCM audio writes the finalized MP4 index");
        Equal(true, audioMp4.AsSpan().IndexOf("soun"u8) >= 0,
            "recording with PCM audio contains an audio track");
    }
    finally
    {
        try { File.Delete(audioRecordingPath); } catch { }
    }

    var interruptedAudioPath = Path.Combine(Path.GetTempPath(),
        $"iphone-mirror-interrupted-audio-{Guid.NewGuid():N}.mp4");
    await using var interruptedAudioOutput = new MediaOutputService(
        (_, width, height) => new IPhoneMirror.App.Interop.Nv12VideoFrame(
            width, height, width,
            1_000_000,
            new byte[checked((int)((ulong)width * height * 3U / 2U))]),
        (_, afterSequence) => afterSequence == 0
            ? new IPhoneMirror.App.Interop.AudioPacket(
                1, 48000, 2, 16, new byte[3840])
            : null);
    try
    {
        using var startupTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await interruptedAudioOutput.StartAsync(1,
            new MediaOutputRequest(MediaOutputKind.Recording,
                interruptedAudioPath, 160, 160, 10, 500),
            installedFfmpegCapabilities, startupTimeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(5.5));
        Equal(true, interruptedAudioOutput.IsRunning,
            "a PCM interruption and static frame longer than five seconds do not stop video output");
        await interruptedAudioOutput.StopAsync();
        var interruptedMp4 = File.ReadAllBytes(interruptedAudioPath);
        Equal(true, interruptedMp4.AsSpan().IndexOf("moov"u8) >= 0,
            "interrupted-audio recording remains a finalized MP4");
        Equal(true, interruptedMp4.AsSpan().IndexOf("soun"u8) >= 0,
            "silence insertion preserves the recording audio track");
    }
    finally
    {
        try { File.Delete(interruptedAudioPath); } catch { }
        try
        {
            File.Delete(PendingRecordingStore.CreateStagingPath(interruptedAudioPath));
        }
        catch { }
    }
}

// MediaElement events do not identify the Source that raised them. A fresh
// backend is bound for every load so delayed events can be rejected by both
// casting generation and sender identity.
var mediaEvents = new MediaCastEventGate();
var firstBackend = new object();
var firstMediaGeneration = mediaEvents.BeginGeneration();
Equal(true, mediaEvents.TryBind(firstMediaGeneration, firstBackend),
    "first media backend binds to its generation");
Equal(true, mediaEvents.IsCurrent(firstMediaGeneration, firstBackend),
    "current media backend event is accepted");
var recoveredBackend = new object();
Equal(true, mediaEvents.TryBind(firstMediaGeneration, recoveredBackend),
    "live recovery can replace the backend within one cast generation");
Equal(false, mediaEvents.IsCurrent(firstMediaGeneration, firstBackend),
    "late event from the pre-recovery backend is rejected");
Equal(true, mediaEvents.IsCurrent(firstMediaGeneration, recoveredBackend),
    "event from the recovered backend is accepted");
mediaEvents.Invalidate();
Equal(false, mediaEvents.IsCurrent(firstMediaGeneration, recoveredBackend),
    "late event after stop is rejected");
var secondBackend = new object();
var secondMediaGeneration = mediaEvents.BeginGeneration();
Equal(true, mediaEvents.TryBind(secondMediaGeneration, secondBackend),
    "new cast backend binds to the new generation");
Equal(false, mediaEvents.TryBind(firstMediaGeneration, firstBackend),
    "stale generation cannot replace the current backend");
Equal(true, mediaEvents.IsCurrent(secondMediaGeneration, secondBackend),
    "stale bind attempt leaves the new backend current");

var recoveryNow = DateTimeOffset.Parse("2026-07-27T00:00:00Z");
var mediaRecovery = new MediaRecoveryBackoff(() => recoveryNow,
    maximumAttempts: 5, stablePlaybackWindow: TimeSpan.FromSeconds(10));
var recoveryDelays = new List<double>();
for (var index = 0; index < 5; ++index)
{
    mediaRecovery.MarkOpened();
    Equal(true, mediaRecovery.TryGetNext(out _, out var delay),
        "short-lived live playback remains recoverable");
    recoveryDelays.Add(delay.TotalMilliseconds);
}
Equal(true, recoveryDelays.SequenceEqual([250, 500, 1000, 2000, 4000]),
    "live recovery uses increasing backoff across short opens");
Equal(false, mediaRecovery.TryGetNext(out _, out _),
    "live recovery has a session-level retry budget");
mediaRecovery.Reset();
mediaRecovery.MarkOpened();
recoveryNow += TimeSpan.FromSeconds(11);
Equal(true, mediaRecovery.TryGetNext(out var stableAttempt, out var stableDelay) &&
    stableAttempt == 1 && stableDelay == TimeSpan.FromMilliseconds(250),
    "stable playback resets live recovery backoff");
var segmentedRecovery = new MediaRecoveryBackoff(() => recoveryNow,
    maximumAttempts: 2, stablePlaybackWindow: TimeSpan.FromSeconds(3));
segmentedRecovery.MarkOpened();
recoveryNow += TimeSpan.FromSeconds(5.8);
Equal(true, segmentedRecovery.TryGetNext(out var segmentedAttempt, out _) &&
    segmentedAttempt == 1,
    "HLS segment handoff resets recovery after real playback progress");
Equal<string?>(null,
    WirelessReceiverConfiguration.FindExecutable(Path.GetTempPath(),
        Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")),
    "wireless receiver discovery rejects missing executables");

var logPath = Path.Combine(Path.GetTempPath(), $"iPhoneMirror-log-{Guid.NewGuid():N}.txt");

var sensitiveDeviceId = "00008110001234567890abcd1234567890abcdef";
var deviceToken = AppLog.Device(sensitiveDeviceId);
Equal(true, deviceToken.StartsWith("device#", StringComparison.Ordinal),
    "device log identity uses an anonymous token");
Equal(deviceToken, AppLog.Device(sensitiveDeviceId),
    "device log identity remains stable within diagnostics");
Equal(false, deviceToken.Contains(sensitiveDeviceId, StringComparison.OrdinalIgnoreCase),
    "device log identity does not contain the raw serial");
Equal("media-cast", AppLog.Device("media-cast://active"),
    "media virtual device has a non-sensitive fixed identity");
var privateMediaSource = new Uri(
    "https://private.example.local/library/personal-video.m3u8?access_token=secret");
Equal("https/m3u8?query=True", AppLog.MediaSource(privateMediaSource),
    "media source diagnostics retain format without host, path, or query values");
Equal("relative/unknown?query=False",
    AppLog.MediaSource(new Uri("../private/video.mp4?token=secret", UriKind.Relative)),
    "relative media source diagnostics are safe and non-throwing");
var sanitizedLog = AppLog.Sanitize(
    $"failed {privateMediaSource} device={sensitiveDeviceId}\r\nC:\\Users\\Private\\secret.txt");
Equal(false, sanitizedLog.Contains("private.example.local", StringComparison.OrdinalIgnoreCase),
    "log sanitization removes media hosts");
Equal(false, sanitizedLog.Contains("access_token", StringComparison.OrdinalIgnoreCase),
    "log sanitization removes media query names and values");
Equal(false, sanitizedLog.Contains(sensitiveDeviceId, StringComparison.OrdinalIgnoreCase),
    "log sanitization removes raw device serials");
Equal(false, sanitizedLog.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase),
    "log sanitization removes local paths");
Equal(false, sanitizedLog.Contains('\n'),
    "log sanitization produces a single-line entry");
var modernDeviceId = "00008110-001234567890ABCD";
var credentialLog = AppLog.Sanitize(
    "Authorization: Bearer bearer-secret-value\n" +
    "Proxy-Authorization: Basic YmFzaWMtc2VjcmV0\n" +
    "Cookie: session=cookie-secret; csrf=cookie-csrf\n" +
    "Authorization=opaque-authorization-secret\n" +
    "Cookie=assignment-cookie-secret; assignment-csrf-secret\n" +
    "token=bare-token password=\"quoted password\" api_key:'api-secret' " +
    $"server=private.internal endpoint=192.168.10.20:8080 device={modernDeviceId} " +
    "mac=AA:BB:CC:DD:EE:FF path=/Users/Private/Movies/video.mp4");
foreach (var secret in new[]
         {
             "bearer-secret-value", "YmFzaWMtc2VjcmV0", "cookie-secret",
             "cookie-csrf", "bare-token", "quoted password", "api-secret",
             "opaque-authorization-secret", "assignment-cookie-secret",
             "assignment-csrf-secret",
             "private.internal", "192.168.10.20", modernDeviceId,
             "AA:BB:CC:DD:EE:FF", "/Users/Private",
         })
    Equal(false, credentialLog.Contains(secret, StringComparison.OrdinalIgnoreCase),
        $"log sanitization removes sensitive value {secret}");
Equal(true, credentialLog.Contains("<redacted>", StringComparison.Ordinal),
    "credential fields retain a redaction marker");
Equal(false, credentialLog.Contains('\n'),
    "credential header redaction remains single-line");
var structuredLog = AppLog.Event("capture_state",
    ("device", sensitiveDeviceId), ("handle", 17UL), ("state", "Streaming"));
Equal(false, structuredLog.Contains(sensitiveDeviceId, StringComparison.OrdinalIgnoreCase),
    "structured event values are sanitized before persistence");
Equal(true, structuredLog.Contains("handle=17", StringComparison.Ordinal),
    "structured event preserves non-sensitive numeric context");
var injectedEvent = AppLog.Event("capture state=forged\nnext",
    ("bad key=injected", "value\nforged=true"),
    ("oversized", new string('x', 5000)));
Equal(false, injectedEvent.Contains('\n'),
    "structured event flattens newline injection");
Equal(false, injectedEvent.Contains("bad key", StringComparison.Ordinal),
    "structured event normalizes field keys");
Equal(true, injectedEvent.Length < 600,
    "structured event bounds an individual field");
var manyFields = Enumerable.Range(0, 10)
    .Select(index => (object?)($"field_{index}", new string('y', 384)))
    .ToArray();
var boundedEvent = AppLog.Event("bounded_event", manyFields);
Equal(true, boundedEvent.Length <= 2048,
    "structured event bounds total line length");
Equal(true, boundedEvent.EndsWith("truncated=true", StringComparison.Ordinal),
    "structured event marks total-line truncation");
Equal(2048, AppLog.Message(new string('z', 5000)).Length,
    "direct application log message is bounded");
var diagnosticEntry = DiagnosticLogger.FormatEntry("ERROR", "test", "failure",
    ("device", sensitiveDeviceId),
    ("secret", "token=private-token"),
    ("detail", "failed\nC:\\Users\\Private\\secret.txt"));
Equal(false, diagnosticEntry.Contains(sensitiveDeviceId,
        StringComparison.OrdinalIgnoreCase),
    "persistent diagnostic entry redacts device identifiers");
Equal(false, diagnosticEntry.Contains("private-token",
        StringComparison.OrdinalIgnoreCase),
    "persistent diagnostic entry redacts credentials");
Equal(1, diagnosticEntry.Count(character => character == '\n'),
    "persistent diagnostic entry remains one physical line");
Equal(true, DiagnosticLogger.FormatEntry("INFO", "test", "version",
        ("version", "1.4.3.0")).Contains("version=1.4.3.0",
        StringComparison.Ordinal),
    "persistent diagnostics preserve an application version");
var diagnosticDirectory = Path.Combine(Path.GetTempPath(),
    $"iPhoneMirror-diagnostics-{Guid.NewGuid():N}");
Directory.CreateDirectory(diagnosticDirectory);
try
{
    var activeDiagnosticPath = Path.Combine(diagnosticDirectory, "test.log");
    await File.WriteAllTextAsync(activeDiagnosticPath, new string('x', 128));
    var sessionStarted = true;
    Equal(true, DiagnosticLogger.TryRotateIfNeeded(activeDiagnosticPath,
            64, 2, ref sessionStarted),
        "diagnostic logger rotates an oversized active file");
    Equal(false, sessionStarted,
        "diagnostic rotation requires a new session header");
    Equal(true, File.Exists(activeDiagnosticPath + ".1"),
        "diagnostic rotation retains the previous file");
    var expiredPath = Path.Combine(diagnosticDirectory, "expired.log.1");
    await File.WriteAllTextAsync(expiredPath, "expired");
    File.SetLastWriteTimeUtc(expiredPath, DateTime.UtcNow.AddDays(-30));
    var cleanupResult = DiagnosticLogger.CleanupDirectory(diagnosticDirectory,
        DateTimeOffset.UtcNow, includeActiveLogs: false);
    Equal(true, cleanupResult.DeletedFiles >= 1 && !File.Exists(expiredPath),
        "diagnostic cleanup removes expired archives");
}
finally
{
    Directory.Delete(diagnosticDirectory, recursive: true);
}
Equal(true, NativeLogTailReader.IsUiEventLine(
        "12:00:00.000 [seq=42] [info] [app] ui_event action refreshed"),
    "structured native UI event is recognized for duplicate suppression");
Equal(true, NativeLogTailReader.IsUiEventLine("ui_event action refreshed"),
    "unprefixed native UI event remains recognized");
Equal(false, NativeLogTailReader.IsUiEventLine(
        "12:00:00.000 [seq=43] [info] [capture] frame ready"),
    "non-UI native event remains visible");
try
{
    var reader = new NativeLogTailReader(logPath);
    await File.WriteAllTextAsync(logPath, "first\npartial");
    Sequence(["first"], await reader.ReadNewLinesAsync(),
        "log reader publishes complete lines only");
    await File.AppendAllTextAsync(logPath, "-line\n");
    Sequence(["partial-line"], await reader.ReadNewLinesAsync(),
        "log reader preserves a partial line across reads");
    await File.WriteAllTextAsync(logPath, "reset\n");
    Sequence(["reset"], await reader.ReadNewLinesAsync(),
        "log reader restarts after truncation");
    await File.AppendAllTextAsync(logPath, new string('x', 20 * 1024) + "\n");
    var longLines = await reader.ReadNewLinesAsync();
    Equal(1, longLines.Count, "log reader returns one bounded long line");
    Equal(16 * 1024, longLines[0].Length, "log reader bounds individual line memory");
    Equal(true, longLines[0].EndsWith('…'), "bounded log line has a truncation marker");
}
finally
{
    if (File.Exists(logPath)) File.Delete(logPath);
}

var driverManagerRoot = Path.Combine(Path.GetTempPath(), "iPhoneMirror.App.Tests",
    Guid.NewGuid().ToString("N"));
try
{
    var appDirectory = Path.Combine(driverManagerRoot, "outputs", "iPhoneMirror");
    var siblingDirectory = Path.Combine(driverManagerRoot, "outputs", "iPhoneMirror.Driver");
    Directory.CreateDirectory(appDirectory);
    Directory.CreateDirectory(siblingDirectory);
    var siblingExecutable = Path.Combine(siblingDirectory, "iPhoneMirror.Driver.exe");
    File.WriteAllBytes(siblingExecutable, []);
    Equal(Path.GetFullPath(siblingExecutable),
        DriverManagerLauncher.FindExecutable(appDirectory, workingDirectory: driverManagerRoot),
        "driver manager discovery finds sibling output");

    var overrideExecutable = Path.Combine(driverManagerRoot, "custom-driver-manager.exe");
    File.WriteAllBytes(overrideExecutable, []);
    Equal(Path.GetFullPath(overrideExecutable),
        DriverManagerLauncher.FindExecutable(appDirectory, overrideExecutable,
            driverManagerRoot),
        "driver manager override takes priority");
}
finally
{
    if (Directory.Exists(driverManagerRoot)) Directory.Delete(driverManagerRoot, recursive: true);
}

Equal(false, IndependentWindowAudioPolicy.ShowMuteOthers(1),
    "single device only shows the current-window mute action");
Equal(true, IndependentWindowAudioPolicy.ShowMuteOthers(2),
    "multiple devices show the mute-other-windows action");
Sequence(["phone-b", "phone-c"],
    IndependentWindowAudioPolicy.GetOtherDeviceIds("PHONE-A",
        ["phone-a", "phone-b", "PHONE-B", "phone-c"]),
    "mute-other-windows excludes the current device and duplicate sessions");


// Closing must explicitly stop the QuickTime session before core disposal,
// and repeated close notifications must not send a second shutdown sequence.
var shutdownOrder = new List<string>();
var shutdown = new CaptureShutdownCoordinator();
await shutdown.StopAndDisposeOnceAsync(
    () => { shutdownOrder.Add("stop"); return Task.CompletedTask; },
    () => { shutdownOrder.Add("dispose"); return Task.CompletedTask; });
await shutdown.StopAndDisposeOnceAsync(
    () => { shutdownOrder.Add("duplicate-stop"); return Task.CompletedTask; },
    () => { shutdownOrder.Add("duplicate-dispose"); return Task.CompletedTask; });
Sequence(["stop", "dispose"], shutdownOrder, "window close cleanup is ordered and idempotent");

var teardownEntered = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
var allowTeardown = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
var teardownStops = 0;
var teardownDestroys = 0;
var sessionManager = new DeviceSessionManager(
    _ =>
    {
        Interlocked.Increment(ref teardownStops);
        teardownEntered.SetResult();
        allowTeardown.Task.GetAwaiter().GetResult();
    },
    _ => Interlocked.Increment(ref teardownDestroys));
var concurrentSession = new DeviceCaptureState
{
    Udid = "concurrent-stop-device",
    Handle = 77,
};
var firstTeardown = sessionManager.StopAndDestroyAsync(concurrentSession);
await teardownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
var secondTeardown = sessionManager.StopAndDestroyAsync(concurrentSession);
Equal(true, ReferenceEquals(firstTeardown, secondTeardown),
    "concurrent stop callers share the in-flight teardown task");
Equal((ulong)0, concurrentSession.Handle,
    "session handle is revoked before native teardown finishes");
Equal(true, concurrentSession.IsStopping,
    "session remains stopping while native teardown is in flight");
allowTeardown.SetResult();
await Task.WhenAll(firstTeardown, secondTeardown);
Equal(1, teardownStops, "concurrent stop invokes native stop exactly once");
Equal(1, teardownDestroys, "concurrent stop destroys the native handle exactly once");
Equal(false, concurrentSession.IsStopping,
    "completed teardown resets the session transition state");

var warningStops = 0;
var warningDestroys = 0;
var warningManager = new DeviceSessionManager(
    _ =>
    {
        Interlocked.Increment(ref warningStops);
        throw new UsbConfigurationRestoreWarningException(
            "normal USB configuration was not observed",
            (int)NativeResult.UsbConfigurationRestoreWarning);
    },
    _ => Interlocked.Increment(ref warningDestroys));
var warningSession = new DeviceCaptureState
{
    Udid = "restore-warning-device",
    Handle = 88,
};
try
{
    await warningManager.StopAndDestroyAsync(warningSession);
    throw new InvalidOperationException("USB restore warning was not propagated");
}
catch (UsbConfigurationRestoreWarningException warning)
{
    Equal((int)NativeResult.UsbConfigurationRestoreWarning, warning.ErrorCode,
        "USB restore warning preserves the native result code");
}
Equal(1, warningStops,
    "USB restore warning still invokes native stop exactly once");
Equal(1, warningDestroys,
    "USB restore warning still destroys the native session handle");
Equal((ulong)0, warningSession.Handle,
    "USB restore warning revokes the session handle before reporting the warning");
Equal(false, warningSession.IsStopping,
    "USB restore warning clears the in-flight stop state");

var backgroundReleaseIndex = mainViewModelSource.IndexOf(
    "await ReleaseFailedSessionLockedAsync(state, status);",
    StringComparison.Ordinal);
var backgroundPromptIndex = mainViewModelSource.IndexOf(
    "errorTitle, errorBody);",
    backgroundReleaseIndex,
    StringComparison.Ordinal);
Equal(true, backgroundReleaseIndex >= 0 && backgroundPromptIndex > backgroundReleaseIndex,
    "background capture errors release the failed session before showing a modal prompt");
var selectedReleaseIndex = mainViewModelSource.IndexOf(
    "await ReleaseFailedSessionLockedAsync(state, status);",
    backgroundReleaseIndex + 1,
    StringComparison.Ordinal);
var selectedPromptIndex = mainViewModelSource.IndexOf(
    "CaptureStatusNoticeWindow.ShowError(errorTitle, errorBody);",
    selectedReleaseIndex,
    StringComparison.Ordinal);
Equal(true, selectedReleaseIndex >= 0 && selectedPromptIndex > selectedReleaseIndex,
    "selected capture errors release the failed session before showing a modal prompt");
var sessionClosedWarningMethodIndex = mainViewModelSource.IndexOf(
    "private void ShowDeviceSessionClosedWarningThenRelease(",
    StringComparison.Ordinal);
var sessionClosedPromptIndex = mainViewModelSource.IndexOf(
    "CaptureStatusNoticeWindow.ShowStoppedThen(errorTitle, errorBody,",
    sessionClosedWarningMethodIndex, StringComparison.Ordinal);
var sessionClosedCleanupIndex = mainViewModelSource.IndexOf(
    "() => ReleaseFailedSessionLockedAsync(state, status)",
    sessionClosedPromptIndex, StringComparison.Ordinal);
Equal(true, sessionClosedWarningMethodIndex >= 0 &&
    sessionClosedPromptIndex > sessionClosedWarningMethodIndex &&
    sessionClosedCleanupIndex > sessionClosedPromptIndex,
    "phone-side stop warnings are displayed before their teardown callback runs");

var currentExecutable = Environment.ProcessPath!;
Equal(true, SingleInstanceCoordinator.IsSameExecutable(currentExecutable,
        currentExecutable.ToUpperInvariant()),
    "single-instance matching treats Windows executable paths case-insensitively");
Equal(false, SingleInstanceCoordinator.IsSameExecutable(currentExecutable,
        Path.Combine(Path.GetDirectoryName(currentExecutable)!, "unrelated", "iPhoneMirror.exe")),
    "single-instance matching rejects same-name executables from another directory");
Equal(false, SingleInstanceCoordinator.IsSameExecutable(currentExecutable, null),
    "single-instance matching rejects processes whose executable path cannot be verified");

var deviceA = new DeviceCaptureState { Udid = "phone-a", Handle = 11, FrameRate = 60, Volume = 80 };
var deviceB = new DeviceCaptureState { Udid = "phone-b", Handle = 22, FrameRate = 30, Volume = 25 };
Equal(UsbProjectionMode.Demo, deviceA.UsbProjectionMode,
    "USB projection defaults to recommended demo mode");
deviceA.UsbProjectionMode = UsbProjectionMode.AirPlay;
deviceB.UsbProjectionMode = UsbProjectionMode.Aisi;
Equal(UsbProjectionMode.AirPlay, deviceA.UsbProjectionMode,
    "device A keeps its independent USB projection mode");
Equal(UsbProjectionMode.Aisi, deviceB.UsbProjectionMode,
    "device B keeps its independent USB projection mode");
Equal(DecoderPreference.Auto, deviceA.DecoderPreference,
    "decoder selection defaults to capability-based automatic fallback");
Equal(0.0, deviceA.Brightness, "brightness defaults to neutral");
Equal(100.0, deviceA.Contrast, "contrast defaults to neutral");
Equal(100.0, deviceA.Saturation, "saturation defaults to neutral");
Equal(100.0, deviceA.Gamma, "gamma defaults to neutral");
deviceA.DecoderPreference = DecoderPreference.HardwarePreferred;
deviceA.Brightness = 12;
deviceA.Contrast = 118;
deviceB.DecoderPreference = DecoderPreference.SoftwareCompatible;
deviceB.Saturation = 75;
deviceB.Gamma = 110;
Equal(DecoderPreference.HardwarePreferred, deviceA.DecoderPreference,
    "device A keeps its independent decoder policy");
Equal(DecoderPreference.SoftwareCompatible, deviceB.DecoderPreference,
    "device B keeps its independent decoder policy");
Equal(12.0, deviceA.Brightness,
    "device A keeps its independent brightness adjustment");
Equal(75.0, deviceB.Saturation,
    "device B keeps its independent saturation adjustment");
deviceB.FrameRate = 24;
Equal((ulong)11, deviceA.Handle, "switching device does not release first session");
Equal(60, deviceA.FrameRate, "device A settings remain independent");
Equal(24, deviceB.FrameRate, "device B settings update independently");
Equal(true, deviceA.UpdateProtectionState(true, false, 0, 0),
    "device protection state reports its first transition");
Equal(true, deviceA.VideoProtected && !deviceA.ProtectedAudioActive,
    "device protection state allows video and audio protection together");
Equal(true, deviceA.UpdateProtectionState(true, true, 48000, 2),
    "device protection state reports an audio-activity transition");
Equal(false, deviceA.UpdateProtectionState(true, true, 48000, 2),
    "identical protection observations do not republish events");
deviceA.UpdateProtectionState(false, false, 0, 0);
deviceA.UpdateProtectionState(true, false, 0, 0);
deviceA.ResetRuntimeObservations();
Equal(false, deviceA.VideoProtected,
    "replacement sessions clear stale protected-content state");

var imageSettingsSession = new DeviceCaptureState
{
    Udid = "image-settings-device",
    Handle = 41,
};
Equal(true, imageSettingsSession.MatchesSessionHandle(41),
    "image settings recognizes the session handle that opened the window");
imageSettingsSession.Handle = 42;
Equal(false, imageSettingsSession.MatchesSessionHandle(41),
    "image settings rejects a replacement session even when its state object is reused");
imageSettingsSession.IsStopping = true;
Equal(false, imageSettingsSession.MatchesSessionHandle(42),
    "image settings rejects a session while it is being torn down");

var videoSettings = new DeviceCaptureState
{
    Udid = "settings-device",
    RenderWidth = 1920,
    RenderHeight = 1080,
    FrameRate = 60,
    DecoderPreference = DecoderPreference.HardwarePreferred,
    Brightness = 8,
    Contrast = 115,
    Saturation = 90,
    Gamma = 105,
};
Equal(true, videoSettings.HasPendingVideoSettings,
    "new per-device video settings start pending");
videoSettings.MarkRenderSettingsApplied(1920, 1080, 60);
Equal(true, videoSettings.HasPendingVideoSettings,
    "render success does not mark decoder and image settings as applied");
videoSettings.MarkImageAdjustmentsApplied(8, 115, 90, 105);
Equal(true, videoSettings.HasPendingVideoSettings,
    "image adjustment submission does not claim an asynchronous decoder switch succeeded");
videoSettings.SynchronizeAppliedDecoderPreference(
    DecoderPreference.HardwarePreferred);
Equal(false, videoSettings.HasPendingVideoSettings,
    "native decoder status completes the submitted per-device settings");

videoSettings.RenderWidth = 1280;
videoSettings.RenderHeight = 720;
videoSettings.FrameRate = 30;
videoSettings.MarkVideoSettingsApplied(1920, 1080, 60,
    DecoderPreference.HardwarePreferred, 8, 115, 90, 105);
Equal((uint)1920, videoSettings.AppliedRenderWidth,
    "applied bookkeeping uses the explicit request snapshot");
Equal(true, videoSettings.HasPendingVideoSettings,
    "values changed after a request remain pending instead of being mislabelled applied");

// Even when explicit stop fails, im_shutdown/dispose remains a mandatory
// defensive cleanup path.
var failureOrder = new List<string>();
var failedShutdown = new CaptureShutdownCoordinator();
try
{
    await failedShutdown.StopAndDisposeOnceAsync(
        () => { failureOrder.Add("stop"); throw new InvalidOperationException("stop failed"); },
        () => { failureOrder.Add("dispose"); return Task.CompletedTask; });
    throw new InvalidOperationException("failed shutdown should propagate its stop error");
}
catch (InvalidOperationException error) when (error.Message == "stop failed")
{
}
Sequence(["stop", "dispose"], failureOrder, "core is disposed after stop failure");

Console.WriteLine("App logic tests passed.");
if (Directory.Exists(diagnosticTestRoot))
    Directory.Delete(diagnosticTestRoot, recursive: true);

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        handler(request, cancellationToken);
}

internal sealed class UnknownLengthContent(byte[] content) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream,
        System.Net.TransportContext? context) => stream.WriteAsync(content).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
