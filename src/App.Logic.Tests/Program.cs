using System.IO;
using System.Net.Http;
using System.Xml.Linq;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;
using IPhoneMirror.Shared.Networking;

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

static HttpResponseMessage HttpResponse(HttpRequestMessage request,
    HttpContent content) => new(System.Net.HttpStatusCode.OK)
{
    RequestMessage = request,
    Content = content,
};

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
    var expectedNavigationFont = Path.GetFileName(localizationPath)
        .Contains("zh-CN", StringComparison.OrdinalIgnoreCase)
        ? "Microsoft YaHei UI"
        : "Segoe UI";
    Equal(expectedNavigationFont, navigationFont,
        $"navigation font matches the interface language in {Path.GetFileName(localizationPath)}");
    var noPingRecovery = localization.Descendants()
        .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"),
            "CaptureNoPingRecovery", StringComparison.Ordinal)).Value;
    Equal(true,
        noPingRecovery.Contains("Restart", StringComparison.OrdinalIgnoreCase) ||
        noPingRecovery.Contains("重启", StringComparison.Ordinal),
        $"no-PING recovery asks the user to restart in {Path.GetFileName(localizationPath)}");
    Equal(true,
        noPingRecovery.Contains("MFi", StringComparison.OrdinalIgnoreCase),
        $"no-PING recovery recommends an original or MFi cable in {Path.GetFileName(localizationPath)}");
    var usbRecovery = localization.Descendants()
        .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"),
            "CaptureUsbConfigurationRecovery", StringComparison.Ordinal)).Value;
    Equal(true,
        usbRecovery.Contains("restart", StringComparison.OrdinalIgnoreCase) ||
        usbRecovery.Contains("重启", StringComparison.Ordinal),
        $"USB recovery asks the user to restart in {Path.GetFileName(localizationPath)}");
    Equal(true,
        usbRecovery.Contains("cable", StringComparison.OrdinalIgnoreCase) ||
        usbRecovery.Contains("数据线", StringComparison.Ordinal),
        $"USB recovery asks the user to replace/reconnect a cable in {Path.GetFileName(localizationPath)}");
}

var captureRecoveryWindowPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "App", "Windows", "CaptureRecoveryWindow.xaml"));
var captureRecoveryWindow = XDocument.Load(captureRecoveryWindowPath);
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
Equal(0, highDpiWindowLayout.Left,
    "oversized high-DPI window starts inside the work area");
Equal(0, highDpiWindowLayout.Top,
    "oversized high-DPI window stays below the work-area top");
Equal(1920, highDpiWindowLayout.Width,
    "oversized high-DPI window is clamped to the work-area width");
Equal(1040, highDpiWindowLayout.Height,
    "oversized high-DPI window is clamped to the work-area height");
Equal(960d, highDpiWindowLayout.MinWidth,
    "minimum width is lowered when 200 percent scaling leaves fewer DIPs");
Equal(520d, highDpiWindowLayout.MinHeight,
    "minimum height is lowered when 200 percent scaling leaves fewer DIPs");

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
var previewQuickActions = mainWindow.Descendants()
    .SingleOrDefault(element =>
        string.Equals((string?)element.Attribute(xaml + "Name"),
            "PreviewQuickActions", StringComparison.Ordinal));
Equal("{Binding PreviewAndObsVisibility}",
    (string?)previewQuickActions?.Attribute("Visibility"),
    "preview quick actions follow the active projection session");
foreach (var automationId in new[]
         {
             "QuickImageSettingsButton", "QuickPreviewWindowButton",
             "QuickScreenshotButton", "QuickRefreshPreviewButton",
             "QuickFullScreenButton",
         })
{
    Equal(true, mainWindow.Descendants().Any(element =>
            string.Equals((string?)element.Attribute("AutomationProperties.AutomationId"),
                automationId, StringComparison.Ordinal)),
        $"preview quick actions contain {automationId}");
}

var sourceDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", ".."));
var releaseManifestPath = Path.Combine(sourceDirectory, "..", "updates", "releases.json");
var repositoryRelease = ReleaseParser.ParseLatest(
    File.ReadAllText(releaseManifestPath), includeStable: true, includePrerelease: true);
var appProject = XDocument.Load(Path.Combine(sourceDirectory, "App",
    "iPhoneMirror.App.csproj"));
var appVersion = appProject.Descendants("Version").Single().Value.Trim();
Equal($"v{appVersion}", repositoryRelease?.TagName,
    "repository update manifest matches the application version");
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
             "PrimaryActionFocusBrush", "PrimaryActionDisabledBrush",
             "PrimaryActionDisabledTextBrush",
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
             "TitleBarButton", "SubWindowCloseButton", "CornerRadius=\"8\"",
             "SubWindowPageRoot", "SubWindowHeader", "SubWindowTitle",
             "SubWindowSubtitle", "SubWindowTabControl", "SubWindowTabItem",
             "IconButtonHoverBrush",
             "ModernVerticalScrollThumbStyle", "ModernHorizontalScrollThumbStyle",
             "ui:ModernButton", "ui:ModernCard", "ui:ModernDialog",
         })
{
    Equal(true, modernControlsText.Contains(reusableControl, StringComparison.Ordinal),
        $"shared UI contains {reusableControl}");
}
Equal(true,
    modernControlsText.Contains("<Style TargetType=\"{x:Type ScrollBar}\">",
        StringComparison.Ordinal) &&
    modernControlsText.Contains("<Setter Property=\"Width\" Value=\"8\"/>",
        StringComparison.Ordinal) &&
    modernControlsText.Contains("<Setter Property=\"Height\" Value=\"8\"/>",
        StringComparison.Ordinal) &&
    modernControlsText.Contains("CornerRadius=\"2.5\"", StringComparison.Ordinal) &&
    modernControlsText.Contains("MinHeight=\"24\"", StringComparison.Ordinal) &&
    modernControlsText.Contains("MinWidth=\"24\"", StringComparison.Ordinal) &&
    modernControlsText.Contains("QuadraticEase", StringComparison.Ordinal) &&
    modernControlsText.Contains("PageLeftCommand", StringComparison.Ordinal) &&
    modernControlsText.Contains("PageRightCommand", StringComparison.Ordinal),
    "shared scrollbars are thin, rounded, animated, and support both orientations");
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
foreach (var windowPath in Directory.GetFiles(appWindowDirectory, "*.xaml"))
{
    var windowText = File.ReadAllText(windowPath);
    if (!windowText.Contains("SubWindowHeader", StringComparison.Ordinal)) continue;
    Equal(true,
        windowText.Contains("WindowDragBehavior.IsEnabled=\"True\"",
            StringComparison.Ordinal) ||
        windowText.Contains("MouseLeftButtonDown=", StringComparison.Ordinal),
        $"{Path.GetFileName(windowPath)} exposes a draggable title region");
}
var windowDragBehaviorText = File.ReadAllText(Path.Combine(sourceDirectory,
    "App", "Animations", "WindowDragBehavior.cs"));
Equal(true,
    windowDragBehaviorText.Contains("window.DragMove()", StringComparison.Ordinal) &&
    windowDragBehaviorText.Contains("ResizeMode.CanResizeWithGrip",
        StringComparison.Ordinal),
    "shared child-window drag behavior supports moving and title double-click");

var mainWindowText = File.ReadAllText(mainWindowPath);
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

var mainWindowCodePath = Path.Combine(sourceDirectory, "App", "MainWindow.xaml.cs");
var mainWindowCode = File.ReadAllText(mainWindowCodePath);
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
Equal(true, mainWindowText.Contains(
        "Background=\"{DynamicResource PreviewChromeBrush}\"", StringComparison.Ordinal),
    "main preview surface follows the active light/dark theme");
Equal(false, mainWindowText.Contains("Background=\"#050505\"",
        StringComparison.Ordinal),
    "main preview does not hard-code a dark background");
var aboutWindowPath = Path.Combine(sourceDirectory, "App", "Windows", "AboutWindow.xaml");
var aboutWindowText = File.ReadAllText(aboutWindowPath);
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
            aboutWindowText.Contains("Style=\"{StaticResource PrimaryButton}\"",
                StringComparison.Ordinal),
    "check for updates uses the audited primary action style");
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
Equal(4, mirrorCandidates.Count,
    "verified update assets include GitHub and three mirror candidates");
Equal("github.com", mirrorCandidates[0].Host,
    "official GitHub download remains the first candidate");
Equal(true, mirrorCandidates.Skip(1).All(candidate =>
        candidate.AbsoluteUri.EndsWith(mirroredAsset.DownloadUri.AbsoluteUri,
            StringComparison.Ordinal)),
    "mirror candidates proxy the exact trusted GitHub asset URL");
Equal(1, GitHubReleaseClient.BuildDownloadCandidates(
        mirroredAsset with { Sha256 = null }, allowMirrorFallback: true).Count,
    "unverified assets never use third-party download mirrors");
Equal(1, GitHubReleaseClient.BuildDownloadCandidates(
        mirroredAsset, allowMirrorFallback: false).Count,
    "disabled mirror fallback keeps the official download only");

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
        Theme = AppTheme.Light,
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
}
finally
{
    if (Directory.Exists(updateSettingsRoot))
        Directory.Delete(updateSettingsRoot, recursive: true);
}

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
        "release check uses only official GitHub metadata endpoints");

    await ThrowsAsync<HttpRequestException>(async () =>
        await releaseClient.GetLatestAsync(new UpdateSettings
        {
            AllowMirrorFallback = false,
            NotifyStableReleases = true,
        }), "disabled release mirror fallback does not contact alternate endpoints");
    Equal(1, releaseRequests.Count(host => host.Equals("raw.githubusercontent.com",
            StringComparison.OrdinalIgnoreCase)),
        "disabled metadata fallback leaves GitHub Raw untouched");

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
    var downloadRequests = new List<string>();
    using var downloadHttpClient = new HttpClient(new StubHttpMessageHandler(
        (request, _) =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            downloadRequests.Add(host);
            if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("simulated GitHub asset outage"));
            if (host.Equals("gh-proxy.org", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(HttpResponse(request,
                    new ByteArrayContent(payload)));
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException($"unexpected endpoint {host}"));
        }));
    using var downloadClient = new GitHubReleaseClient(downloadHttpClient,
        Path.Combine(updateNetworkRoot, "downloads"));
    var downloaded = await downloadClient.DownloadAsync(downloadRelease,
        cancellationToken: default, allowMirrorFallback: true);
    Equal(true, downloaded.HashVerified,
        "mirror download is accepted only after SHA256 verification");
    Equal(true, File.ReadAllBytes(downloaded.Path).SequenceEqual(payload),
        "mirror download preserves the verified payload exactly");
    Sequence(["github.com", "gh-proxy.org"], downloadRequests,
        "asset download tries GitHub before the first verified mirror");
    Throws<InvalidDataException>(() => UpdateInstallerLauncher.Launch(
            downloaded with { HashVerified = false }),
        "installer launcher refuses an update without verified integrity");
}
finally
{
    if (Directory.Exists(updateNetworkRoot))
        Directory.Delete(updateNetworkRoot, recursive: true);
}
Equal(true, UpdateInstallerLauncher.BuildInstallerArguments()
        .Contains("/RESTARTAPP=1", StringComparison.Ordinal),
    "one-click installer update requests application restart");
Equal(true, UpdateInstallerLauncher.BuildInstallerArguments()
        .Contains("/LOG=", StringComparison.Ordinal),
    "one-click installer update persists an installer log");

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

var ffmpegCapabilities = MediaOutputService.CreateCapabilities("ffmpeg.exe",
    " V..... h264_mf\n V..... libx264\n A..... aac\n A..... libopus ",
    "Input:\nrtmp\nOutput:\nrtmp\nsrt", " E flv\n E mpegts\n E whip");
Equal("libx264", ffmpegCapabilities.PreferredH264Encoder,
    "libx264 is preferred when software and Media Foundation encoders are available");
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

var recordingArguments = MediaOutputService.BuildArguments(
    new MediaOutputRequest(MediaOutputKind.Recording, "capture.mp4", 1280, 720, 30, 6000),
    ffmpegCapabilities);
Sequence(["-hide_banner", "-loglevel", "warning", "-nostdin",
        "-thread_queue_size", "512", "-f", "s16le", "-ar", "48000", "-ac", "2",
        "-i", @"\\.\pipe\iphoneMirror-audio-test", "-f", "rawvideo",
        "-pixel_format", "bgra", "-video_size", "1280x720", "-framerate", "30",
        "-i", "pipe:0", "-map", "1:v:0", "-map", "0:a:0", "-c:v", "libx264",
        "-preset", "veryfast", "-tune",
        "zerolatency", "-pix_fmt", "yuv420p", "-g", "60", "-b:v", "6000k",
        "-maxrate", "6000k", "-bufsize", "12000k", "-c:a", "aac",
        "-b:a", "192k", "-movflags", "+faststart",
        "-y", "capture.mp4"],
    recordingArguments,
    "recording opens the audio pipe before stdin video and keeps stable mapping");

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

var portraitPixels = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
await using (var portraitOutput = new MemoryStream())
{
    await MediaOutputService.WriteFrameAsync(portraitOutput,
        new IPhoneMirror.App.Interop.VideoFrame(2, 4, 8, 1, portraitPixels),
        4, 4, new byte[64], CancellationToken.None);
    var output = portraitOutput.ToArray();
    Equal(64, output.Length, "portrait output keeps the requested fixed canvas size");
    for (var row = 0; row < 4; ++row)
    {
        Equal(true, output.AsSpan(row * 16, 4).SequenceEqual(new byte[4]),
            $"portrait row {row} has a black left pillar");
        Equal(true, output.AsSpan(row * 16 + 4, 8)
                .SequenceEqual(portraitPixels.AsSpan(row * 8, 8)),
            $"portrait row {row} is centered without distortion");
        Equal(true, output.AsSpan(row * 16 + 12, 4).SequenceEqual(new byte[4]),
            $"portrait row {row} has a black right pillar");
    }
}

var landscapePixels = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
await using (var landscapeOutput = new MemoryStream())
{
    await MediaOutputService.WriteFrameAsync(landscapeOutput,
        new IPhoneMirror.App.Interop.VideoFrame(4, 2, 16, 2, landscapePixels),
        4, 4, new byte[64], CancellationToken.None);
    var output = landscapeOutput.ToArray();
    Equal(true, output.AsSpan(0, 16).SequenceEqual(new byte[16]),
        "landscape output has a black top bar");
    Equal(true, output.AsSpan(16, 32).SequenceEqual(landscapePixels),
        "landscape output is vertically centered without distortion");
    Equal(true, output.AsSpan(48, 16).SequenceEqual(new byte[16]),
        "landscape output has a black bottom bar");
}

await ThrowsAsync<InvalidDataException>(() => MediaOutputService.WriteFrameAsync(
        Stream.Null,
        new IPhoneMirror.App.Interop.VideoFrame(8, 8, 32, 3, new byte[256]),
        4, 4, new byte[64], CancellationToken.None),
    "a native frame larger than the fixed output canvas is rejected");

var processTestRequest = new MediaOutputRequest(MediaOutputKind.Recording,
    Path.Combine(Path.GetTempPath(), $"process-test-{Guid.NewGuid():N}.mp4"),
    160, 160, 10, 500);
await using (var immediateExitOutput = new MediaOutputService((_, _, _) => null,
    (_, afterSequence) => afterSequence == 0
        ? new IPhoneMirror.App.Interop.AudioPacket(
            1, 48000, 2, 16, new byte[4])
        : null))
{
    var immediateExitCapabilities = ffmpegCapabilities with
    {
        FfmpegPath = Path.Combine(Environment.SystemDirectory, "whoami.exe"),
    };
    await ThrowsAsync<InvalidOperationException>(() => immediateExitOutput.StartAsync(
            1, processTestRequest, immediateExitCapabilities),
        "an output process that exits during startup is rejected");
    Equal(false, immediateExitOutput.IsRunning,
        "immediate process exit does not publish a running output");
    Equal(0UL, immediateExitOutput.SessionHandle,
        "immediate process exit does not retain the session handle");
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
    await using var silentRecordingOutput = new MediaOutputService(
        (_, width, height) => new IPhoneMirror.App.Interop.VideoFrame(
            width, height, width * 4,
            Interlocked.Add(ref recordingTimestamp, 1_000_000),
            new byte[checked((int)(width * height * 4))]),
        (_, _) => null);
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
        (_, width, height) => new IPhoneMirror.App.Interop.VideoFrame(
            width, height, width * 4,
            Interlocked.Add(ref audioRecordingTimestamp, 1_000_000),
            new byte[checked((int)(width * height * 4))]),
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
    long interruptedVideoTimestamp = 0;
    await using var interruptedAudioOutput = new MediaOutputService(
        (_, width, height) => new IPhoneMirror.App.Interop.VideoFrame(
            width, height, width * 4,
            Interlocked.Add(ref interruptedVideoTimestamp, 1_000_000),
            new byte[checked((int)(width * height * 4))]),
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
            "a PCM interruption longer than five seconds does not stop video output");
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
