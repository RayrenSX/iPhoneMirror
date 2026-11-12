using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWirelessRaopPortTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var store = new UpdateSettingsStore(Path.Combine(output, "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var receiver = (WirelessReceiverController)KeyboardField(vm, "_wireless");
        try
        {
            var legacy = Path.Combine(output, "legacy.json");
            File.WriteAllText(legacy, "{}");
            InteractionAssert(new UpdateSettingsStore(legacy).Load().WirelessRaopPort == 5001,
                "Existing settings lost the default RAOP port.");
            foreach (var invalid in new[] { 0, -1, 65536, 7001, 8090 })
            {
                File.WriteAllText(legacy, $"{{\"WirelessRaopPort\":{invalid}}}");
                InteractionAssert(new UpdateSettingsStore(legacy).Load().WirelessRaopPort == 5001,
                    "Invalid saved port was not migrated safely.");
                vm.WirelessRaopPortText = invalid.ToString(CultureInfo.InvariantCulture);
                InteractionAssert(vm.WirelessRaopPortValidationVisibility == Visibility.Visible,
                    "Invalid port has no inline error.");
            }
            foreach (var invalid in new[] { "", "port", "1.5", "5 001" })
            {
                vm.WirelessRaopPortText = invalid;
                InteractionAssert(vm.WirelessRaopPortValidation.Length > 0, "Non-integer port was accepted.");
            }
            using var listener = new TcpListener(IPAddress.Any, 0);
            listener.ExclusiveAddressUse = true;
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            InteractionAssert(WirelessReceiverConfiguration.CheckRaopPortAvailability(port)?.Contains(port.ToString()) == true,
                "An occupied port was not reported.");
            vm.WirelessRaopPortText = port.ToString(CultureInfo.InvariantCulture);
            WaitReviewTask((Task)KeyboardCall(vm, "RestartWirelessReceiverAsync")!);
            KeyboardCall(vm, "RefreshWirelessStatus");
            InteractionAssert(vm.WirelessRaopPortValidationVisibility == Visibility.Visible &&
                vm.WirelessRaopPortValidation.Contains(port.ToString()) && receiver.RaopPort == 5001,
                "Applying an occupied port lost its inline error or changed the running configuration.");
            listener.Stop();
            InteractionAssert(WirelessReceiverConfiguration.CheckRaopPortAvailability(port) is null,
                "A released port remains unavailable.");

            vm.WirelessRaopPortText = "";
            vm.WirelessRaopPortText = port.ToString(CultureInfo.InvariantCulture);
            InteractionAssert(receiver.RaopPort == 5001, "Editing the port restarted or reconfigured the live receiver.");
            vm.SelectedWirelessReceiverBackend = vm.WirelessReceiverBackends.First(o => o.Backend == WirelessReceiverBackend.UxPlay);
            InteractionAssert(vm.WirelessRaopPortVisibility == Visibility.Collapsed, "Original-only port setting leaks into UxPlay.");
            vm.SelectedWirelessReceiverBackend = vm.WirelessReceiverBackends.First(o => o.Backend == WirelessReceiverBackend.Original);
            InteractionAssert(vm.WirelessRaopPortVisibility == Visibility.Visible && vm.WirelessRaopPortValidation.Length == 0,
                "Switching backends lost the draft or its visibility.");
            receiver.RaopPort = port;
            app.IsUiPreviewMode = false;
            InteractionAssert((bool)KeyboardCall(vm, "PersistWirelessReceiverSettings", WirelessReceiverBackend.Original,
                vm.SelectedWirelessDisplayProfile, "RAOP test")! && store.Load().WirelessRaopPort == port &&
                store.Load().Clone().WirelessRaopPort == port, "RAOP port did not survive save/load/clone.");
            var previousPort = app.UpdateSettings.WirelessRaopPort;
            var blocked = Path.Combine(output, "blocked");
            File.WriteAllText(blocked, "file instead of directory");
            SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(blocked, "settings.json")));
            receiver.RaopPort = port == 5002 ? 5003 : 5002;
            InteractionAssert(!(bool)KeyboardCall(vm, "PersistWirelessReceiverSettings", WirelessReceiverBackend.Original,
                vm.SelectedWirelessDisplayProfile, "RAOP test")! && app.UpdateSettings.WirelessRaopPort == previousPort,
                "Failed save replaced the previous persisted port.");
            app.IsUiPreviewMode = true;
            SetKeyboardField(app, "_settingsStore", store);

            var core = (NativeCore)KeyboardField(vm, "_core");
            foreach (var invalid in new[] { 0, 65536, 7001, 8090 })
                InteractionAssert(!core.StartWirelessReceiver("RAOP port test", Environment.ProcessPath!, 1280, 720, 30, invalid).Success,
                    "Native API accepted an invalid/conflicting port.");
            var argumentsPath = Path.Combine(output, "host-arguments.txt");
            File.Delete(argumentsPath);
            var previousEnvironment = Environment.GetEnvironmentVariable("IPHONE_MIRROR_TEST_RAOP_ARGUMENTS");
            Environment.SetEnvironmentVariable("IPHONE_MIRROR_TEST_RAOP_ARGUMENTS", argumentsPath);
            try
            {
                InteractionAssert(core.StartWirelessReceiver("RAOP port test", Environment.ProcessPath!, 1280, 720, 30, port).Success,
                    "Native receiver failed to launch the inert host fixture.");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(argumentsPath) && DateTime.UtcNow < deadline)
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
                var arguments = File.ReadAllLines(argumentsPath);
                InteractionAssert(arguments[Array.IndexOf(arguments, "--raop-port") + 1] == port.ToString() &&
                    arguments[Array.IndexOf(arguments, "--airplay-port") + 1] == "7001",
                    "RAOP port was lost between managed settings, C ABI and the real child process.");
            }
            finally
            {
                core.StopWirelessReceiver();
                Environment.SetEnvironmentVariable("IPHONE_MIRROR_TEST_RAOP_ARGUMENTS", previousEnvironment);
            }

            KeyboardCall(window, "SetSettingsPanelVisible", true);
            FinishWorkspaceReveal(window);
            ((RadioButton)window.FindName("ProjectionSettingsTab")).IsChecked = true;
            foreach (var language in new[] { "zh-CN", "zh-TW", "zh-HK", "en-US", "ja-JP" })
            {
                LocalizationService.SetLanguage(language);
                window.Width = 1100; window.UpdateLayout(); DrainDispatcher();
                var input = Visuals(window).OfType<TextBox>().First(t => t.IsVisible &&
                    System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "WirelessRaopPortTextBox");
                input.BringIntoView(); DrainDispatcher(); window.UpdateLayout();
                InteractionAssert(input.Text == port.ToString() && input.ActualWidth > 60,
                    "RAOP setting is missing or bound incorrectly.");
                SaveWindowRender(window, Path.Combine(output, $"raop-{language}.png"));
            }
            Console.WriteLine("PASS RAOP: legacy/default/invalid migration, inline validation, real occupied port and release, draft/backend isolation, save/load/clone and failure rollback, native API rejection, actual child arguments, five-language settings UI.");
            return 0;
        }
        finally { app.IsUiPreviewMode = true; CloseWorkspaceTestWindow(window); app.Shutdown(); }
    }
}
