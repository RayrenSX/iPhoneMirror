using System.Runtime.InteropServices;
using System.Windows;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunClipboardSyncRegressionTests()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", KeyboardTestMembers)!.SetValue(app, true);
        app.InitializeComponent();
        try { TestClipboardSyncRegressions(); }
        finally { app.Shutdown(); }
        return 0;
    }

    private static void TestClipboardSyncRegressions()
    {
        var vm = new MainViewModel();
        var writes = new List<string>();
        var attempts = 0;
        uint sequence = 1;
        var sync = new ClipboardSyncState(text =>
        {
            InteractionAssert(Application.Current.Dispatcher.CheckAccess() &&
                Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
                "Clipboard retries must run on the WPF STA dispatcher.");
            if (++attempts == 1) throw new ExternalException("simulated busy clipboard");
            writes.Add(text);
            sequence++;
        }, () => sequence, (_, _) => true,
            (_, _, error) => InteractionAssert(error is null, "Clipboard write unexpectedly failed."));
        SetKeyboardField(vm, "_clipboardSyncState", sync);
        var constructor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        DeviceViewModel Device(string udid) => (DeviceViewModel)constructor.Invoke([
            udid, "Clipboard test", "iPhone15,2", "18.0", "USB", "",
            Enum.Parse(constructor.GetParameters()[6].ParameterType, "Ready")]);
        var wired = new UsbTouchBridgeHost();
        var wireless = new UsbTouchBridgeHost();
        var first = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", "clipboard-A")!;
        var second = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", "clipboard-B")!;
        first.WiredBridge = wired;
        second.WirelessBridge = wireless;
        KeyboardCall(vm, "AttachUsbBridgeEvents", first, wired, Device(first.DeviceUdid), CancellationToken.None);
        KeyboardCall(vm, "AttachWirelessBridgeEvents", second, wireless, Device(second.DeviceUdid));
        void Emit(UsbTouchBridgeHost source, string text) =>
            KeyboardCall(source, "Raise", "clipboard_text", null, null, text);
        void Flush()
        {
            DrainDispatcher();
            WaitReviewTask(sync.FlushAsync());
        }
        try
        {
            vm.SetControlInputDevice(first.DeviceUdid);
            vm.SetControlInputDevice(null);
            Emit(wireless, "wireless cached");
            Emit(wired, "wired background");
            Flush();
            InteractionAssert(writes.SequenceEqual(["wired background"]) && attempts == 2,
                "Wired clipboard must sync after focus loss and retry without another bridge event.");
            Flush();
            InteractionAssert(writes.Count == 1, "An unselected device overwrote the clipboard.");
            vm.SetControlInputDevice(second.DeviceUdid);
            Flush();
            InteractionAssert(writes.Last() == "wireless cached", "Switching devices lost its cached clipboard.");

            foreach (var (control, bridge) in new[] { (first, wired), (second, wireless) })
            {
                vm.SetControlInputDevice(control.DeviceUdid);
                Flush();
                var before = writes.Count;
                // Raise from the transport reader while the STA dispatcher is
                // occupied, then simulate a new copy in another Windows app.
                Task.Run(() => Emit(bridge, "old device event")).GetAwaiter().GetResult();
                sequence++;
                Flush();
                InteractionAssert(writes.Count == before,
                    "An event queued before a new Windows copy overwrote it.");
                Task.Run(() => Emit(bridge, "fresh device event")).GetAwaiter().GetResult();
                Flush();
                InteractionAssert(writes.Count == before + 1 && writes.Last() == "fresh device event",
                    "A fresh device copy stopped syncing after discarding an older queued event.");
            }
            var accepted = writes.Count;
            Emit(wireless, "old bridge queued");
            second.WirelessBridge = new UsbTouchBridgeHost();
            Flush();
            InteractionAssert(writes.Count == accepted, "An event queued by a replaced bridge was accepted.");
            SetKeyboardField(vm, "_disposed", true);
            vm.HandleClipboardTextFromDevice(first.DeviceUdid, wired, "after shutdown", sync.CaptureSequence());
            Flush();
            InteractionAssert(writes.Count == accepted, "A disposed view model accepted clipboard content.");
            Console.WriteLine("Clipboard runtime: USB/wireless arrival races, background selection, STA retries and stale bridge filtering passed.");
        }
        finally
        {
            sync.Stop();
            first.WiredBridge = null;
            second.WirelessBridge = null;
            SetKeyboardField(vm, "_disposed", false);
            WaitReviewTask(vm.ShutdownAsync());
        }
    }
}
