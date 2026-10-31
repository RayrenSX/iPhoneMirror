using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Uses the same real SendInput/hook/router as the fault probe, with no
    // transport replacement. USB capture can coexist with WirelessDirect.
    private static void RunLiveShortcutSoak(MainWindow main, MainViewModel vm, string output,
        bool wireless, int seconds, nint window)
    {
        MappingAssert(seconds is >= 10 and <= 7200, "Soak duration outside 10..7200 seconds.");
        var manager = (MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        if (File.Exists(Path.Combine(output, "soak-fullscreen"))) manager.ToggleFullScreenWindow(window);
        else
        {
            var windows = (System.Collections.IDictionary)KeyboardField(manager, "_windows");
            var preview = windows[vm.SelectedDevice!.Udid]!;
            if ((bool)KeyboardField(preview, "_isTopMost")) KeyboardCall(preview, "ToggleTopMost");
        }
        var events = Path.Combine(output, "events.jsonl");
        var summary = Path.Combine(output, "summary.json");
        var deadlineFile = Path.Combine(output, "stop-at.txt");
        var hookFaultFile = Path.Combine(output, "omit-hook-down-every.txt");
        var hookFaultEvery = File.Exists(hookFaultFile) ? int.Parse(File.ReadAllText(hookFaultFile)) : 0;
        MappingAssert(hookFaultEvery == 0 || hookFaultEvery >= 2, "Hook fault interval must be zero or at least two.");
        DateTimeOffset? deadline = File.Exists(deadlineFile)
            ? DateTimeOffset.Parse(File.ReadAllText(deadlineFile)) : null;
        MappingAssert(!File.Exists(events), "Use a fresh soak output directory.");
        var sequence = new[] { (0x71, "ControlCenter"), (0x70, "Home"), (0x72, "NotificationCenter"),
            (0x70, "Home"), (0x73, "AppSwitcher"), (0x70, "Home"), (0x74, "Dock"), (0x70, "Home") };
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        double active = 0;
        var count = 0; var anomalies = 0; var pauses = 0; var paused = false;
        var state = "running"; string? reason = null; string? previousAction = null; double previousDifference = 0;
        var beforePath = Path.Combine(output, "before.png");
        var afterPath = Path.Combine(output, "after.png");
        static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var appHash = Hash(Path.Combine(AppContext.BaseDirectory, "iPhoneMirror.dll"));
        var bridgeHash = Hash(Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe"));
        void Event(string kind, object data) => File.AppendAllText(events, JsonSerializer.Serialize(new
            { time = DateTimeOffset.Now.ToString("O"), kind, data }) + Environment.NewLine);
        void Save() => File.WriteAllText(summary, JsonSerializer.Serialize(new
        {
            result = state, reason, requestedSeconds = seconds, started = started.ToString("O"),
            updated = DateTimeOffset.Now.ToString("O"), appPid = Environment.ProcessId,
            elapsedSeconds = clock.Elapsed.TotalSeconds, activeSeconds = active, keypresses = count,
            anomalies, focusPauses = pauses, input = "Windows SendInput -> production MainWindow hook -> real iPhone",
            capture = "USB", control = wireless ? "WirelessDirect" : "WiredDirect",
            appPath = AppContext.BaseDirectory, appAssemblySha256 = appHash, bridgeSha256 = bridgeHash,
            hookDownFaultEvery = hookFaultEvery,
        }, new JsonSerializerOptions { WriteIndented = true }));
        bool Capture(string path)
        {
            vm.CaptureScreenshot(Path.GetFullPath(path));
            return File.Exists(path);
        }
        static double Difference(string first, string second)
        {
            static (byte[] Pixels, int Width, int Height) Read(string path)
            {
                using var stream = File.OpenRead(path);
                var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                return (pixels, bitmap.PixelWidth, bitmap.PixelHeight);
            }
            var a = Read(first); var b = Read(second);
            if (a.Width != b.Width || a.Height != b.Height) return 1;
            var changed = 0; var total = 0;
            for (var y = 1; y < 64; y++) for (var x = 1; x < 32; x++)
            {
                var offset = (y * a.Height / 64 * a.Width + x * a.Width / 32) * 4;
                if (Enumerable.Range(0, 3).Sum(c => Math.Abs(a.Pixels[offset + c] - b.Pixels[offset + c])) > 60) changed++;
                total++;
            }
            return (double)changed / total;
        }
        try
        {
            // Previous probes can leave a system panel above App Switcher.
            // Establish a real Home baseline before judging the first action.
            for (var baseline = 0; baseline < 2; baseline++)
            {
                MappingAssert(MappingGetForeground() == window, "Soak baseline preview is not foreground.");
                AwaitMapping(Task.Run(() =>
                {
                    MappingAssert(ShortcutLiveSend(1, [new() { Type = 1, Key = 0x70 }], 40) == 1,
                        "Soak baseline key-down failed.");
                    try { Thread.Sleep(70); }
                    finally
                    {
                        MappingAssert(ShortcutLiveSend(1, [new() { Type = 1, Key = 0x70, Flags = 2 }], 40) == 1,
                            "Soak baseline key-up failed.");
                    }
                }));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(600));
            }
            AdvanceDispatcher(TimeSpan.FromSeconds(1));
            MappingAssert(Capture(beforePath), "Soak initial frame unavailable.");
            File.Copy(beforePath, Path.Combine(output, "baseline-home.png"), true);
            Event("soak_started", new { durationSeconds = seconds, wireless, preview = (long)window, hookDownFaultEvery = hookFaultEvery }); Save();
            while (active < seconds)
            {
                if (deadline is { } stopAt && DateTimeOffset.Now >= stopAt)
                { state = "stopped_at_deadline"; reason = "User requested end time reached."; break; }
                if (File.Exists(Path.Combine(output, "stop"))) { state = "stopped"; break; }
                if (manager.GetWindowHandle(vm.SelectedDevice!.Udid) != window)
                { state = "stopped"; reason = "The test preview window was closed or replaced."; break; }
                if (MappingGetForeground() != window)
                {
                    if (!paused) { pauses++; paused = true; Event("foreground_lost", new { elapsed = clock.Elapsed.TotalSeconds }); }
                    Save(); AdvanceDispatcher(TimeSpan.FromSeconds(1)); continue;
                }
                if (paused) { MappingAssert(Capture(beforePath), "Soak resume frame unavailable."); paused = false; }
                var iteration = Stopwatch.StartNew();
                var (key, action) = sequence[count % sequence.Length];
                var omitHookDown = hookFaultEvery != 0 && (count + 1) % hookFaultEvery == 0;
                if (omitHookDown) _dropShortcutHookDownKey = key;
                var logOffset = new FileInfo(DiagnosticLogger.Path).Length;
                DateTimeOffset downAt = default, upAt = default;
                var foregroundBefore = MappingGetForeground();
                var input = Task.Run(() =>
                {
                    downAt = DateTimeOffset.Now;
                    MappingAssert(ShortcutLiveSend(1, [new() { Type = 1, Key = (ushort)key }], 40) == 1, "Soak SendInput down failed.");
                    try { Thread.Sleep(70); }
                    finally
                    {
                        MappingAssert(ShortcutLiveSend(1, [new() { Type = 1, Key = (ushort)key, Flags = 2 }], 40) == 1, "Soak SendInput up failed.");
                        upAt = DateTimeOffset.Now;
                    }
                });
                AwaitMapping(input);
                MappingAssert(!omitHookDown || _dropShortcutHookDownKey == 0,
                    "The requested hook-down omission was not consumed by the live hook.");
                var foregroundAfter = MappingGetForeground(); count++;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(1100));
                MappingAssert(Capture(afterPath), "Soak result frame unavailable.");
                var difference = Difference(beforePath, afterPath);
                var initialDifference = difference; double? lateDifference = null;
                var dismissal = action == "Home" && previousAction is "ControlCenter" or "NotificationCenter" or "AppSwitcher" && previousDifference >= .01;
                var expectsChange = action is "ControlCenter" or "NotificationCenter" or "AppSwitcher" || dismissal;
                if (expectsChange && difference < .01)
                {
                    File.Copy(afterPath, Path.Combine(output, $"frame-{count}-early.png"), true);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(700)); Capture(afterPath);
                    lateDifference = Difference(beforePath, afterPath); difference = Math.Max(difference, lateDifference.Value);
                }
                using var log = new FileStream(DiagnosticLogger.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                log.Seek(logOffset, SeekOrigin.Begin); using var reader = new StreamReader(log);
                var sent = reader.ReadToEnd().Contains($"system_shortcut_sent action={action} ");
                var windowsKeyDownAfterUp = ShortcutLiveKeyState(key) < 0;
                var anomaly = !sent || (expectsChange && difference < .01) || (omitHookDown && windowsKeyDownAfterUp);
                if (anomaly) anomalies++;
                Event("keypress", new { index = count, key, action, sent, imageDifference = difference, anomaly,
                    hookDownOmitted = omitHookDown,
                    windowsKeyDownAfterUp,
                    elapsed = clock.Elapsed.TotalSeconds, downAt = downAt.ToString("O"), upAt = upAt.ToString("O"),
                    foregroundBefore = (long)foregroundBefore, foregroundAfter = (long)foregroundAfter,
                    initialImageDifference = initialDifference, lateImageDifference = lateDifference, expectedDismissal = dismissal });
                if (anomaly || count % 40 == 1)
                {
                    File.Copy(beforePath, Path.Combine(output, $"frame-{count}-before.png"), true);
                    File.Copy(afterPath, Path.Combine(output, $"frame-{count}-after.png"), true);
                }
                File.Copy(afterPath, beforePath, true); previousAction = action; previousDifference = difference;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(800)); active += iteration.Elapsed.TotalSeconds; Save();
            }
            if (state == "running") state = anomalies == 0 ? "completed" : "completed_with_anomalies";
        }
        catch (Exception error) { state = "failed"; reason = error.Message; throw; }
        finally { _dropShortcutHookDownKey = 0; Save(); Event("finished", new { result = state, reason, keypresses = count, anomalies }); }
    }
}
