using System.IO;
using System.Text.Json;
using System.Windows;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunWirelessHidRecoveryTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            foreach (var scenario in new[] { "direct", "mediastream", "stopping", "disabled", "stale", "not-ready", "rebound", "wrong-device" })
            {
                var vm = new MainViewModel();
                var bindings = new DeviceBindingManager(Path.Combine(Path.GetFullPath(output), $"{scenario}-{Guid.NewGuid():N}.json"));
                var resolver = new DeviceIdentityResolver(bindings);
                SetKeyboardField(vm, "_reverseBindings", bindings);
                SetKeyboardField(vm, "_identityResolver", resolver);
                var id = "wireless-recovery-phone";
                var device = DeviceViewModel.FromNative(new NativeDeviceInfo
                { Udid = "airplay://wireless-recovery-phone", Name = "Recovery phone", State = ConnectionState.Ready });
                var profile = bindings.CreateProfileFromIdentity(device.Name, DeviceIdentityType.Wired, id, null).Profile!;
                bindings.Bind(profile.Id, DeviceIdentityType.AirPlay, device.Udid, device.Name, null, true);
                vm.Devices.Add(device);
                var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
                var host = new UsbTouchBridgeHost();
                var transport = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
                SetKeyboardField(host, "_started", 1);
                SetKeyboardField(transport, "_requestedUdid", id);
                SetKeyboardField(transport, "_requestedWireless", true);
                transport.OnEvent += e => KeyboardCall(host, "OnBridgeEvent", e);
                control.AppleUdid = id;
                control.Binding = resolver.ResolveControlBinding(device, ReverseControlMode.Wireless);
                control.WirelessBridge = host;
                control.WirelessEnabled = control.WirelessConnected = true;
                control.WirelessTarget = device.Udid;
                control.Router.Begin(id, ReverseControlMode.Wireless);
                KeyboardCall(vm, "AttachWirelessBridgeEvents", control, host, device);
                void EmitReady() => KeyboardCall(transport, "HandleLine", JsonSerializer.Serialize(new
                {
                    @event = "ready", protocol = CoreDeviceTouchProtocol.ProtocolVersion, udid = id,
                    transport = "wireless", generation = 2, gateOpen = true,
                    authMode = scenario == "mediastream" ? "mediastream" : "direct"
                }));
                try
                {
                    KeyboardCall(transport, "HandleLine", "{\"event\":\"status\",\"code\":\"recovery_triggered\"}");
                    DrainDispatcher();
                    InteractionAssert(!control.InputEnabled && control.Router.Mode == ReverseControlMode.None,
                        "Recovery did not suspend wireless input.");
                    if (scenario == "stopping") control.Stopping = true;
                    if (scenario == "disabled") control.WirelessEnabled = false;
                    if (scenario == "stale") control.WirelessBridge = null;
                    if (scenario == "rebound") bindings.Bind(profile.Id, DeviceIdentityType.Wired, "other-phone", "Other", null, true);
                    if (scenario == "wrong-device") control.AppleUdid = "other-phone";
                    if (scenario == "not-ready") KeyboardCall(host, "Raise", "ready", null, null, null);
                    else EmitReady();
                    DrainDispatcher();
                    var recovered = scenario is "direct" or "mediastream";
                    InteractionAssert(control.InputEnabled == recovered &&
                        control.Router.Owns(id, ReverseControlMode.Wireless) == recovered,
                        $"Wireless recovery admission failed for {scenario}.");
                    if (recovered)
                    {
                        InteractionAssert(ReferenceEquals(control.WirelessBridge, host) &&
                            control.ControlStatus.Current?.Stage == ControlStage.Ready &&
                            control.Status == LocalizationService.Get(scenario == "direct"
                                ? "ReverseControlWirelessEnabledDirect" : "ReverseControlWirelessEnabled"),
                            "Inline recovery replaced its owner or retained the disconnected status.");
                        var generation = control.Router.Generation;
                        EmitReady();
                        DrainDispatcher();
                        InteractionAssert(control.Router.Generation == generation, "Duplicate ready restarted the input router.");
                    }
                    Console.WriteLine($"PASS wireless HID recovery: {scenario}");
                }
                finally
                {
                    control.Stopping = false;
                    control.WirelessEnabled = control.WirelessConnected = false;
                    control.WirelessBridge = null;
                    SetKeyboardField(host, "_started", 0);
                    WaitReviewTask(vm.ShutdownAsync());
                }
            }
            return 0;
        }
        finally { app.Shutdown(); }
    }
}
