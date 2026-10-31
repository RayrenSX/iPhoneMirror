using System.Diagnostics;
using System.IO;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestUsbSystemShortcutSequence(MainWindow main)
    {
        foreach (var usage in new byte[] { 0x06, 0x11, 0x04 })
        foreach (var fault in new[] { "none", "globe_down", "letter_down", "letter_up", "globe_up", "focus_lost" })
        {
            var events = new List<(string Name, long Time)>();
            var allowed = true;
            Task Record(string name, Func<bool>? guard)
            {
                events.Add((name, Stopwatch.GetTimestamp()));
                MappingAssert(name.EndsWith("up") ? guard is null : guard is not null,
                    "Shortcut release inherited a focus guard or press lost its guard.");
                if (fault == "focus_lost" && name == "globe_down") allowed = false;
                return fault == name ? Task.FromException(new IOException(name)) : Task.CompletedTask;
            }
            var route = new DirectKeyboardRoute("sequence-fixture", "WiredDirect", new object(), 1, () => true,
                (_, _, _) => throw new InvalidOperationException("A system shortcut crossed into another HID channel."),
                (page, code, state, guard) =>
                {
                    var globe = page == CoreDeviceTouchProtocol.IndigoConsumerUsagePage &&
                        code == CoreDeviceTouchProtocol.GlobeKeyboardLayoutUsage;
                    MappingAssert(globe || page == 7 && code == usage, "Wrong system shortcut usage.");
                    return Record((globe ? "globe_" : "letter_") + state, guard);
                });
            var task = (Task)KeyboardCall(main, "SendUsbSystemShortcutAsync", usage, route, (Func<bool>)(() => allowed))!;
            try { AwaitMapping(task); }
            catch (IOException) when (fault is "globe_down" or "letter_down" or "letter_up" or "globe_up") { }
            MappingAssert(task.IsFaulted == (fault is "globe_down" or "letter_down" or "letter_up" or "globe_up"),
                "Shortcut sequence swallowed or invented a transport failure.");
            var expected = fault is "globe_down" or "focus_lost"
                ? new[] { "globe_down", "letter_up", "globe_up" }
                : new[] { "globe_down", "letter_down", "letter_up", "globe_up" };
            MappingAssert(events.Select(e => e.Name).SequenceEqual(expected),
                $"{fault}: shortcut release order or cleanup failed.");
            if (fault == "none")
            {
                MappingAssert(Stopwatch.GetElapsedTime(events[0].Time, events[1].Time).TotalMilliseconds >= 50 &&
                    Stopwatch.GetElapsedTime(events[1].Time, events[2].Time).TotalMilliseconds >= 65,
                    "Shortcut reports lost the modifier lead or letter hold.");
            }
        }
        foreach (var failDown in new[] { false, true })
        {
            var events = new List<(string State, long Time)>();
            var route = new DirectKeyboardRoute("home-timing-fixture", "WiredDirect", new object(), 1, () => true,
                (_, _, _) => Task.CompletedTask,
                (page, code, state, guard) =>
                {
                    MappingAssert(page == CoreDeviceTouchProtocol.IndigoConsumerUsagePage &&
                        code == CoreDeviceTouchProtocol.IndigoHome, "Wrong Home usage.");
                    MappingAssert(state == "up" ? guard is null : guard is not null, "Home release inherited the press guard.");
                    events.Add((state, Stopwatch.GetTimestamp()));
                    return failDown && state == "down" ? Task.FromException(new IOException("down")) : Task.CompletedTask;
                });
            var home = (CoreDeviceTouchProtocol.IndigoConsumerUsagePage, CoreDeviceTouchProtocol.IndigoHome, 50);
            var task = (Task)KeyboardCall(main, "SendUsbButtonPulseAsync", home, route, (Func<bool>)(() => true))!;
            try { AwaitMapping(task); }
            catch (IOException) when (failDown) { }
            MappingAssert(events.Select(e => e.State).SequenceEqual(new[] { "down", "up" }), "Home failed press was not released.");
            if (!failDown) MappingAssert(Stopwatch.GetElapsedTime(events[0].Time, events[1].Time).TotalMilliseconds >= 45,
                "Home press was too short for the device.");
        }
        Console.WriteLine("PASS system shortcut sequence: Globe lead/letter hold, ordered releases, focus cancellation, cleanup after press/release failures.");
    }
}
