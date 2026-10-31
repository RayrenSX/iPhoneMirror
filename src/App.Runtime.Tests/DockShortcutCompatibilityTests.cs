using System.IO;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestDockShortcutCompatibility(MainWindow main, DeviceViewModel device,
        MemoryStream packets, Action<int, bool> key, Action finish)
    {
        var bindings = (Dictionary<BluetoothShortcutAction, KeyboardShortcut>)KeyboardField(main, "_bluetoothShortcuts");
        var previous = bindings[BluetoothShortcutAction.Dock];
        var product = device.ProductType;
        try
        {
            bindings[BluetoothShortcutAction.Dock] = new(0, 0x77);
            KeyboardCall(main, "ConfigureKeyboardShortcuts", bindings);
            foreach (var model in new[] { "iPhone15,2", "iPod9,1", "", "iPad14,3" })
            {
                SetKeyboardField(device, "_productType", model);
                packets.SetLength(0);
                key(0x77, true);
                key(0x77, false);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(400));
                finish();
                var frames = ReadShortcutPackets(packets);
                if (model.StartsWith("iPad", StringComparison.Ordinal))
                    MappingAssert(frames.Length == 4 && frames.All(f =>
                        f.GetProperty("kind").GetString() == "button_event") &&
                        frames[1].GetProperty("usageCode").GetInt32() == 4,
                        "iPad Dock lost its ordered Globe+A shortcut.");
                else
                    MappingAssert(frames.Length == 0,
                        $"{model}: unsupported Dock leaked keyboard input to the device.");
            }
        }
        finally
        {
            SetKeyboardField(device, "_productType", product);
            bindings[BluetoothShortcutAction.Dock] = previous;
            KeyboardCall(main, "ConfigureKeyboardShortcuts", bindings);
        }
        Console.WriteLine("PASS Dock compatibility: iPhone/iPod/unknown input isolated; iPad retains its ordered shortcut.");
    }
}
