using System.Diagnostics;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Explicit opt-in only. Exercises the actual host and packaged child against
    // the selected phone without typing or invoking application actions.
    private static int RunDeviceControlLive(string bridgePath, string udid, string mode, int holdSeconds = 0)
    {
        RunDeviceControlLiveAsync(bridgePath, udid, mode, holdSeconds).GetAwaiter().GetResult();
        return 0;
    }

    private static async Task RunDeviceControlLiveAsync(string bridgePath, string udid, string mode, int holdSeconds = 0)
    {
        if (holdSeconds is < 0 or > 180) throw new ArgumentOutOfRangeException(nameof(holdSeconds));
        var transport = mode switch
        {
            "usb" => UsbTouchTransport.Usb,
            "wireless" => UsbTouchTransport.Wireless,
            _ => throw new ArgumentException("Expected usb or wireless.", nameof(mode))
        };
        await using var host = new UsbTouchBridgeHost();
        TaskCompletionSource<bool>? acknowledgement = null;
        string? terminal = null;
        host.StatusChanged += (_, e) =>
        {
            if (e.Code == "input_verified") acknowledgement?.TrySetResult(true);
            if (e.EventName == "error") terminal = e.Code;
            if (e.EventName is "ready" or "error" || e.Code is "input_verified" or "recovery_triggered")
                Console.WriteLine($"{mode}: {e.EventName} {e.Code}");
        };
        long previousGeneration = -1;
        for (var pass = 1; pass <= (holdSeconds == 0 ? 2 : 1); pass++)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(365));
            var watch = Stopwatch.StartNew();
            terminal = null;
            await host.StartAsync(transport, udid, bridgePath, limit.Token);
            if (!host.IsReady || !host.GateOpen || host.InputGeneration <= previousGeneration)
                throw new InvalidOperationException("Device host did not validate a new input session.");
            previousGeneration = host.InputGeneration;
            Console.WriteLine($"{mode}: pass={pass} ready_seconds={watch.Elapsed.TotalSeconds:F2} auth={host.AuthMode}");
            var liveWatch = Stopwatch.StartNew();
            var reports = 0;
            while (reports < 3 || liveWatch.Elapsed.TotalSeconds < holdSeconds)
            {
                acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await host.SendKeyboardAsync(Array.Empty<byte>(), limit.Token, releaseAll: true);
                await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(10), limit.Token);
                if (!host.IsReady || terminal is not null)
                    throw new InvalidOperationException($"Device input failed: {terminal}");
                if (holdSeconds > 0 && host.InputGeneration != previousGeneration)
                    throw new InvalidOperationException("Peer cleanup invalidated the live phone's input session.");
                reports++;
                await Task.Delay(holdSeconds == 0 ? 250 : 1000, limit.Token);
            }
            Console.WriteLine($"{mode}: live_seconds={liveWatch.Elapsed.TotalSeconds:F2} acknowledgements={reports} generation={host.InputGeneration}");
            watch.Restart();
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (host.IsReady || host.GateOpen || host.State != ReverseControlState.Idle)
                throw new InvalidOperationException("Stopped device host retained readiness.");
            Console.WriteLine($"{mode}: pass={pass} stop_seconds={watch.Elapsed.TotalSeconds:F2}");
        }
        Console.WriteLine(holdSeconds == 0
            ? $"Live {mode} host startup, HID acknowledgements, stop and restart passed."
            : $"Live {mode} host remained ready throughout the hold, with HID acknowledgements and clean stop.");
    }
}
