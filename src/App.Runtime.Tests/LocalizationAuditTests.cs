using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using IPhoneMirror.App;

namespace IPhoneMirror.App.Runtime.Tests;

internal static class LocalizationAuditTests
{
    // Compare source text with compiled BAML: a source-only XML audit cannot
    // detect WPF collapsing explicit line breaks in a resource string.
    private static void AssertResourceTextPreserved(ResourceDictionary dictionary, string project, string culture)
    {
        using var source = typeof(LocalizationAuditTests).Assembly.GetManifestResourceStream(
            $"TextSources.{project}.Strings.{culture}.xaml")
            ?? throw new InvalidOperationException($"Missing text source: {project}/{culture}");
        var document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var node in document.Root!.Elements().Where(node => node.Name.LocalName == "String"))
        {
            var key = (string)node.Attribute(x + "Key")!;
            var expected = node.Value;
            if (dictionary[key] is not string actual || actual != expected)
                throw new InvalidOperationException(
                    $"Compiled text differs from source (including line breaks): {project}/{culture}/{key}");
        }
    }

    internal static int RunDriver(string assemblyPath)
    {
        Assembly.Load("Wpf.Ui");
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var app = (Application)Activator.CreateInstance(assembly.GetType("IPhoneMirror.DriverInstaller.App")!)!;
        app.GetType().GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
        app.GetType().GetMethod("InitializeComponent")!.Invoke(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var localization = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverLocalization")!;
        var initialize = localization.GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!;
        var get = localization.GetMethod("Get", BindingFlags.Static | BindingFlags.NonPublic)!;
        var translate = localization.GetMethod("LocalizeOperationResult", BindingFlags.Static | BindingFlags.NonPublic)!;
        var formats = 0;
        var outcomes = 0;
        ResourceDictionary? previous = null;
        foreach (var cultureName in new[] { "zh-CN", "zh-HK", "en-US" })
        {
            initialize.Invoke(null, [new[] { "--language", cultureName }]);
            if (previous is not null) app.Resources.MergedDictionaries.Remove(previous);
            var dictionary = new ResourceDictionary
            {
                Source = new Uri($"/{assembly.GetName().Name};component/Localization/Strings.{cultureName}.xaml", UriKind.Relative),
            };
            app.Resources.MergedDictionaries.Insert(0, dictionary);
            previous = dictionary;
            AssertResourceTextPreserved(dictionary, "DriverInstaller", cultureName);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is not string text) continue;
                if ((string)get.Invoke(null, [entry.Key])! != text || string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException($"Missing driver resource: {cultureName}/{entry.Key}");
                var format = CompositeFormat.Parse(text);
                foreach (var count in new[] { 0, 1, 2, 5 })
                {
                    _ = string.Format(CultureInfo.GetCultureInfo(cultureName), text,
                        Enumerable.Repeat<object>(count, format.MinimumArgumentCount).ToArray());
                    formats++;
                }
            }
            // The elevated host retains its protocol text. UI translation must
            // preserve raw technical details and must not rewrite unknown data.
            foreach (var (source, key) in new[]
            {
                ("The incorrect Apple parent device was removed. Reconnect the iPhone to rebind usbccgp.", "DriverParentRemoved"),
                ("Selected-device capture filter removed. Reconnect the device to complete unload.", "DriverFilterRemovedReconnect"),
                ("Selected-device capture filter installed. Reconnect the device to complete activation.", "DriverFilterInstalledReconnect"),
                ("Parent driver repair stopped after the removal request began. Reconnect the iPhone and review the operation log. ", "DriverParentRepairStopped"),
                ("Parent driver repair was rejected before any system change. ", "DriverParentRepairRejected"),
                ("Driver operation failed and all captured state was restored. ", "DriverOperationRolledBack"),
                ("Driver operation failed and rollback was incomplete. Review the operation log. ", "DriverOperationRollbackIncomplete"),
            })
            {
                const string detail = "Win32=5; USB\\VID_05AC; file=C:\\Temp\\driver.log";
                var actual = (string)translate.Invoke(null, [source + detail])!;
                var summary = (string)get.Invoke(null, [key])!;
                if (!actual.StartsWith(summary + "\n", StringComparison.Ordinal) ||
                    !actual.EndsWith(detail, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Incorrect driver outcome or lost details: {cultureName}/{key}");
                if ((string)translate.Invoke(null, [detail])! != detail)
                    throw new InvalidOperationException("Unknown diagnostic data must remain intact.");
                outcomes++;
            }
            foreach (var key in new[]
            {
                "ParentBindingComplete", "ParentBindingRestartRequired", "ParentResetRestartRequired",
                "ParentResetComplete", "ParentChangeRejected", "ParentChangeRolledBack",
                "ParentRollbackRestartRequired", "ParentChangeRecoveryNeeded",
            })
            {
                var actual = (string)translate.Invoke(null, [key])!;
                if (actual == key || actual != (string)get.Invoke(null, [key])!)
                    throw new InvalidOperationException($"Untranslated parent driver outcome: {cultureName}/{key}");
                outcomes++;
            }
        }
        app.Shutdown();
        Console.WriteLine($"Driver localization audit passed: {formats} format cases, 3 dictionary switches, {outcomes} translated outcomes.");
        return 0;
    }

    internal static int Run()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, true);
        app.InitializeComponent();
        var assembly = typeof(App).Assembly;
        var localization = assembly.GetType("IPhoneMirror.App.Localization.LocalizationService")!;
        var apply = localization.GetMethod("ApplyLanguage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var get = localization.GetMethod("Get", BindingFlags.NonPublic | BindingFlags.Static)!;
        var serviceType = assembly.GetType("IPhoneMirror.App.Services.ControlStatusService")!;
        var modeType = assembly.GetType("IPhoneMirror.App.Services.ControlStatusMode")!;
        var formats = 0;
        var captions = new[] { "DeviceBindingBound", "ShortcutSettingsWirelessControl", "ShortcutSettingsWiredControl" };
        // Switching back to the first language catches stale/replaced dictionary issues.
        foreach (var cultureName in new[] { "zh-CN", "zh-HK", "en-US", "zh-CN" })
        {
            apply.Invoke(null, [cultureName, false, true]);
            var dictionary = new ResourceDictionary
            {
                Source = new Uri($"/{assembly.GetName().Name};component/Localization/Strings.{cultureName}.xaml", UriKind.Relative),
            };
            AssertResourceTextPreserved(dictionary, "App", cultureName);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is not string text) continue;
                var actual = (string)get.Invoke(null, [entry.Key])!;
                if (string.IsNullOrWhiteSpace(actual) || actual != text)
                    throw new InvalidOperationException($"Missing or stale resource: {cultureName}/{entry.Key}");
                var format = CompositeFormat.Parse(text);
                foreach (var count in new[] { 0, 1, 2, 5 })
                {
                    _ = string.Format(CultureInfo.GetCultureInfo(cultureName), text,
                        Enumerable.Repeat<object>(count, format.MinimumArgumentCount).ToArray());
                    formats++;
                }
            }
            var expected = cultureName switch
            {
                "zh-CN" => new[] { "未绑定", "无线控制", "有线控制" },
                "zh-HK" => new[] { "未綁定", "無線控制", "有線控制" },
                _ => new[] { "Not bound", "Wireless control", "Wired control" },
            };
            for (var i = 0; i < captions.Length; i++)
                if ((string)get.Invoke(null, [captions[i]])! != expected[i])
                    throw new InvalidOperationException($"Incorrect UI meaning: {cultureName}/{captions[i]}");
            // Default service messages used to be hardcoded Chinese, including on
            // background callbacks. Exercise the actual workflow without device IO.
            foreach (var mode in Enum.GetValues(modeType))
            {
                var service = Activator.CreateInstance(serviceType, nonPublic: true)!;
                serviceType.GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [mode, "Test iPhone"]);
                serviceType.GetMethod("Ready", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [mode, "Test iPhone", null]);
                var snapshot = serviceType.GetProperty("Current", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                var description = (string)snapshot.GetType().GetProperty("Description")!.GetValue(snapshot)!;
                if (description != (string)get.Invoke(null, ["ControlReadyDescription"])!)
                    throw new InvalidOperationException($"Unlocalized ready description: {cultureName}/{mode}");
                serviceType.GetMethod("Cancelled", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [mode, "Test iPhone", null, null]);
                snapshot = serviceType.GetProperty("Current", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                description = (string)snapshot.GetType().GetProperty("Description")!.GetValue(snapshot)!;
                if (description != (string)get.Invoke(null, ["ControlStageCancelled"])!)
                    throw new InvalidOperationException($"Unlocalized cancellation: {cultureName}/{mode}");
            }
        }
        app.Shutdown();
        Console.WriteLine($"Localization runtime audit passed: {formats} format cases, 4 dictionary switches, 12 control workflows.");
        return 0;
    }
}
