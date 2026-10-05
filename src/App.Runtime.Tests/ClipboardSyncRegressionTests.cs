using System.Runtime.InteropServices;
using System.Windows;
using IPhoneMirror.App.Localization;
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
        TestUsbPasteKeyboardState();
        WaitReviewTask(Application.Current.Dispatcher.InvokeAsync(TestClipboardReadRetries).Task);
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

                // Exercise JSON parsing and both bridge event routes. The local
                // copy happens while PULL is running, before its reply arrives.
                void Protocol(string json) => KeyboardCall(bridge, "OnBridgeEvent", Parse(json));
                BridgeEvent Parse(string json)
                {
                    BridgeEvent? parsed = null;
                    var reader = new DirectUsbInputBridge();
                    reader.OnEvent += e => parsed = e;
                    KeyboardCall(reader, "HandleLine", json);
                    return parsed ?? throw new InvalidOperationException("Clipboard protocol event was lost.");
                }
                before = writes.Count;
                Task.Run(() => Protocol("{\"event\":\"clipboard_read_started\",\"readId\":1}")).GetAwaiter().GetResult();
                sequence++;
                Task.Run(() => Protocol("{\"event\":\"clipboard_text\",\"readId\":1,\"text\":\"slow old reply\"}")).GetAwaiter().GetResult();
                Protocol("{\"event\":\"clipboard_read_finished\",\"readId\":1}");
                Flush();
                InteractionAssert(writes.Count == before, "A slow device read overwrote a newer Windows copy.");
                Protocol("{\"event\":\"clipboard_read_started\",\"readId\":2}");
                Protocol("{\"event\":\"clipboard_text\",\"readId\":2,\"text\":\"new device copy\"}");
                Protocol("{\"event\":\"clipboard_read_finished\",\"readId\":2}");
                Flush();
                InteractionAssert(writes.Count == before + 1 && writes.Last() == "new device copy",
                    "A fresh read failed after discarding a slow stale reply.");
                Protocol("{\"event\":\"clipboard_read_started\",\"readId\":3}");
                Protocol("{\"event\":\"clipboard_read_finished\",\"readId\":3}");
                Protocol("{\"event\":\"clipboard_text\",\"readId\":3,\"text\":\"late cancelled read\"}");
                Flush();
                InteractionAssert(writes.Count == before + 1, "A completed read accepted a late reply.");
                Protocol("{\"event\":\"clipboard_read_started\",\"readId\":4}");
                sync.ForgetReads(bridge);
                Protocol("{\"event\":\"clipboard_text\",\"readId\":4,\"text\":\"reply after disconnect\"}");
                Flush();
                InteractionAssert(writes.Count == before + 1, "A disconnected read accepted a late reply.");

                var previousStatus = control.Status;
                KeyboardCall(bridge, "Raise", "warning", "clipboard_poll_failed", "simulated", null);
                DrainDispatcher();
                InteractionAssert(control.Status == previousStatus,
                    "A background clipboard poll warning changed the control status.");
                KeyboardCall(bridge, "Raise", "warning", "paste_failed", "simulated", null);
                DrainDispatcher();
                var pasteFailure = LocalizationService.Get("ClipboardPasteFailed");
                InteractionAssert(control.Status == pasteFailure && vm.LogText.Contains(pasteFailure),
                    "An asynchronous device paste failure was hidden from the user.");
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

    private static void TestClipboardReadRetries()
    {
        var attempts = 0;
        var read = ClipboardTextReader.ReadAsync(() =>
        {
            InteractionAssert(Application.Current.Dispatcher.CheckAccess() &&
                Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
                "Clipboard reads must retry on STA.");
            if (++attempts < 3) throw new ExternalException("busy");
            return "中文 / café / 😀";
        }, () => true, () => Task.Delay(1));
        WaitReviewTask(read);
        InteractionAssert(attempts == 3 && read.Result == "中文 / café / 😀", "Busy clipboard lost Ctrl+V.");

        var current = true;
        attempts = 0;
        read = ClipboardTextReader.ReadAsync(() =>
        {
            attempts++;
            throw new ExternalException("busy");
        }, () => current, () => { current = false; return Task.CompletedTask; });
        WaitReviewTask(read);
        InteractionAssert(attempts == 1 && read.Result is null, "A paste survived a keyboard ownership change.");

        attempts = 0;
        read = ClipboardTextReader.ReadAsync(() =>
        {
            attempts++;
            throw new ExternalException("busy");
        }, () => true, () => Task.CompletedTask);
        InteractionAssert(read.IsFaulted && attempts == 5, "Clipboard read retries must be bounded.");
        _ = read.Exception;
    }
}
