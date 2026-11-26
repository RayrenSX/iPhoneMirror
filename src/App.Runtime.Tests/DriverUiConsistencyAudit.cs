using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDriverConsistencyAudit(string sourceRoot, string assemblyPath, string output, string? onlyCulture = null)
    {
        // Keep batch UI verification independent of display-driver teardown.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Directory.CreateDirectory(output);
        Assembly.Load("Wpf.Ui");
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        // Initialize compiled resources through the same entry point as the executable.
        // Preview mode bypasses driver startup and device discovery.
        var app = (Application)Activator.CreateInstance(assembly.GetType("IPhoneMirror.DriverInstaller.App")!)!;
        app.GetType().GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
        app.GetType().GetMethod("InitializeComponent")!.Invoke(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var findings = new HashSet<string>();
        var failures = new List<string>();
        var count = 0;
        using var bindingTrace = new AuditTraceListener();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(bindingTrace);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
        var themeService = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverThemeService")!;
        var themeMode = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverThemeMode")!;
        var localizationService = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverLocalization")!;
        var initialize = localizationService.GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!;
        var appearance = assembly.GetType("IPhoneMirror.UI.Services.AccessibilityAppearance")!
            .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
        var combinations = new List<object>();
        ResourceDictionary? localization = null;
        foreach (var culture in (onlyCulture is not null ? new[] { onlyCulture } : QuickLayoutAudit ? new[] { "en-US" } : IPhoneMirror.UI.Services.SupportedUiCultures.All))
        foreach (var theme in (QuickLayoutAudit ? new[] { "Light" } : new[] { "Light", "Dark" }))
        foreach (var textScale in (QuickLayoutAudit ? new[] { 1d } : new[] { 1d, 2d }))
        {
            initialize.Invoke(null, [new[] { "--language", culture }]);
            if (localization != null) app.Resources.MergedDictionaries.Remove(localization);
            localization = new ResourceDictionary { Source = new Uri($"/{assembly.GetName().Name};component/Localization/Strings.{culture}.xaml", UriKind.Relative) };
            app.Resources.MergedDictionaries.Insert(0, localization);
            themeService.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Enum.Parse(themeMode, theme), false]);
            appearance.Invoke(null, [app, (double?)textScale, (bool?)false]);
            if (app.FindResource("AppFontFamily") is not System.Windows.Media.FontFamily font ||
                !font.Source.Contains(culture == "ja-JP" ? "Yu Gothic UI" : "Microsoft YaHei UI"))
                throw new InvalidOperationException("Driver font fallback disagrees with the selected language: " + culture);
            Window Construct(string name, params object?[] arguments) => (Window)Activator.CreateInstance(
                assembly.GetType("IPhoneMirror.DriverInstaller." + name)!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, arguments, null)!;
            var owner = Construct("MainWindow");
            var loaded = owner.GetType().GetMethod("OnLoaded", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
            owner.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), owner, loaded);
            var auditDevice = CreateParentAuditDevice(assembly);
            var auditChoices = CreateParentAuditChoices(assembly);
            var auditTarget = auditChoices.GetValue(0)!;
            ((System.Collections.IList)owner.GetType().GetProperty("Devices")!.GetValue(owner)!).Add(auditDevice);
            app.MainWindow = owner;
            owner.Show();
            try
            {
                owner.Opacity = 0.63;
                themeService.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [Enum.Parse(themeMode, theme), false]);
                var themeWait = System.Diagnostics.Stopwatch.StartNew();
                do { AdvanceDispatcher(TimeSpan.FromMilliseconds(100)); }
                while (Math.Abs(owner.Opacity - 0.63) > 0.001 && themeWait.Elapsed < TimeSpan.FromSeconds(4));
                if (Math.Abs(owner.Opacity - 0.63) > 0.001)
                    throw new InvalidOperationException($"Driver theme transition overwrote window opacity: {owner.Opacity}, base={owner.GetAnimationBaseValue(UIElement.OpacityProperty)}; assembly={assembly.Location}.");
                owner.Opacity = 1;
                // Applying the theme above restores the real Windows text scale.
                // Reapply the simulated scale only after that transition finishes.
                appearance.Invoke(null, [app, (double?)textScale, (bool?)false]);
                owner.UpdateLayout();
                var actualTitle = Visuals(owner).OfType<System.Windows.Controls.TextBlock>()
                    .Single(block => block.Text == (string)app.FindResource("AppTitle"));
                if (Math.Abs(actualTitle.FontSize - (textScale == 1 ? 22 : 38)) > 0.001)
                    throw new InvalidOperationException($"Driver actual title size disagrees with its matrix label: {culture}/{textScale}: {actualTitle.FontSize}.");
                const string diagnostic = "Win32=5; USB\\VID_05AC; access denied while reading device properties.";
                const string logPath = @"C:\Users\Preview\AppData\Local\iPhoneMirror\Logs\driver-operation-20261001.log";
                foreach (var advanced in new[] { false, true })
                {
                    owner.GetType().GetProperty("IsAdvancedMode")!.SetValue(owner, advanced);
                    owner.UpdateLayout();
                    var name = $"{culture}-{theme}-{textScale}-driver-{(advanced ? "advanced" : "simple")}";
                    ExerciseTabsAndPopups(owner, findings);
                    SaveWindowRender(owner, Path.Combine(output, name + ".png"));
                    ExerciseWorkAreaSizes(owner, name, findings, Path.Combine(output, name));
                    count++;
                    combinations.Add(new { culture, theme, textScale, actualTitleSize = actualTitle.FontSize, surface = name });
                    // Render the actual new status messages in both layouts without
                    // starting an elevated operation, cancelling one, or touching PnP.
                    var originalStatus = owner.GetType().GetProperty("OperationStatus")!.GetValue(owner);
                    foreach (var statusKey in new[] { "DriverWaitingSafeStop", "DriverRefreshFailed" })
                    {
                        var status = statusKey == "DriverWaitingSafeStop"
                            ? string.Format((string)app.FindResource(statusKey), logPath)
                            : app.FindResource("ParentChangeRecoveryNeeded") + "\n" +
                              string.Format((string)app.FindResource(statusKey), diagnostic, logPath);
                        owner.GetType().GetProperty("OperationStatus")!.SetValue(owner, status);
                        owner.GetType().GetProperty("IsBusy")!.SetValue(owner, statusKey == "DriverWaitingSafeStop");
                        // Process queued binding updates before checking rendered
                        // captions after the preceding resize/scroll exercise.
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                        owner.UpdateLayout();
                        if (FindVisualDescendant<System.Windows.Controls.TextBlock>(owner,
                                block => block.IsVisible && block.Text == status) is null)
                            throw new InvalidOperationException($"Driver status not displayed: {culture}/{statusKey}");
                        var statusName = name + "-" + statusKey;
                        SaveWindowRender(owner, Path.Combine(output, statusName + ".png"));
                        ExerciseWorkAreaSizes(owner, statusName, findings, Path.Combine(output, statusName));
                        count++;
                        combinations.Add(new { culture, theme, textScale, surface = statusName });
                    }
                    owner.GetType().GetProperty("IsBusy")!.SetValue(owner, false);
                    owner.GetType().GetProperty("OperationStatus")!.SetValue(owner, originalStatus);
                }
                const string previewLog = @"C:\ProgramData\iPhoneMirror.Driver\Operations\Preview\operation.log";
                foreach (var name in new[] { "PromptWindow", "RequiredActionWindow", "FailureHelpWindow", "DeviceTrustWindow", "PromptLong", "RequiredActionLong", "ParentDriverWindow", "ParentConfirm", "ParentResetConfirm", "QuickInstallConfirm", "ReconnectVerificationFailed", "HelpBrowserFailed", "CleanupLaunchFailed", "OpenLogFailed" })
                {
                    var window = name switch
                    {
                        "ReconnectVerificationFailed" => Construct("Windows.FailureHelpWindow", app.FindResource(name), previewLog),
                        "HelpBrowserFailed" => Construct("Windows.PromptWindow", app.FindResource("HelpBrowserFailedTitle"),
                            string.Format((string)app.FindResource(name), "https://www.i4.cn/"),
                            app.FindResource("Acknowledge"), string.Empty, false, false, true),
                        "CleanupLaunchFailed" => Construct("Windows.PromptWindow", app.FindResource("DriverCleanupUnavailableTitle"),
                            app.FindResource("DriverCleanupScriptStartFailed"),
                            app.FindResource("Acknowledge"), string.Empty, false, false, true),
                        "OpenLogFailed" => Construct("Windows.PromptWindow", app.FindResource("CannotOpenLogs"),
                            app.FindResource("OpenLogFailedBody"),
                            app.FindResource("Acknowledge"), string.Empty, false, false, true),
                        "ParentConfirm" => Construct("Windows.PromptWindow", app.FindResource("ParentConfirmTitle"),
                            string.Format((string)app.FindResource("ParentConfirmBody"),
                                "iPhone 16 Pro Max · iOS 26.0",
                                $"Microsoft · usb.inf · {auditTarget.GetType().GetProperty("VersionText")!.GetValue(auditTarget)}"),
                            app.FindResource("ParentConfirmApply"), app.FindResource("Cancel"), true, true, false),
                        "ParentResetConfirm" => Construct("Windows.PromptWindow", app.FindResource("ParentReset"),
                            string.Format((string)app.FindResource("ParentReenumerateConfirmBody"), "iPhone 16 Pro Max · iOS 26.0"),
                            app.FindResource("ParentReset"), app.FindResource("Cancel"), true, true, false),
                        "QuickInstallConfirm" => Construct("Windows.PromptWindow", app.FindResource("QuickInstallTitle"),
                            app.FindResource("QuickInstallBody"), app.FindResource("StartInstall"),
                            app.FindResource("Cancel"), true, false, false),
                        "ParentDriverWindow" => Construct("Windows.ParentDriverWindow", auditDevice, auditChoices,
                            app.FindResource("ParentListPartial") + "\n" + string.Join("\n", Enumerable.Repeat(diagnostic, 8))),
                        "PromptLong" => Construct("Windows.PromptWindow", "Device confirmation / 裝置確認", string.Join(" ", Enumerable.Repeat("Confirm device trust before continuing. 请确认设备已信任此电脑。", 40)), "Continue after confirming device trust and developer mode / 确认后继续", app.FindResource("Cancel"), true, false, false),
                        "RequiredActionLong" => Construct("Windows.RequiredActionWindow", "Device confirmation / 裝置確認", string.Join(" ", Enumerable.Repeat("Confirm device trust before continuing. 请确认设备已信任此电脑。", 40)), "Continue after confirming device trust and developer mode / 确认后继续"),
                        "PromptWindow" => Construct("Windows." + name,
                            string.Format((string)app.FindResource("ConfirmOperation"), app.FindResource("Repair")),
                            string.Format((string)app.FindResource("ConfirmOperationBody"), "Test iPhone"),
                            app.FindResource("Repair"), app.FindResource("Cancel"), true, false, false),
                        "RequiredActionWindow" => Construct("Windows." + name, "iPhoneMirror",
                            string.Format((string)app.FindResource("AppleRestartRequired"), "C:\\Preview\\driver.log"), app.FindResource("Continue")),
                        "FailureHelpWindow" => Construct("Windows." + name,
                            app.FindResource("ParentChangeRolledBack"), previewLog),
                        _ => Construct("Windows." + name),
                    };
                    window.Owner = owner;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                    {
                        try
                        {
                            window.UpdateLayout();
                            AssertSelfDrawnWindowCorners(window);
                            if (window.GetType().Name == "PromptWindow")
                                AssertNoticeHeaderAlignment(window, "HeaderTitle");
                            if (window.GetType().Name == "FailureHelpWindow")
                            {
                                AssertNoticeHeaderAlignment(window, "HeaderTitle");
                                InteractionAssert(!FindVisualDescendants<System.Windows.Controls.Expander>(window).Any(),
                                    "Driver recovery options must not require expansion.");
                                foreach (var id in new[] { "FailureCopyGroup", "FailureOpenWebsite", "FailureCleanup", "FailureOpenLog" })
                                {
                                    InteractionAssert(FindVisualDescendants<System.Windows.Controls.Button>(window)
                                        .Any(button => button.IsVisible && System.Windows.Automation.AutomationProperties
                                            .GetAutomationId(button) == id), $"Recovery action {id} was not visible.");
                                }
                                InteractionAssert(!FindVisualDescendants<System.Windows.Controls.TextBlock>(window)
                                    .Any(text => text.IsVisible && text.Text.Contains(previewLog, StringComparison.OrdinalIgnoreCase))
                                    && !FindVisualDescendants<System.Windows.Controls.TextBox>(window).Any(text => text.IsVisible),
                                    "Driver log location should be available through the button only.");
                            }
                            var key = $"{culture}-{theme}-{textScale}-{name}";
                            if (name == "ParentDriverWindow")
                            {
                                var list = FindVisualDescendant<System.Windows.Controls.ListBox>(window, _ => true)!;
                                var item = (System.Windows.Controls.ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                                var border = (System.Windows.Controls.Border)item.Template.FindName("ItemRoot", item);
                                if (((System.Windows.Media.SolidColorBrush)border.BorderBrush).Color !=
                                    ((System.Windows.Media.SolidColorBrush)app.FindResource("AccentBrush")).Color)
                                    throw new InvalidOperationException("Parent driver selection must use the application accent.");
                                var details = (FrameworkElement)window.FindName("TechnicalDetails");
                                var detailsButton = (System.Windows.Controls.Button)window.FindName("DetailsButton");
                                var content = (System.Windows.Controls.ScrollViewer)window.FindName("DriverContentScroll");
                                var summary = (FrameworkElement)window.FindName("DeviceSummary");
                                InteractionAssert(!details.IsVisible && summary.TranslatePoint(new Point(), content).Y >= 0,
                                    "The device summary should appear before optional technical diagnostics.");
                                AssertVisibleButtonsFit(window);
                                SaveWindowRender(window, Path.Combine(output, key + ".png"));
                                detailsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                                window.UpdateLayout();
                                InteractionAssert(details.IsVisible &&
                                    FindVisualDescendants<System.Windows.Controls.TextBlock>(details).Any(block =>
                                        block.IsVisible && block.Text.Contains("Win32=5", StringComparison.Ordinal)),
                                    "Driver diagnostics were lost when opening technical details.");
                                detailsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                                content.ScrollToTop();
                                window.UpdateLayout();
                            }
                            else
                            {
                                AssertVisibleButtonsFit(window);
                                SaveWindowRender(window, Path.Combine(output, key + ".png"));
                            }
                            ExerciseWorkAreaSizes(window, key, findings, Path.Combine(output, key));
                            AssertEscapeCloses(window);
                            count++;
                            combinations.Add(new { culture, theme, textScale, surface = name });
                        }
                        catch (Exception error) { failures.Add($"{culture}/{theme}/{name}: {error.GetBaseException().Message}"); }
                        finally { if (window.IsVisible) window.Close(); }
                    }));
                    window.ShowDialog();
                }
            }
            finally
            {
                owner.GetType().GetProperty("IsBusy")!.SetValue(owner, false);
                owner.Close();
            }
        }
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingTrace);
        themeService.GetMethod("Shutdown", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        File.WriteAllText(Path.Combine(output, "driver-results.json"), JsonSerializer.Serialize(new { count, combinations, failures, findings, bindingErrors = bindingTrace.Messages }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var failure in failures.Concat(findings)) Console.Error.WriteLine(failure);
        Console.WriteLine($"Driver UI audit: {count} renders, {failures.Count} failures, {findings.Count} layout findings, {bindingTrace.Messages.Count} binding diagnostics.");
        app.Shutdown();
        return failures.Count == 0 && findings.Count == 0 && bindingTrace.Messages.Count == 0 ? 0 : 1;
    }

    // Synthetic fixtures only: never discover hardware or invoke bind/reset actions.
    private static object CreateParentAuditDevice(Assembly assembly) => Activator.CreateInstance(
        assembly.GetType("IPhoneMirror.DriverInstaller.Models.AppleDeviceRecord")!,
        [@"USB\VID_05AC&PID_12A8\UI_AUDIT_ONLY_LONG_DEVICE_IDENTIFIER_000000000000",
         "UI_AUDIT_ONLY", "Test iPhone", "iPhone18,1", "iPhone 16 Pro Max", "iPhone", "26.0",
         1, "usbccgp", true, false, Array.Empty<string>(), (uint?)0, true,
         "usb.inf", "Composite.Dev", "10.0.26100.1", Array.Empty<string>()])!;

    private static Array CreateParentAuditChoices(Assembly assembly)
    {
        var type = assembly.GetType("IPhoneMirror.DriverInstaller.Services.ParentDriverChoice")!;
        var choices = Array.CreateInstance(type, 3);
        choices.SetValue(Activator.CreateInstance(type, [@"C:\Windows\INF\usb.inf", "Composite.Dev",
            "USB Composite Device", "Microsoft", 0x000A000066140001UL, 0L]), 0);
        choices.SetValue(Activator.CreateInstance(type, [@"C:\Windows\INF\oem3.inf", "AppleUSB_CCGPDriverInstall",
            "Apple Mobile Device USB Composite Device", "Apple, Inc.", 0x021A000000000000UL, 0L]), 1);
        choices.SetValue(Activator.CreateInstance(type, [@"C:\Windows\INF\wpdmtp.inf", "MTP",
            "MTP USB Device", "Microsoft", 0x000A000066140001UL, 0L]), 2);
        return choices;
    }
}
