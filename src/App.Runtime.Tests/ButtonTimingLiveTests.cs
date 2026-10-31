using System.Diagnostics;
using System.IO;
using System.Text.Json;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void RunLiveButtonTiming(MainWindow main, MainViewModel vm, string output, nint window)
    {
        var target = vm.SelectedDevice!.Udid;
        var configuration = Path.Combine(output, "button-timing-patterns.json");
        var patterns = File.Exists(configuration)
            ? JsonSerializer.Deserialize<int[][]>(File.ReadAllText(configuration))!.Select(p => (p[0], p[1])).ToArray()
            : new[] { (50, 100), (80, 100), (100, 150), (100, 250), (50, 150), (80, 200) };
        MappingAssert(patterns.Length > 0 && patterns.All(p => p.Item1 is >= 20 and <= 500 && p.Item2 is >= 20 and <= 500),
            "Button timing pattern outside safe bounds.");
        var atomic = File.Exists(Path.Combine(output, "atomic-home"));
        var globe = File.Exists(Path.Combine(output, "globe-chord"));
        var indigoLetter = File.Exists(Path.Combine(output, "indigo-letter"));
        var events = Path.Combine(output, "button-timing.jsonl");
        MappingAssert(!File.Exists(events), "Use fresh button timing output.");
        bool Wait(Func<bool> test)
        {
            var clock = Stopwatch.StartNew();
            while (!test() && clock.Elapsed.TotalSeconds < 45) AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            return test();
        }
        foreach (var trial in Enumerable.Range(0, 60))
        {
            if (File.Exists(Path.Combine(output, "stop"))) break;
            MappingAssert(MappingGetForeground() == window, "Button timing preview lost foreground.");
            MappingAssert(Wait(() => vm.CaptureDirectKeyboardRoute(target)?.IsCurrent() == true), "Button timing route unavailable.");
            var route = vm.CaptureDirectKeyboardRoute(target)!;
            var guard = (Func<bool>)KeyboardCall(main, "CapturePointerSendGuard", window)!;
            var (hold, gap) = patterns[trial % patterns.Length];
            var stem = Path.Combine(output, $"button-{trial + 1:D2}-{hold}-{gap}");
            var home = (CoreDeviceTouchProtocol.IndigoConsumerUsagePage, CoreDeviceTouchProtocol.IndigoHome, 50);
            for (var baseline = 0; baseline < 2; baseline++)
            {
                AwaitMapping((Task)KeyboardCall(main, "SendUsbButtonPulseAsync", home, route, guard)!);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(300));
            }
            AdvanceDispatcher(TimeSpan.FromSeconds(1.3));
            vm.CaptureScreenshot(stem + "-before.png");
            var start = DateTimeOffset.Now;
            async Task Send()
            {
                var gate = (SemaphoreSlim)KeyboardCall(main, "KeyboardSessionGate", route)!;
                await gate.WaitAsync();
                try
                {
                    if (globe)
                    {
                        try
                        {
                            await route.SendButtonAsync!(home.Item1, CoreDeviceTouchProtocol.GlobeKeyboardLayoutUsage, "down", guard);
                            await Task.Delay(hold);
                            if (indigoLetter) await route.SendButtonAsync!(7, 0x11, "down", guard);
                            else await route.SendAsync(0, [0x11], guard);
                            await Task.Delay(100);
                        }
                        finally
                        {
                            try
                            {
                                if (indigoLetter) await route.SendButtonAsync!(7, 0x11, "up", null);
                                else await route.SendAsync(0, [], null);
                            }
                            finally
                            {
                                await Task.Delay(gap);
                                await route.SendButtonAsync!(home.Item1, CoreDeviceTouchProtocol.GlobeKeyboardLayoutUsage, "up", null);
                            }
                        }
                        return;
                    }
                    if (atomic)
                    {
                        await route.SendButtonAsync!(home.Item1, home.Item2, "double_press", guard);
                        return;
                    }
                    for (var press = 0; press < 2; press++)
                    {
                        try
                        {
                            await route.SendButtonAsync!(home.Item1, home.Item2, "down", guard);
                            await Task.Delay(hold);
                        }
                        finally { await route.SendButtonAsync!(home.Item1, home.Item2, "up", null); }
                        if (press == 0) await Task.Delay(gap);
                    }
                }
                finally { gate.Release(); }
            }
            AwaitMapping(Send());
            AdvanceDispatcher(TimeSpan.FromSeconds(1.8));
            vm.CaptureScreenshot(stem + "-after.png");
            AdvanceDispatcher(TimeSpan.FromSeconds(.7));
            vm.CaptureScreenshot(stem + "-late.png");
            var valid = route.IsCurrent() && guard();
            File.AppendAllText(events, JsonSerializer.Serialize(new { trial = trial + 1, hold, gap, atomic, globe, indigoLetter, start, valid }) + Environment.NewLine);
            Console.WriteLine($"BUTTON TIMING trial={trial + 1} hold_ms={hold} gap_ms={gap} valid={valid}");
        }
    }
}
