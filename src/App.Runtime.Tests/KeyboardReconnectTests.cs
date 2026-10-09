using System.IO;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestIndependentKeyboardReconnect(MainWindow main, DeviceControlSession control,
        MemoryStream packets, nint mainWindow, Action<nint> setForeground, Action<int, bool> key, Action finish)
    {
        var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
        try
        {
            foreach (var mode in new[] { ReverseControlMode.Usb, ReverseControlMode.Wireless })
            {
                control.WiredEnabled = mode == ReverseControlMode.Usb;
                control.WirelessEnabled = mode == ReverseControlMode.Wireless;
                control.WiredConnected = control.WirelessConnected = true;
                control.Router.Begin(control.DeviceUdid, mode);
                // Transport setup in this fixture bypasses the production
                // state notifications that invalidate the previous route.
                KeyboardCall(main, "ResetKeyboardOwnership");
                AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
                ReleaseTestPhysicalKeys(main);
                setForeground(mainWindow);
                KeyboardCall(main, "FocusControlDevice", control.DeviceUdid, (nint)0);
                AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
                packets.SetLength(0);
                key(0x41, true); key(0x41, false); finish();
                MappingAssert(MappingFrames(packets).Count == 2 &&
                    MappingFrames(packets)[0].GetProperty("usages").GetArrayLength() == 1 &&
                    MappingFrames(packets)[1].GetProperty("usages").GetArrayLength() == 0,
                    "Reconnect fixture baseline typing failed.");

                setForeground((nint)234);
                KeyboardCall(main, "FocusControlDevice", control.DeviceUdid, (nint)234);
                // Recovery invalidates the old sender without activating main.
                control.WiredConnected = control.WirelessConnected = false;
                control.Router.Stop();
                KeyboardCall(main, "ApplyBluetoothControlInputState", false);
                AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
                MappingAssert(router.Mode == KeyboardInputMode.None, "Disconnected input remained active.");
                control.WiredConnected = control.WirelessConnected = true;
                control.Router.Begin(control.DeviceUdid, mode);
                KeyboardCall(main, "ApplyBluetoothControlInputState", false);
                AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
                MappingAssert(router.Mode == KeyboardInputMode.Direct, $"{mode}: independent reconnect left keyboard owner None.");
                MappingAssert(KeyboardCall(main, "CanForwardControlKeyboard", control.DeviceUdid, (nint)234) is true,
                    "Independent keyboard focus guard rejected the recovered route.");
                var vm = (IPhoneMirror.App.ViewModels.MainViewModel)KeyboardField(main, "_viewModel");
                MappingAssert(vm.CaptureDirectKeyboardRoute(control.DeviceUdid) is not null,
                    "Independent keyboard transport was not ready.");
                packets.SetLength(0);
                key(0x41, true); key(0x41, false); finish();
                MappingAssert(MappingFrames(packets).Count == 2 &&
                    MappingFrames(packets).All(f => f.TryGetProperty("usages", out _)),
                    $"{mode}: typing did not resume once after independent reconnect (owner={router.Mode}, retired={router.HasRetiredKeys}, foreground={KeyboardField(main, "_lastKeyboardEventForeground")}, active={KeyboardField(main, "_activeControlWindow")}): " + string.Join("; ", MappingFrames(packets)));
                packets.SetLength(0);
                key(0xA2, true); key(0x53, true); key(0x53, false); key(0xA2, false); finish();
                MappingAssert(MappingFrames(packets).Count == 2 &&
                    MappingFrames(packets).All(f => f.TryGetProperty("state", out _)),
                    $"{mode}: device shortcut did not resume once after independent reconnect.");
                packets.SetLength(0);
                foreach (var kind in new[] { PreviewPointerKind.ButtonDown, PreviewPointerKind.ButtonUp })
                    AwaitMapping((Task)KeyboardCall(main, "HandleUsbPointerInputAsync",
                        new PreviewPointerEventArgs(kind, 100, 100, 1, 0, 390, 844), control.DeviceUdid)!);
                MappingAssert(MappingFrames(packets).Count == 2, $"{mode}: reconnect broke mouse control.");

                // Updating a ready independent route must not take over Mapping.
                KeyboardCall(main, "TryEnterKeyboardMappingInputMode");
                KeyboardCall(main, "ApplyBluetoothControlInputState", false);
                AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
                MappingAssert(router.Mode == KeyboardInputMode.Mapping, "Independent route stole the mapping owner.");
                KeyboardCall(main, "LeaveKeyboardMappingInputMode");
                AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
                ReleaseTestPhysicalKeys(main);
                Console.WriteLine($"PASS {mode}: independent reconnect restores typing and device shortcuts, mouse remains usable, Mapping owner preserved.");
            }
        }
        finally
        {
            setForeground(mainWindow);
            KeyboardCall(main, "FocusControlDevice", control.DeviceUdid, (nint)0);
            AwaitMapping((Task)KeyboardField(main, "_keyboardHandoff"));
            ReleaseTestPhysicalKeys(main);
        }
    }
}
