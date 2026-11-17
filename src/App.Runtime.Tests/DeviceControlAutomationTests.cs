using System.Collections.Concurrent;
using System.IO;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestAutomationDeviceControlPreferences(string output)
    {
        var vm = new MainViewModel();
        var bindings = new DeviceBindingManager(Path.Combine(Path.GetFullPath(output), $"automation-bindings-{Guid.NewGuid():N}.json"));
        var device = DeviceViewModel.FromNative(new NativeDeviceInfo
            { Udid = "control-automation-fixture", Name = "Automation fixture", State = ConnectionState.Ready }, DeviceAppearance.Unknown);
        vm.Devices.Add(device);
        bindings.CreateProfileFromIdentity(device.Name, DeviceIdentityType.Wired, device.Udid, null);
        var resolver = new DeviceIdentityResolver(bindings);
        SetKeyboardField(vm, "_reverseBindings", bindings);
        SetKeyboardField(vm, "_identityResolver", resolver);
        var host = new UsbTouchBridgeHost();
        var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
        typeof(DirectUsbInputBridge).GetProperty("IsReady")!.SetValue(bridge, true);
        var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
        control.WiredBridge = host;
        control.WiredEnabled = control.WiredConnected = true;
        control.AppleUdid = device.Udid;
        control.Binding = resolver.ResolveControlBinding(device, ReverseControlMode.Usb);
        control.Router.Begin(device.Udid, ReverseControlMode.Usb);
        var preferences = (ConcurrentDictionary<string, DeviceControlPreferences>)KeyboardField(vm, "_appliedDeviceControlPreferences");
        var expected = vm.ControlPreferencesFor(device.Udid);
        var resolve = ((IAutomationBackend)vm).ResolveAsync(AutomationDeviceId.Encode(device.Udid), default);
        WaitReviewTask(resolve);
        var target = resolve.GetAwaiter().GetResult();
        InteractionAssert(target.IsCurrent(), "The synthetic automation route is not ready.");
        preferences[device.Udid] = expected with { MouseSensitivity = expected.MouseSensitivity == 456 ? 457 : 456 };
        InteractionAssert(target.IsCurrent(), "A sensitivity-only change invalidated touch coordinates.");
        preferences["another-device"] = expected with { ReverseHorizontal = !expected.ReverseHorizontal };
        InteractionAssert(target.IsCurrent(), "Another device's direction invalidated this automation route.");

        // Neither published configuration has the captured direction. A guard
        // that reads each property separately can nevertheless accept a hybrid.
        var portraitChanged = expected with { PortraitDirection = (expected.PortraitDirection + 1) % 4 };
        var landscapeChanged = expected with { LandscapeDirection = (expected.LandscapeDirection + 1) % 4 };
        preferences[device.Udid] = portraitChanged;
        using var ready = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var writer = Task.Factory.StartNew(() =>
        {
            ready.Set();
            while (!stop.IsCancellationRequested)
            {
                preferences[device.Udid] = landscapeChanged;
                Thread.Yield();
                preferences[device.Udid] = portraitChanged;
                Thread.Yield();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var acceptedHybrid = false;
        try
        {
            InteractionAssert(ready.Wait(TimeSpan.FromSeconds(5)), "Concurrent configuration writer did not start.");
            for (var i = 0; i < 200_000 && !acceptedHybrid; i++) acceptedHybrid = target.IsCurrent();
        }
        finally
        {
            stop.Cancel();
            writer.GetAwaiter().GetResult();
            control.WiredEnabled = control.WiredConnected = false;
            control.WiredBridge = null;
            WaitReviewTask(host.DisposeAsync().AsTask());
        }
        InteractionAssert(!acceptedHybrid, "An automation guard accepted mixed direction settings from different committed snapshots.");
        Console.WriteLine("PASS automation direction snapshots: coherent concurrent reads, sensitivity-only changes and device isolation.");
    }
}
