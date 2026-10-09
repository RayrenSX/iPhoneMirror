using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestIndependentBluetoothPointer(MainWindow window, Window independent,
        DeviceViewModel device)
    {
        var vm = KeyboardField(window, "_viewModel");
        var previousForeground = KeyboardField(window, "_keyboardForegroundWindow");
        var main = new WindowInteropHelper(window).Handle;
        var handle = new WindowInteropHelper(independent).EnsureHandle();
        nint foreground = handle;
        SetKeyboardField(window, "_keyboardForegroundWindow", (Func<nint>)(() => foreground));
        try
        {
            KeyboardCall(window, "FocusControlDevice", device.Udid, handle);
            InteractionAssert((bool)KeyboardField(window, "_rawMouseInputEnabled"),
                "Independent Bluetooth control must keep relative Raw Input enabled.");
            InteractionAssert((nint)KeyboardField(window, "_activeControlWindow") == handle,
                "Registering mouse input must preserve the independent control window.");
            KeyboardCall(window, "ResetKeyboardOwnership");
            AwaitMapping((Task)KeyboardField(window, "_keyboardHandoff"));
            InteractionAssert((bool)KeyboardField(window, "_rawMouseInputEnabled"),
                "Keyboard ownership handoff must preserve independent Bluetooth Raw Input.");

            // A transport write can keep the handoff pending until after the
            // independent preview has registered its mouse route.
            var pendingSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sends = (HashSet<Task>)KeyboardField(window, "_keyboardSends");
            sends.Add(pendingSend.Task);
            try
            {
                KeyboardCall(window, "ResetKeyboardOwnership");
                var handoff = (Task)KeyboardField(window, "_keyboardHandoff");
                InteractionAssert(!handoff.IsCompleted,
                    "The delayed handoff must wait for the pending keyboard write.");
                InteractionAssert((bool)KeyboardField(window, "_rawMouseInputEnabled"),
                    "Independent mouse input must remain enabled while keyboard input drains.");
                pendingSend.SetResult();
                AwaitMapping(handoff);
                InteractionAssert((bool)KeyboardField(window, "_rawMouseInputEnabled") &&
                    (nint)KeyboardField(window, "_activeControlWindow") == handle,
                    "Delayed keyboard handoff must preserve the independent mouse route.");
            }
            finally
            {
                pendingSend.TrySetResult();
                sends.Remove(pendingSend.Task);
            }

            // The selected main-page device can differ from the independent
            // target. No GATT client is attached; inspect the production queue.
            SetKeyboardField(vm, "_selectedDevice", null);
            SetKeyboardField(window, "_controlPointerFlushInFlight", 1);
            var rawType = typeof(MainWindow).GetNestedType("RawInput", BindingFlags.NonPublic)!;
            var raw = Activator.CreateInstance(rawType)!;
            var mouse = KeyboardField(raw, "Mouse");
            SetKeyboardField(mouse, "LastX", 37);
            SetKeyboardField(mouse, "LastY", 19);
            SetKeyboardField(mouse, "ButtonFlags", (ushort)1);
            SetKeyboardField(raw, "Mouse", mouse);
            void Send(bool movement = true) => KeyboardCall(window, "ProcessRawMouseInput", raw, movement);
            void Clear()
            {
                KeyboardCall(window, "StopControlPointerTimer");
                foreach (var field in new[] { "_pendingControlDx", "_pendingControlDy", "_pendingControlWheel" })
                    SetKeyboardField(window, field, 0);
                SetKeyboardField(window, "_controlButtons", (byte)0);
                SetKeyboardField(window, "_pendingControlStateDirty", false);
            }
            Send();
            InteractionAssert((int)KeyboardField(window, "_pendingControlDx") != 0 ||
                (int)KeyboardField(window, "_pendingControlDy") != 0,
                "Independent relative movement must reach the mouse queue.");
            InteractionAssert((byte)KeyboardField(window, "_controlButtons") == 1,
                "Independent mouse press must use its own target, not the main selection.");
            Clear();
            Send(false);
            InteractionAssert((int)KeyboardField(window, "_pendingControlDx") == 0 &&
                (int)KeyboardField(window, "_pendingControlDy") == 0 &&
                (byte)KeyboardField(window, "_controlButtons") == 1,
                "Draining old movement must preserve button transitions.");
            SetKeyboardField(mouse, "ButtonFlags", (ushort)2);
            SetKeyboardField(raw, "Mouse", mouse);
            Send(false);
            InteractionAssert((byte)KeyboardField(window, "_controlButtons") == 0,
                "Independent mouse release must reach the active target.");
            KeyboardCall(window, "HandleRawWheel", (short)120);
            InteractionAssert((int)KeyboardField(window, "_pendingControlWheel") != 0,
                "Independent wheel input must use the active target.");

            Clear();
            KeyboardCall(window, "OnIndependentPointerInput", device.Udid,
                new PreviewPointerEventArgs(PreviewPointerKind.ButtonDown, 10, 10, 1, 0));
            InteractionAssert((byte)KeyboardField(window, "_controlButtons") == 0,
                "Legacy independent events must not duplicate Raw Input.");

            SetKeyboardField(mouse, "ButtonFlags", (ushort)1);
            SetKeyboardField(raw, "Mouse", mouse);
            foreach (var blocked in new[] { "foreground", "suspended", "transition", "unregistered" })
            {
                Clear();
                foreground = blocked == "foreground" ? main : handle;
                SetKeyboardField(window, "_keyboardFocusSuspended", blocked == "suspended");
                SetKeyboardField(window, "_bluetoothRouteChanging", blocked == "transition" ? 1 : 0);
                SetKeyboardField(window, "_rawMouseInputEnabled", blocked != "unregistered");
                Send();
                InteractionAssert((int)KeyboardField(window, "_pendingControlDx") == 0 &&
                    (int)KeyboardField(window, "_pendingControlDy") == 0 &&
                    (byte)KeyboardField(window, "_controlButtons") == 0,
                    $"Raw mouse input must be blocked for {blocked}.");
            }
            SetKeyboardField(window, "_rawMouseInputEnabled", true);
            SetKeyboardField(window, "_keyboardFocusSuspended", false);
            SetKeyboardField(window, "_bluetoothRouteChanging", 0);
            Send();
            InteractionAssert((byte)KeyboardField(window, "_controlButtons") == 1,
                "Independent mouse input must resume after focus returns.");
            Console.WriteLine("Bluetooth pointer: independent registration, motion, buttons, wheel, target isolation and focus guards passed.");
        }
        finally
        {
            SetKeyboardField(vm, "_selectedDevice", device);
            SetKeyboardField(window, "_bluetoothRouteChanging", 0);
            SetKeyboardField(window, "_rawMouseInputEnabled", true);
            KeyboardCall(window, "RegisterRawInput", false, false);
            KeyboardCall(window, "ResetControlRouteState");
            SetKeyboardField(window, "_controlPointerFlushInFlight", 0);
            SetKeyboardField(window, "_keyboardForegroundWindow", previousForeground);
            KeyboardCall(window, "SetWindowsCursorHidden", false);
        }
    }
}
