using System.IO;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Models;

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
}

static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual, string name)
{
    if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            $"{name}: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
}

// usbmux may reverse its enumeration order on every poll. Existing cards must
// never move, while a newly connected phone is appended exactly once.
Sequence(["phone-a", "phone-b"],
    StableDeviceSelection.MergeVisibleOrder(["phone-a", "phone-b"], ["phone-b", "phone-a"]),
    "reversed discovery keeps visible order");
Sequence(["phone-a", "phone-b", "phone-c"],
    StableDeviceSelection.MergeVisibleOrder(["phone-a", "phone-b"], ["phone-c", "phone-b", "phone-a"]),
    "new phone appends without moving selection");
Sequence(["phone-b", "phone-c"],
    StableDeviceSelection.MergeVisibleOrder(["phone-a", "phone-b", "phone-c"], ["phone-c", "phone-b"]),
    "disconnected phone is removed without reordering survivors");
Equal("phone-b", StableDeviceSelection.ChooseUdid(["phone-a", "phone-b"], "PHONE-B", "phone-a"),
    "previous selection wins case-insensitively");
Equal("phone-a", StableDeviceSelection.ChooseUdid(["phone-a", "phone-b"], "missing", "PHONE-A"),
    "active capture is fallback selection");
Equal("airplay://phone-b", StableDeviceSelection.ChooseUdid(
        ["phone-a", "airplay://phone-b"], "phone-a", "phone-a", "AIRPLAY://PHONE-B"),
    "new wireless connection is selected once");
Equal("media-cast://active", StableDeviceSelection.ChooseUdid(
        ["media-cast://active", "airplay://phone-b"], "media-cast://active", null,
        "airplay://phone-b", preferNewlyConnectedWireless: false),
    "active media cast is not displaced by its AirPlay connection");
Equal("airplay://phone-b", StableDeviceSelection.FindNewlyConnected(
        ["airplay://phone-a"], ["airplay://phone-a", "airplay://phone-b"]),
    "new wireless edge is detected");
Equal<string?>(null, StableDeviceSelection.FindNewlyConnected(
        ["airplay://phone-a"], ["AIRPLAY://PHONE-A"]),
    "known wireless device is not selected repeatedly");
Equal(2, StableDeviceSelection.CalculateDropIndex(3, 0, 2, true),
    "dragging first device after last moves it to the end");
Equal(0, StableDeviceSelection.CalculateDropIndex(3, 2, 0, false),
    "dragging last device before first moves it to the start");

Equal("iPhone 12 mini", AppleProductNames.Resolve("iPhone13,1"),
    "known ProductType uses the real model name");
Equal("iPhone99,9", AppleProductNames.Resolve("iPhone99,9"),
    "unknown ProductType remains visible for diagnostics");

// Display outlines are selected from ProductType when available. Legacy
// Home-button displays remain rectangular, while metadata-less startup can
// use a conservative decoded-frame aspect fallback.
Equal("iphone-dynamic-island",
    DeviceCornerProfileResolver.Resolve("iPhone18,3", 1206, 2622).Id,
    "iPhone17 family profile");
Equal("iphone-x",
    DeviceCornerProfileResolver.Resolve("iPhone10,3", 1125, 2436).Id,
    "iPhone X profile");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve("iPhone10,1", 750, 1334).Id,
    "iPhone 8 remains rectangular");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve("iPhone14,6", 750, 1334).Id,
    "iPhone SE 3 remains rectangular");
Equal("iphone-notch",
    DeviceCornerProfileResolver.Resolve("iPhone17,5", 1170, 2532).Id,
    "iPhone 16e retains notch profile");
Equal("iphone-12-mini",
    DeviceCornerProfileResolver.Resolve("iPhone13,1", 1080, 2340).Id,
    "iPhone 12 mini has a tighter device-specific curve");
Equal("iphone-13-mini",
    DeviceCornerProfileResolver.Resolve("iPhone14,4", 1080, 2340).Id,
    "iPhone 13 mini has a device-specific curve");
Equal("ipad-pro-rounded",
    DeviceCornerProfileResolver.Resolve("iPad8,1", 1668, 2388).Id,
    "2018 iPad Pro profile");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve("iPad12,1", 1620, 2160).Id,
    "Home-button iPad remains rectangular");
Equal("ipad-mini-rounded",
    DeviceCornerProfileResolver.Resolve("iPad14,1", 1488, 2266).Id,
    "iPad mini profile");
Equal("ipad-rounded",
    DeviceCornerProfileResolver.Resolve("iPad15,7", 1640, 2360).Id,
    "iPad A16 base profile");
Equal("ipad-pro-rounded",
    DeviceCornerProfileResolver.Resolve("iPad17,1", 1668, 2420).Id,
    "iPad M5 Pro profile");
Equal("iphone-dynamic-island",
    DeviceCornerProfileResolver.Resolve(null, 1206, 2622).Id,
    "unknown phone geometry fallback");
Equal("ipad-rounded",
    DeviceCornerProfileResolver.Resolve(null, 1640, 2360).Id,
    "unknown tablet geometry fallback");
Equal("rectangular",
    DeviceCornerProfileResolver.Resolve(null, 1000, 1000).Id,
    "ambiguous geometry does not clip");
Equal(0, DeviceCornerProfile.Rectangular.GetGdiRadius(1206),
    "rectangular fallback radius");

Equal("iPhoneMirror AirPlay",
    WirelessReceiverConfiguration.SanitizeReceiverName("  iPhoneMirror AirPlay  "),
    "wireless receiver name is trimmed");
Equal("iPhoneMirror AirPlay",
    WirelessReceiverConfiguration.SanitizeReceiverName("\r\n[];"),
    "invalid wireless receiver name falls back");
Equal(63,
    WirelessReceiverConfiguration.SanitizeReceiverName(new string('a', 80)).Length,
    "wireless receiver name respects the mDNS label limit");
Equal("1080p", WirelessReceiverConfiguration.DefaultDisplayProfile.Id,
    "wireless receiver defaults to the balanced 1080p profile");
Equal(true, WirelessReceiverConfiguration.RequiresOriginalQualityWarning(
        WirelessReceiverConfiguration.DisplayProfiles[0]),
    "wireless original-quality profile requires a stability warning");
Equal(false, WirelessReceiverConfiguration.RequiresOriginalQualityWarning(
        WirelessReceiverConfiguration.DisplayProfiles[1]),
    "wireless 1080p profile does not require the original-quality warning");
Equal(true, WirelessReceiverConfiguration.IsSupportedDisplayProfile(1280, 720, 30),
    "wireless 720p weak-network profile is supported");
Equal(false, WirelessReceiverConfiguration.IsSupportedDisplayProfile(1280, 720, 60),
    "unsupported wireless profile combinations are rejected");
Equal(true, MediaSourceClassifier.IsLikelyLive(
        new Uri("https://example.test/video.m3u8")),
    "HLS extension is classified as live");
Equal(true, MediaSourceClassifier.IsLikelyLive(
        new Uri("https://example.test/live/playlist")),
    "extensionless live playlist is classified as live");
Equal(true, MediaSourceClassifier.IsLikelyLive(
        new Uri("https://example.test/watch?id=1&format=m3u8")),
    "HLS query hint is classified as live");
Equal(false, MediaSourceClassifier.IsLikelyLive(
        new Uri("https://example.test/library/video.mp4")),
    "ordinary MP4 remains on-demand media");

// MediaElement events do not identify the Source that raised them. A fresh
// backend is bound for every load so delayed events can be rejected by both
// casting generation and sender identity.
var mediaEvents = new MediaCastEventGate();
var firstBackend = new object();
var firstMediaGeneration = mediaEvents.BeginGeneration();
Equal(true, mediaEvents.TryBind(firstMediaGeneration, firstBackend),
    "first media backend binds to its generation");
Equal(true, mediaEvents.IsCurrent(firstMediaGeneration, firstBackend),
    "current media backend event is accepted");
var recoveredBackend = new object();
Equal(true, mediaEvents.TryBind(firstMediaGeneration, recoveredBackend),
    "live recovery can replace the backend within one cast generation");
Equal(false, mediaEvents.IsCurrent(firstMediaGeneration, firstBackend),
    "late event from the pre-recovery backend is rejected");
Equal(true, mediaEvents.IsCurrent(firstMediaGeneration, recoveredBackend),
    "event from the recovered backend is accepted");
mediaEvents.Invalidate();
Equal(false, mediaEvents.IsCurrent(firstMediaGeneration, recoveredBackend),
    "late event after stop is rejected");
var secondBackend = new object();
var secondMediaGeneration = mediaEvents.BeginGeneration();
Equal(true, mediaEvents.TryBind(secondMediaGeneration, secondBackend),
    "new cast backend binds to the new generation");
Equal(false, mediaEvents.TryBind(firstMediaGeneration, firstBackend),
    "stale generation cannot replace the current backend");
Equal(true, mediaEvents.IsCurrent(secondMediaGeneration, secondBackend),
    "stale bind attempt leaves the new backend current");

var recoveryNow = DateTimeOffset.Parse("2026-07-27T00:00:00Z");
var mediaRecovery = new MediaRecoveryBackoff(() => recoveryNow,
    maximumAttempts: 5, stablePlaybackWindow: TimeSpan.FromSeconds(10));
var recoveryDelays = new List<double>();
for (var index = 0; index < 5; ++index)
{
    mediaRecovery.MarkOpened();
    Equal(true, mediaRecovery.TryGetNext(out _, out var delay),
        "short-lived live playback remains recoverable");
    recoveryDelays.Add(delay.TotalMilliseconds);
}
Equal(true, recoveryDelays.SequenceEqual([250, 500, 1000, 2000, 4000]),
    "live recovery uses increasing backoff across short opens");
Equal(false, mediaRecovery.TryGetNext(out _, out _),
    "live recovery has a session-level retry budget");
mediaRecovery.Reset();
mediaRecovery.MarkOpened();
recoveryNow += TimeSpan.FromSeconds(11);
Equal(true, mediaRecovery.TryGetNext(out var stableAttempt, out var stableDelay) &&
    stableAttempt == 1 && stableDelay == TimeSpan.FromMilliseconds(250),
    "stable playback resets live recovery backoff");
Equal<string?>(null,
    WirelessReceiverConfiguration.FindExecutable(Path.GetTempPath(),
        Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")),
    "wireless receiver discovery rejects missing executables");

var logPath = Path.Combine(Path.GetTempPath(), $"iPhoneMirror-log-{Guid.NewGuid():N}.txt");

var sensitiveDeviceId = "00008110001234567890abcd1234567890abcdef";
var deviceToken = AppLog.Device(sensitiveDeviceId);
Equal(true, deviceToken.StartsWith("device#", StringComparison.Ordinal),
    "device log identity uses an anonymous token");
Equal(deviceToken, AppLog.Device(sensitiveDeviceId),
    "device log identity remains stable within diagnostics");
Equal(false, deviceToken.Contains(sensitiveDeviceId, StringComparison.OrdinalIgnoreCase),
    "device log identity does not contain the raw serial");
Equal("media-cast", AppLog.Device("media-cast://active"),
    "media virtual device has a non-sensitive fixed identity");
var privateMediaSource = new Uri(
    "https://private.example.local/library/personal-video.m3u8?access_token=secret");
Equal("https/m3u8?query=True", AppLog.MediaSource(privateMediaSource),
    "media source diagnostics retain format without host, path, or query values");
Equal("relative/unknown?query=False",
    AppLog.MediaSource(new Uri("../private/video.mp4?token=secret", UriKind.Relative)),
    "relative media source diagnostics are safe and non-throwing");
var sanitizedLog = AppLog.Sanitize(
    $"failed {privateMediaSource} device={sensitiveDeviceId}\r\nC:\\Users\\Private\\secret.txt");
Equal(false, sanitizedLog.Contains("private.example.local", StringComparison.OrdinalIgnoreCase),
    "log sanitization removes media hosts");
Equal(false, sanitizedLog.Contains("access_token", StringComparison.OrdinalIgnoreCase),
    "log sanitization removes media query names and values");
Equal(false, sanitizedLog.Contains(sensitiveDeviceId, StringComparison.OrdinalIgnoreCase),
    "log sanitization removes raw device serials");
Equal(false, sanitizedLog.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase),
    "log sanitization removes local paths");
Equal(false, sanitizedLog.Contains('\n'),
    "log sanitization produces a single-line entry");
var modernDeviceId = "00008110-001234567890ABCD";
var credentialLog = AppLog.Sanitize(
    "Authorization: Bearer bearer-secret-value\n" +
    "Proxy-Authorization: Basic YmFzaWMtc2VjcmV0\n" +
    "Cookie: session=cookie-secret; csrf=cookie-csrf\n" +
    "Authorization=opaque-authorization-secret\n" +
    "Cookie=assignment-cookie-secret; assignment-csrf-secret\n" +
    "token=bare-token password=\"quoted password\" api_key:'api-secret' " +
    $"server=private.internal endpoint=192.168.10.20:8080 device={modernDeviceId} " +
    "mac=AA:BB:CC:DD:EE:FF path=/Users/Private/Movies/video.mp4");
foreach (var secret in new[]
         {
             "bearer-secret-value", "YmFzaWMtc2VjcmV0", "cookie-secret",
             "cookie-csrf", "bare-token", "quoted password", "api-secret",
             "opaque-authorization-secret", "assignment-cookie-secret",
             "assignment-csrf-secret",
             "private.internal", "192.168.10.20", modernDeviceId,
             "AA:BB:CC:DD:EE:FF", "/Users/Private",
         })
    Equal(false, credentialLog.Contains(secret, StringComparison.OrdinalIgnoreCase),
        $"log sanitization removes sensitive value {secret}");
Equal(true, credentialLog.Contains("<redacted>", StringComparison.Ordinal),
    "credential fields retain a redaction marker");
Equal(false, credentialLog.Contains('\n'),
    "credential header redaction remains single-line");
var structuredLog = AppLog.Event("capture_state",
    ("device", sensitiveDeviceId), ("handle", 17UL), ("state", "Streaming"));
Equal(false, structuredLog.Contains(sensitiveDeviceId, StringComparison.OrdinalIgnoreCase),
    "structured event values are sanitized before persistence");
Equal(true, structuredLog.Contains("handle=17", StringComparison.Ordinal),
    "structured event preserves non-sensitive numeric context");
var injectedEvent = AppLog.Event("capture state=forged\nnext",
    ("bad key=injected", "value\nforged=true"),
    ("oversized", new string('x', 5000)));
Equal(false, injectedEvent.Contains('\n'),
    "structured event flattens newline injection");
Equal(false, injectedEvent.Contains("bad key", StringComparison.Ordinal),
    "structured event normalizes field keys");
Equal(true, injectedEvent.Length < 600,
    "structured event bounds an individual field");
var manyFields = Enumerable.Range(0, 10)
    .Select(index => (object?)($"field_{index}", new string('y', 384)))
    .ToArray();
var boundedEvent = AppLog.Event("bounded_event", manyFields);
Equal(true, boundedEvent.Length <= 2048,
    "structured event bounds total line length");
Equal(true, boundedEvent.EndsWith("truncated=true", StringComparison.Ordinal),
    "structured event marks total-line truncation");
Equal(2048, AppLog.Message(new string('z', 5000)).Length,
    "direct application log message is bounded");
Equal(true, NativeLogTailReader.IsUiEventLine(
        "12:00:00.000 [seq=42] [info] [app] ui_event action refreshed"),
    "structured native UI event is recognized for duplicate suppression");
Equal(true, NativeLogTailReader.IsUiEventLine("ui_event action refreshed"),
    "unprefixed native UI event remains recognized");
Equal(false, NativeLogTailReader.IsUiEventLine(
        "12:00:00.000 [seq=43] [info] [capture] frame ready"),
    "non-UI native event remains visible");
try
{
    var reader = new NativeLogTailReader(logPath);
    await File.WriteAllTextAsync(logPath, "first\npartial");
    Sequence(["first"], await reader.ReadNewLinesAsync(),
        "log reader publishes complete lines only");
    await File.AppendAllTextAsync(logPath, "-line\n");
    Sequence(["partial-line"], await reader.ReadNewLinesAsync(),
        "log reader preserves a partial line across reads");
    await File.WriteAllTextAsync(logPath, "reset\n");
    Sequence(["reset"], await reader.ReadNewLinesAsync(),
        "log reader restarts after truncation");
    await File.AppendAllTextAsync(logPath, new string('x', 20 * 1024) + "\n");
    var longLines = await reader.ReadNewLinesAsync();
    Equal(1, longLines.Count, "log reader returns one bounded long line");
    Equal(16 * 1024, longLines[0].Length, "log reader bounds individual line memory");
    Equal(true, longLines[0].EndsWith('…'), "bounded log line has a truncation marker");
}
finally
{
    if (File.Exists(logPath)) File.Delete(logPath);
}

var driverManagerRoot = Path.Combine(Path.GetTempPath(), "iPhoneMirror.App.Tests",
    Guid.NewGuid().ToString("N"));
try
{
    var appDirectory = Path.Combine(driverManagerRoot, "outputs", "iPhoneMirror");
    var siblingDirectory = Path.Combine(driverManagerRoot, "outputs", "iPhoneMirror.Driver");
    Directory.CreateDirectory(appDirectory);
    Directory.CreateDirectory(siblingDirectory);
    var siblingExecutable = Path.Combine(siblingDirectory, "iPhoneMirror.Driver.exe");
    File.WriteAllBytes(siblingExecutable, []);
    Equal(Path.GetFullPath(siblingExecutable),
        DriverManagerLauncher.FindExecutable(appDirectory, workingDirectory: driverManagerRoot),
        "driver manager discovery finds sibling output");

    var overrideExecutable = Path.Combine(driverManagerRoot, "custom-driver-manager.exe");
    File.WriteAllBytes(overrideExecutable, []);
    Equal(Path.GetFullPath(overrideExecutable),
        DriverManagerLauncher.FindExecutable(appDirectory, overrideExecutable,
            driverManagerRoot),
        "driver manager override takes priority");
}
finally
{
    if (Directory.Exists(driverManagerRoot)) Directory.Delete(driverManagerRoot, recursive: true);
}

Equal(false, IndependentWindowAudioPolicy.ShowMuteOthers(1),
    "single device only shows the current-window mute action");
Equal(true, IndependentWindowAudioPolicy.ShowMuteOthers(2),
    "multiple devices show the mute-other-windows action");
Sequence(["phone-b", "phone-c"],
    IndependentWindowAudioPolicy.GetOtherDeviceIds("PHONE-A",
        ["phone-a", "phone-b", "PHONE-B", "phone-c"]),
    "mute-other-windows excludes the current device and duplicate sessions");


// Closing must explicitly stop the QuickTime session before core disposal,
// and repeated close notifications must not send a second shutdown sequence.
var shutdownOrder = new List<string>();
var shutdown = new CaptureShutdownCoordinator();
await shutdown.StopAndDisposeOnceAsync(
    () => { shutdownOrder.Add("stop"); return Task.CompletedTask; },
    () => { shutdownOrder.Add("dispose"); return Task.CompletedTask; });
await shutdown.StopAndDisposeOnceAsync(
    () => { shutdownOrder.Add("duplicate-stop"); return Task.CompletedTask; },
    () => { shutdownOrder.Add("duplicate-dispose"); return Task.CompletedTask; });
Sequence(["stop", "dispose"], shutdownOrder, "window close cleanup is ordered and idempotent");

var deviceA = new DeviceCaptureState { Udid = "phone-a", Handle = 11, FrameRate = 60, Volume = 80 };
var deviceB = new DeviceCaptureState { Udid = "phone-b", Handle = 22, FrameRate = 30, Volume = 25 };
Equal(UsbProjectionMode.Demo, deviceA.UsbProjectionMode,
    "USB projection defaults to recommended demo mode");
deviceA.UsbProjectionMode = UsbProjectionMode.AirPlay;
deviceB.UsbProjectionMode = UsbProjectionMode.Aisi;
Equal(UsbProjectionMode.AirPlay, deviceA.UsbProjectionMode,
    "device A keeps its independent USB projection mode");
Equal(UsbProjectionMode.Aisi, deviceB.UsbProjectionMode,
    "device B keeps its independent USB projection mode");
Equal(DecoderPreference.Auto, deviceA.DecoderPreference,
    "decoder selection defaults to capability-based automatic fallback");
Equal(ColorOutputPreference.Auto, deviceA.ColorOutputPreference,
    "color output defaults to automatic HDR display detection");
deviceA.DecoderPreference = DecoderPreference.HardwarePreferred;
deviceA.ColorOutputPreference = ColorOutputPreference.PreferHdrWhenSupported;
deviceB.DecoderPreference = DecoderPreference.SoftwareCompatible;
deviceB.ColorOutputPreference = ColorOutputPreference.ForceSdrToneMap;
Equal(DecoderPreference.HardwarePreferred, deviceA.DecoderPreference,
    "device A keeps its independent decoder policy");
Equal(DecoderPreference.SoftwareCompatible, deviceB.DecoderPreference,
    "device B keeps its independent decoder policy");
Equal(ColorOutputPreference.PreferHdrWhenSupported, deviceA.ColorOutputPreference,
    "device A keeps its independent HDR output policy");
Equal(ColorOutputPreference.ForceSdrToneMap, deviceB.ColorOutputPreference,
    "device B keeps its independent SDR tone-map policy");
deviceB.FrameRate = 24;
Equal((ulong)11, deviceA.Handle, "switching device does not release first session");
Equal(60, deviceA.FrameRate, "device A settings remain independent");
Equal(24, deviceB.FrameRate, "device B settings update independently");

// Even when explicit stop fails, im_shutdown/dispose remains a mandatory
// defensive cleanup path.
var failureOrder = new List<string>();
var failedShutdown = new CaptureShutdownCoordinator();
try
{
    await failedShutdown.StopAndDisposeOnceAsync(
        () => { failureOrder.Add("stop"); throw new InvalidOperationException("stop failed"); },
        () => { failureOrder.Add("dispose"); return Task.CompletedTask; });
    throw new InvalidOperationException("failed shutdown should propagate its stop error");
}
catch (InvalidOperationException error) when (error.Message == "stop failed")
{
}
Sequence(["stop", "dispose"], failureOrder, "core is disposed after stop failure");

Console.WriteLine("App logic tests passed.");
