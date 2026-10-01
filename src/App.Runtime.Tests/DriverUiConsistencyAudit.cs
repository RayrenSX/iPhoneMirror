using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDriverConsistencyAudit(string sourceRoot, string assemblyPath, string output)
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
        ResourceDictionary? localization = null;
        foreach (var culture in (QuickLayoutAudit ? new[] { "en-US" } : new[] { "zh-CN", "en-US", "zh-HK" }))
        foreach (var theme in (QuickLayoutAudit ? new[] { "Light" } : new[] { "Light", "Dark" }))
        {
            if (localization != null) app.Resources.MergedDictionaries.Remove(localization);
            localization = new ResourceDictionary { Source = new Uri($"/{assembly.GetName().Name};component/Localization/Strings.{culture}.xaml", UriKind.Relative) };
            app.Resources.MergedDictionaries.Insert(0, localization);
            themeService.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Enum.Parse(themeMode, theme), false]);
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
                const string diagnostic = "Win32=5; USB\\VID_05AC; access denied while reading device properties.";
                const string logPath = @"C:\Users\Preview\AppData\Local\iPhoneMirror\Logs\driver-operation-20261001.log";
                foreach (var advanced in new[] { false, true })
                {
                    owner.GetType().GetProperty("IsAdvancedMode")!.SetValue(owner, advanced);
                    owner.UpdateLayout();
                    var name = $"{culture}-{theme}-driver-{(advanced ? "advanced" : "simple")}";
                    ExerciseTabsAndPopups(owner);
                    SaveWindowRender(owner, Path.Combine(output, name + ".png"));
                    ExerciseWorkAreaSizes(owner, name, findings, Path.Combine(output, name));
                    count++;
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
                        owner.UpdateLayout();
                        if (FindVisualDescendant<System.Windows.Controls.TextBlock>(owner,
                                block => block.IsVisible && block.Text == status) is null)
                            throw new InvalidOperationException($"Driver status not displayed: {culture}/{statusKey}");
                        var statusName = name + "-" + statusKey;
                        SaveWindowRender(owner, Path.Combine(output, statusName + ".png"));
                        ExerciseWorkAreaSizes(owner, statusName, findings, Path.Combine(output, statusName));
                        count++;
                    }
                    owner.GetType().GetProperty("IsBusy")!.SetValue(owner, false);
                    owner.GetType().GetProperty("OperationStatus")!.SetValue(owner, originalStatus);
                }
                foreach (var name in new[] { "PromptWindow", "RequiredActionWindow", "FailureHelpWindow", "DeviceTrustWindow", "PromptLong", "RequiredActionLong", "ParentDriverWindow", "ParentConfirm", "ReconnectVerificationFailed", "HelpBrowserFailed" })
                {
                    var window = name switch
                    {
                        "ReconnectVerificationFailed" => Construct("Windows.PromptWindow", app.FindResource("ParentManagerTitle"),
                            app.FindResource(name) + "\n" + app.FindResource("ParentResetComplete"),
                            app.FindResource("Acknowledge"), string.Empty, false, false),
                        "HelpBrowserFailed" => Construct("Windows.PromptWindow", app.FindResource("HelpBrowserFailedTitle"),
                            string.Format((string)app.FindResource(name), "https://www.i4.cn/", diagnostic),
                            app.FindResource("Acknowledge"), string.Empty, false, false),
                        "ParentConfirm" => Construct("Windows.PromptWindow", app.FindResource("ParentConfirmTitle"),
                            string.Format((string)app.FindResource("ParentConfirmBody"),
                                auditDevice.GetType().GetProperty("SelectionText")!.GetValue(auditDevice),
                                auditDevice.GetType().GetProperty("InstanceId")!.GetValue(auditDevice),
                                auditDevice.GetType().GetProperty("ParentStatusText")!.GetValue(auditDevice),
                                auditTarget.GetType().GetProperty("DisplayText")!.GetValue(auditTarget)),
                            app.FindResource("ParentConfirmApply"), app.FindResource("Cancel"), true, true),
                        "ParentDriverWindow" => Construct("Windows.ParentDriverWindow", auditDevice, auditChoices,
                            app.FindResource("ParentListPartial") + "\n" + string.Join("\n", Enumerable.Repeat(diagnostic, 8))),
                        "PromptLong" => Construct("Windows.PromptWindow", "Device confirmation / 裝置確認", string.Join(" ", Enumerable.Repeat("Confirm device trust before continuing. 请确认设备已信任此电脑。", 40)), "Continue after confirming device trust and developer mode / 确认后继续", app.FindResource("Cancel"), true, false),
                        "RequiredActionLong" => Construct("Windows.RequiredActionWindow", "Device confirmation / 裝置確認", string.Join(" ", Enumerable.Repeat("Confirm device trust before continuing. 请确认设备已信任此电脑。", 40)), "Continue after confirming device trust and developer mode / 确认后继续"),
                        "PromptWindow" => Construct("Windows." + name, "iPhoneMirror",
                            string.Format((string)app.FindResource("ConfirmOperationBody"), app.FindResource("Continue"), "Test iPhone", "USB\\VID_05AC\\Preview"),
                            app.FindResource("Continue"), app.FindResource("Cancel"), true, false),
                        "RequiredActionWindow" => Construct("Windows." + name, "iPhoneMirror",
                            string.Format((string)app.FindResource("AppleRestartRequired"), "C:\\Preview\\driver.log"), app.FindResource("Continue")),
                        "FailureHelpWindow" => Construct("Windows." + name,
                            string.Format((string)app.FindResource("OfflineMsiFailed"), 1603, app.FindResource("CheckPending"), "C:\\Preview\\driver.log")),
                        _ => Construct("Windows." + name),
                    };
                    window.Owner = owner;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                    {
                        try
                        {
                            window.UpdateLayout();
                            AssertSelfDrawnWindowCorners(window);
                            if (name == "ParentDriverWindow")
                            {
                                var list = FindVisualDescendant<System.Windows.Controls.ListBox>(window, _ => true)!;
                                var item = (System.Windows.Controls.ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                                var border = (System.Windows.Controls.Border)item.Template.FindName("ItemRoot", item);
                                if (((System.Windows.Media.SolidColorBrush)border.BorderBrush).Color !=
                                    ((System.Windows.Media.SolidColorBrush)app.FindResource("AccentBrush")).Color)
                                    throw new InvalidOperationException("Parent driver selection must use the application accent.");
                            }
                            var key = $"{culture}-{theme}-{name}";
                            AssertVisibleButtonsFit(window);
                            SaveWindowRender(window, Path.Combine(output, key + ".png"));
                            ExerciseWorkAreaSizes(window, key, findings, Path.Combine(output, key));
                            AssertEscapeCloses(window);
                            count++;
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
        File.WriteAllText(Path.Combine(output, "driver-results.json"), JsonSerializer.Serialize(new { count, failures, findings, bindingErrors = bindingTrace.Messages }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var failure in failures.Concat(findings)) Console.Error.WriteLine(failure);
        Console.WriteLine($"Driver UI audit: {count} renders, {failures.Count} failures, {findings.Count} layout findings, {bindingTrace.Messages.Count} binding diagnostics.");
        app.Shutdown();
        return failures.Count == 0 && findings.Count == 0 && bindingTrace.Messages.Count == 0 ? 0 : 1;
    }

    // Synthetic fixtures only: never discover hardware or invoke bind/reset actions.
    private static object CreateParentAuditDevice(Assembly assembly) => Activator.CreateInstance(
        assembly.GetType("IPhoneMirror.DriverInstaller.Models.AppleDeviceRecord")!,
        [@"USB\VID_05AC&PID_12A8\UI_AUDIT_ONLY_LONG_DEVICE_IDENTIFIER_000000000000",
         "UI_AUDIT_ONLY", "Test iPhone", "iPhone18,1", "iPhone Pro Max",
         "Development and diagnostics device / 开发与诊断设备 / 開發與診斷裝置", "26.0",
         1, "usbccgp", true, false, Array.Empty<string>(), (uint?)0, true,
         "usb.inf", "Composite.Dev", "10.0.26100.1", Array.Empty<string>()])!;

    private static Array CreateParentAuditChoices(Assembly assembly)
    {
        var type = assembly.GetType("IPhoneMirror.DriverInstaller.Services.ParentDriverChoice")!;
        var choices = Array.CreateInstance(type, 4);
        for (var i = 0; i < choices.Length; i++)
            choices.SetValue(Activator.CreateInstance(type,
                [i == 0 ? @"C:\Windows\INF\usb.inf" : $@"C:\Windows\INF\oem{i}.inf",
                 i == 0 ? "Composite.Dev" : "Audit.Device", $"USB composite device candidate {i + 1} / USB 复合设备驱动候选项 / USB 複合裝置驅動程式候選項",
                 "Microsoft / Driver provider", 0x000A000066140001UL, 0L]), i);
        return choices;
    }
}
