using System.Diagnostics;
using IPhoneMirror.App.Services;

if (args is ["--absolute"] or ["--absolute", _])
    return await RunAbsoluteMouseProbeAsync(args.Length == 2 ? args[1] : null);

const string targetUdid = "00008101-00044D600A22001E";
const string targetName = "Ray's Phone";
var duration = args.Length > 0 && int.TryParse(args[0], out var seconds)
    ? TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 120))
    : TimeSpan.FromSeconds(20);

DiagnosticLogger.Initialize();
await using var hid = new BluetoothHidMouseService();
hid.StatusChanged += (_, _) => Console.WriteLine(
    $"STATUS advertising={hid.IsAdvertising} connected={hid.IsConnected} " +
    $"failed={hid.HasTransportFailure} text={hid.Status} error={hid.Error ?? "none"}");

Console.WriteLine("Starting BLE HID advertising...");
if (!await hid.StartAsync(targetUdid, targetName))
{
    Console.Error.WriteLine($"START_FAILED {hid.Status}: {hid.Error}");
    return 2;
}

Console.WriteLine("Waiting up to 30 seconds for the paired iPhone HID subscription...");
if (!await hid.WaitForConnectionAsync(TimeSpan.FromSeconds(30)))
{
    Console.Error.WriteLine("CONNECT_TIMEOUT");
    return 3;
}

Console.WriteLine($"CONNECTED client={hid.TargetClientId}; stress={duration.TotalSeconds:F0}s");
var started = Stopwatch.GetTimestamp();
var sent = 0;
var direction = 1;
while (Stopwatch.GetElapsedTime(started) < duration && !hid.HasTransportFailure)
{
    // Alternate direction frequently so any stale replay is immediately
    // visible as overshoot instead of blending into one long movement.
    if (sent % 18 == 0) direction = -direction;
    // Periodic discrete wheel reports exercise the priority path shared by
    // click/drag state transitions without producing unintended taps.
    var wheel = sent > 0 && sent % 120 == 0 ? 1 : 0;
    await hid.SendMouseAsync(direction * 3, 0, wheel: wheel,
        expectedTargetDeviceUdid: targetUdid);
    sent++;
    await Task.Delay(8);
}

await hid.SendMouseAsync(0, 0, expectedTargetDeviceUdid: targetUdid);
await Task.Delay(500);
Console.WriteLine($"RESULT sent={sent} connected={hid.IsConnected} " +
    $"failed={hid.HasTransportFailure} status={hid.Status} error={hid.Error ?? "none"}");
return hid.HasTransportFailure ? 4 : 0;

static async Task<int> RunAbsoluteMouseProbeAsync(string? targetName)
{
    const string target = "absolute-mouse-probe";
    DiagnosticLogger.Initialize();
    await using var hid = new BluetoothHidMouseService(absoluteMouseProbe: true);
    hid.StatusChanged += (_, _) => Console.WriteLine(
        $"BLE advertising={hid.IsAdvertising} connected={hid.IsConnected} status={hid.Status} error={hid.Error ?? "none"}");

    Console.WriteLine("Absolute BLE mouse probe (movement only)");
    Console.WriteLine("Stop iPhoneMirror Bluetooth control before pairing. AirPlay can remain on.");
    Console.WriteLine($"On the iPad, connect to {hid.SuggestedDeviceName} under Settings > Bluetooth.");
    if (!await hid.StartAsync(target, targetName))
    {
        Console.Error.WriteLine($"START_FAILED {hid.Status}: {hid.Error}");
        return 2;
    }
    if (!await hid.WaitForConnectionAsync(TimeSpan.FromMinutes(2)))
    {
        Console.Error.WriteLine("CONNECT_TIMEOUT: iPad did not subscribe to the mouse report.");
        return 3;
    }

    Console.WriteLine("Choose a point. The iPad pointer should jump to it in a single report.");
    Console.WriteLine("  1 2 3");
    Console.WriteLine("  4 5 6    X and Y positions: 15%, 50%, 85%");
    Console.WriteLine("  7 8 9    Q or Esc: exit");
    double[] axes = [0.15, 0.5, 0.85];
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape) return 0;
        var point = key.KeyChar - '1';
        if (point is < 0 or > 8) continue;

        var x = axes[point % 3];
        var y = axes[point / 3];
        var sent = await hid.MoveAbsoluteMouseProbeAsync(x, y, target);
        Console.WriteLine($"{(sent ? "SENT" : "SEND_FAILED")} point={point + 1} x={x:P0} y={y:P0}");
        if (!sent)
        {
            Console.Error.WriteLine("Check the Bluetooth connection. No relative movement was sent.");
            return 4;
        }
    }
}
