using IPhoneMirror.App.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace IPhoneMirror.App.Services;

public sealed record BridgeEvent(string Event, string? Code, string? Message, string? Text = null);

public sealed record TouchPoint(
    [property: JsonPropertyName("pointerId")] int PointerId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("normalizedX")] double NormalizedX,
    [property: JsonPropertyName("normalizedY")] double NormalizedY);

public sealed class DirectUsbInputBridge : IAsyncDisposable
{
    // A first-run Personalized DDI download, Apple TSS personalization, and
    // mount may consume the bridge's 180 second device timeout. A stale DDI
    // recovery can add one 30 second unmount and a second tunnel handshake.
    private static readonly TimeSpan InitialReadyTimeout = TimeSpan.FromSeconds(360);
    private Process? _process;
    private StreamReader? _stdout;
    private StreamWriter? _stdin;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readerTask;
    private Task? _errorDrainTask;
    private TaskCompletionSource<bool>? _readySignal;
    private string? _requestedUdid;
    private bool _requestedWireless;
    private string? _lastDiagnostic;
    private string? _lastErrorCode;
    private string? _lastStandardError;
    private long _readyGeneration;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _sequence;
    private int _stopping;
    private int _terminalEventReceived;

    public bool IsReady { get; private set; }
    internal long InputGeneration => Interlocked.Read(ref _readyGeneration);
    public bool GateOpen { get; private set; }
    public string? AuthMode { get; private set; }
    public string? Udid { get; private set; }
    public int RateHz { get; private set; }
    public string? LastDiagnostic => _lastDiagnostic;
    public string? LastErrorCode => _lastErrorCode;

    public event Action<BridgeEvent>? OnEvent;

    public async Task StartAsync(
        string pythonExe = "python",
        string? bridgeScript = null,
        string? udid = null,
        int rateHz = 120,
        bool wireless = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(udid))
            throw new ArgumentException("Bridge transport requires an explicit Apple UDID.", nameof(udid));
        Interlocked.Exchange(ref _stopping, 0);
        Interlocked.Exchange(ref _terminalEventReceived, 0);
        AuthMode = null;
        _requestedUdid = udid;
        _requestedWireless = wireless;
        bridgeScript ??= Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
        var usePackagedBridge = string.Equals(Path.GetExtension(bridgeScript), ".exe",
            StringComparison.OrdinalIgnoreCase);
        if (usePackagedBridge &&
            !RuntimeBinaryIntegrity.VerifyUsbTouchBridgeRuntime(bridgeScript,
                out var runtimeFailure))
        {
            throw new InvalidOperationException(
                LocalizationService.Format("TouchBridgeRuntimeIncompleteFormat", runtimeFailure));
        }
        var launchFile = usePackagedBridge ? bridgeScript : pythonExe;

        var psi = new ProcessStartInfo
        {
            FileName = launchFile,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(bridgeScript)) ?? AppContext.BaseDirectory,
            CreateNoWindow = true,
        };
        // libusb0.dll is published beside the main application, while the
        // PyInstaller bridge runs from tools\.  Python's ctypes loader does
        // not search the parent directory, so make every packaged runtime
        // location explicit for both fresh installs and overlay upgrades.
        var bridgeDirectory = Path.GetDirectoryName(Path.GetFullPath(bridgeScript))
            ?? AppContext.BaseDirectory;
        var applicationDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var runtimeDirectories = new[]
        {
            applicationDirectory,
            bridgeDirectory,
            Path.Combine(bridgeDirectory, "_internal"),
        };
        var existingPath = psi.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH");
        var pathEntries = new List<string>(runtimeDirectories.Length + 1);
        foreach (var directory in runtimeDirectories)
        {
            if (!string.IsNullOrWhiteSpace(directory) &&
                !pathEntries.Contains(directory, StringComparer.OrdinalIgnoreCase))
                pathEntries.Add(directory);
        }
        if (!string.IsNullOrWhiteSpace(existingPath))
            pathEntries.Add(existingPath);
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, pathEntries);
        if (!usePackagedBridge)
            psi.ArgumentList.Add(bridgeScript);
        psi.ArgumentList.Add(wireless ? "--wireless" : "--usb");
        psi.ArgumentList.Add("--rate-hz");
        psi.ArgumentList.Add(rateHz.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--udid");
        psi.ArgumentList.Add(udid);
        if (GetPersonalizedDdiDirectory() is { } ddiDirectory)
        {
            // Prefer the verified bundled Personalized DDI; an environment
            // override still allows operators to provide a different build.
            psi.ArgumentList.Add("--ddi-dir");
            psi.ArgumentList.Add(ddiDirectory);
        }

        var startedProcess = Process.Start(psi) ?? throw new InvalidOperationException(LocalizationService.Get("TouchBridgeStartFailed"));
        _process = startedProcess;
        _stdin = startedProcess.StandardInput;
        _stdout = startedProcess.StandardOutput;

        _readySignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        var bridgeProcessId = startedProcess.Id;
        _errorDrainTask = Task.Run(() => DrainErrorAsync(startedProcess.StandardError, bridgeProcessId));
        startedProcess.EnableRaisingEvents = true;
        startedProcess.Exited += (_, _) => _ = HandleProcessExitAsync(startedProcess);
        if (startedProcess.HasExited)
            _ = HandleProcessExitAsync(startedProcess);

        await WaitForReadyAsync(ct);
    }

    private static string? GetPersonalizedDdiDirectory()
    {
        var bundledDirectory = Path.Combine(
            AppContext.BaseDirectory, "tools", "ddi", "Xcode_iOS_DDI_Personalized");
        if (HasCompletePersonalizedDdiBundle(bundledDirectory))
            return bundledDirectory;
        // The bridge owns fallback ordering: bundled image first, then the
        // IPHONE_MIRROR_DDI_DIR environment override, then GitHub download.
        // Do not pass the environment path here or it would bypass the bundle.
        return null;
    }

    private static bool HasCompletePersonalizedDdiBundle(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var fileName in new[]
                 { "Image.dmg", "BuildManifest.plist", "Image.trustcache" })
        {
            try
            {
                if (new FileInfo(Path.Combine(directory, fileName)).Length <= 0)
                    return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return true;
    }

    public async Task SendTouchBatchAsync(IReadOnlyList<TouchPoint> points, long timestampNs,
        long sequence, CancellationToken ct = default, Func<bool>? canSend = null,
        long? expectedGeneration = null)
    {
        var generation = Interlocked.Read(ref _readyGeneration);
        if (!IsReady || _stdin is null)
            throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));

        if (points.Count == 0 || points.Count > CoreDeviceTouchProtocol.MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(points), LocalizationService.Get("TouchBridgeInvalidBatch"));
        foreach (var point in points)
        {
            if (point.PointerId < 0)
                throw new ArgumentOutOfRangeException(nameof(points), LocalizationService.Get("TouchBridgeInvalidPointId"));
            if (!CoreDeviceTouchProtocol.IsNormalizedCoordinate(point.NormalizedX) ||
                !CoreDeviceTouchProtocol.IsNormalizedCoordinate(point.NormalizedY))
                throw new ArgumentOutOfRangeException(nameof(points), LocalizationService.Get("TouchBridgeInvalidCoordinates"));
        }
        await _sendLock.WaitAsync(ct);
        try
        {
            if (expectedGeneration is { } expected && expected != Interlocked.Read(ref _readyGeneration))
                throw new OperationCanceledException();
            // Pure releases must pass after focus loss. A queued contact or
            // movement belongs to the focus snapshot that created it.
            if (points.Any(point => point.Action != "up") && canSend?.Invoke() == false) return;
            // Recovery may close the gate while this packet waits for the writer.
            if (generation != Interlocked.Read(ref _readyGeneration)) return;
            if (!IsReady || _stdin is null)
                throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));
            var frameSequence = NextSequence();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = CoreDeviceTouchProtocol.MessageSchema,
                    generation,
                    kind = CoreDeviceTouchProtocol.MessageKind,
                    seq = frameSequence,
                    timestampNs,
                    points,
                });
            var header = BitConverter.GetBytes((uint)bytes.Length);

            await _stdin.BaseStream.WriteAsync(header, ct);
            await _stdin.BaseStream.WriteAsync(bytes, ct);
            await _stdin.BaseStream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task TouchDownAsync(int pointerId, double normalizedX, double normalizedY, CancellationToken ct = default)
    {
        await SendTouchBatchAsync(new[] { new TouchPoint(pointerId, "down", normalizedX, normalizedY) }, DateTimeOffset.UtcNow.ToUnixTimeNanoseconds(), NextSequence(), ct);
    }

    public async Task TouchMoveAsync(int pointerId, double normalizedX, double normalizedY, CancellationToken ct = default)
    {
        await SendTouchBatchAsync(new[] { new TouchPoint(pointerId, "move", normalizedX, normalizedY) }, DateTimeOffset.UtcNow.ToUnixTimeNanoseconds(), NextSequence(), ct);
    }

    public async Task TouchUpAsync(int pointerId, double normalizedX, double normalizedY, CancellationToken ct = default)
    {
        await SendTouchBatchAsync(new[] { new TouchPoint(pointerId, "up", normalizedX, normalizedY) }, DateTimeOffset.UtcNow.ToUnixTimeNanoseconds(), NextSequence(), ct);
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _cts.Cancel();
        // Closing stdin ends the bridge's input loop and lets its teardown run:
        // releasing all touch points, closing the CoreDevice tunnel, and
        // releasing the claimed usbmux interface. Killing the process instead
        // leaves that interface to asynchronous PnP cleanup, which races the
        // capture teardown that follows and can force iOS to re-prompt for
        // trust. Give the graceful exit a bounded window before killing.
        try { _stdin?.Close(); } catch { }
        if (_process is { HasExited: false })
        {
            try
            {
                using var gracePeriod = new CancellationTokenSource(
                    TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(gracePeriod.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
            if (!_process.HasExited)
            {
                try { _process.Kill(); } catch { }
            }
        }
        var reader = _readerTask;
        var errorDrain = _errorDrainTask;
        if (reader is not null || errorDrain is not null)
        {
            var tasks = new[] { reader, errorDrain }.Where(task => task is not null)
                .Cast<Task>().ToArray();
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* Process termination must not block UI teardown. */ }
        }
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        try { _process?.Dispose(); } catch { }
        _stdin = null;
        _stdout = null;
        _process = null;
        _readySignal = null;
        IsReady = false;
        GateOpen = false;
        AuthMode = null;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_stdout is null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await _stdout.ReadLineAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                if (line is null) break;
                HandleLine(line);
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested && Volatile.Read(ref _stopping) == 0 &&
                Interlocked.Exchange(ref _terminalEventReceived, 1) == 0)
            {
                IsReady = false;
                GateOpen = false;
                _lastErrorCode = "bridge_output_closed";
                OnEvent?.Invoke(new BridgeEvent("error", _lastErrorCode,
                    LocalizationService.Get("TouchBridgeOutputDisconnected")));
            }
            if (!IsReady)
                _readySignal?.TrySetException(new InvalidOperationException(
                    LocalizationService.Get("TouchBridgeOutputClosed")));
        }
    }

    private void HandleLine(string line)
    {
        _lastDiagnostic = line;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var evt = root.GetProperty("event").GetString();
            switch (evt)
            {
                case "status":
                    var statusCode = root.TryGetProperty("code", out var c) ? c.GetString() : null;
                    if (statusCode == "recovery_triggered")
                    {
                        IsReady = false;
                        GateOpen = false;
                    }
                    if (string.Equals(statusCode, "terminated", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Exchange(ref _terminalEventReceived, 1);
                        IsReady = false;
                        GateOpen = false;
                    }
                    OnEvent?.Invoke(new BridgeEvent("status",
                        statusCode, root.TryGetProperty("message", out var sm) ? sm.GetString() : null));
                    break;
                case "ready":
                    var readyUdid = root.TryGetProperty("udid", out var u) ? u.GetString() : null;
                    var transport = root.TryGetProperty("transport", out var t) ? t.GetString() : null;
                    if (!string.Equals(readyUdid, _requestedUdid, StringComparison.OrdinalIgnoreCase))
                    {
                        RejectReady("device_identity_mismatch", LocalizationService.Format("TouchBridgeTargetMismatchFormat", _requestedUdid, readyUdid ?? LocalizationService.Get("Unavailable")));
                        return;
                    }
                    var expectedTransport = _requestedWireless ? "wireless" : "usb";
                    if (!string.Equals(transport, expectedTransport,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        RejectReady("bridge_transport_mismatch", LocalizationService.Format("TouchBridgeTransportMismatchFormat", expectedTransport, transport));
                        return;
                    }
                    Udid = readyUdid;
                    RateHz = root.TryGetProperty("rateHz", out var r) ? r.GetInt32() : 0;
                    GateOpen = root.TryGetProperty("gateOpen", out var g) && g.GetBoolean();
                    AuthMode = root.TryGetProperty("authMode", out var am)
                        ? am.GetString() : null;
                    if (!GateOpen)
                    {
                        IsReady = false;
                        _lastErrorCode = "remote_control_gate_unavailable";
                        var gateMessage =
                            LocalizationService.Get("TouchBridgeAuthenticationRequired");
                        OnEvent?.Invoke(new BridgeEvent("error", _lastErrorCode, gateMessage));
                        _readySignal?.TrySetException(CreateStartupException(gateMessage));
                        return;
                    }
                    Interlocked.Exchange(ref _readyGeneration,
                        root.TryGetProperty("generation", out var generation) ? generation.GetInt64() : 0);
                    IsReady = true;
                    Interlocked.Exchange(ref _terminalEventReceived, 0);
                    _lastErrorCode = null;
                    OnEvent?.Invoke(new BridgeEvent("ready", null, "gate_open"));
                    _readySignal?.TrySetResult(true);
                    break;
                case "warning":
                    OnEvent?.Invoke(new BridgeEvent("warning",
                        root.TryGetProperty("code", out var wc) ? wc.GetString() : null,
                        root.TryGetProperty("message", out var m) ? m.GetString() : null));
                    break;
                case "clipboard_text":
                    OnEvent?.Invoke(new BridgeEvent("clipboard_text", null, null,
                        root.TryGetProperty("text", out var ct) ? ct.GetString() : null));
                    break;
                case "error":
                    Interlocked.Exchange(ref _terminalEventReceived, 1);
                    IsReady = false;
                    GateOpen = false;
                    _lastErrorCode = root.TryGetProperty("code", out var ec)
                        ? ec.GetString() : null;
                    var message = root.TryGetProperty("message", out var em)
                        ? em.GetString() : null;
                    OnEvent?.Invoke(new BridgeEvent("error",
                        _lastErrorCode, message));
                    _readySignal?.TrySetException(CreateStartupException(
                        LocalizationService.Get("TouchBridgeReportedError"), message));
                    break;
            }
        }
        catch
        {
            RejectReady("bridge_invalid_output", LocalizationService.Get("TouchBridgeInvalidOutput"));
        }
    }

    private void RejectReady(string code, string message)
    {
        IsReady = false;
        GateOpen = false;
        _lastErrorCode = code;
        Interlocked.Exchange(ref _terminalEventReceived, 1);
        _readySignal?.TrySetException(CreateStartupException(message));
        OnEvent?.Invoke(new BridgeEvent("error", code, message));
    }

    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        var readySignal = _readySignal ?? throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotStarted"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InitialReadyTimeout);
        try { await readySignal.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(LocalizationService.Format("TouchBridgeTimeoutFormat", InitialReadyTimeout.TotalSeconds, _lastDiagnostic ?? LocalizationService.Get("ReverseControlNoDetail")));
        }
    }

    public async Task SendKeyboardAsync(IReadOnlyCollection<byte> usages,
        CancellationToken ct = default, Func<bool>? canSend = null)
    {
        var generation = Interlocked.Read(ref _readyGeneration);
        if (!IsReady || _stdin is null)
            throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));
        if (usages.Count > CoreDeviceTouchProtocol.MaxKeyboardUsages)
            throw new ArgumentOutOfRangeException(nameof(usages));
        // System.Text.Json encodes byte[] as a base64 string. The bridge
        // protocol requires a JSON numeric array (for example [4] or []), so
        // widen the usages before serialization instead of passing byte[].
        var normalized = usages.Distinct().OrderBy(value => value)
            .Select(value => (int)value).ToArray();
        await _sendLock.WaitAsync(ct);
        try
        {
            // Drop stale presses after waiting for the writer. Empty reports
            // release held keys and must still pass after focus is lost.
            if (normalized.Length != 0 && canSend?.Invoke() == false) return;
            // Recovery may close the gate while this packet waits for the writer.
            if (generation != Interlocked.Read(ref _readyGeneration)) return;
            if (!IsReady || _stdin is null)
                throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));
            var frame = new
            {
                schema = CoreDeviceTouchProtocol.MessageSchema,
                generation,
                kind = CoreDeviceTouchProtocol.KeyboardMessageKind,
                seq = NextSequence(),
                timestampNs = DateTimeOffset.UtcNow.ToUnixTimeNanoseconds(),
                usages = normalized,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            var header = BitConverter.GetBytes((uint)bytes.Length);

            await _stdin.BaseStream.WriteAsync(header, ct);
            await _stdin.BaseStream.WriteAsync(bytes, ct);
            await _stdin.BaseStream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }

    public async Task SendButtonAsync(ushort usagePage, ushort usageCode,
        string state, CancellationToken ct = default, Func<bool>? canSend = null)
    {
        var generation = Interlocked.Read(ref _readyGeneration);
        if (!IsReady || _stdin is null)
            throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));
        if (state is not ("down" or "up" or "canceled"))
            throw new ArgumentOutOfRangeException(nameof(state));
        await _sendLock.WaitAsync(ct);
        try
        {
            if (state == "down" && canSend?.Invoke() == false) return;
            // Recovery may close the gate while this packet waits for the writer.
            if (generation != Interlocked.Read(ref _readyGeneration)) return;
            if (!IsReady || _stdin is null)
                throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));
            var frame = new
            {
                schema = CoreDeviceTouchProtocol.MessageSchema,
                generation,
                kind = CoreDeviceTouchProtocol.ButtonMessageKind,
                seq = NextSequence(),
                usagePage = (int)usagePage,
                usageCode = (int)usageCode,
                state,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            var header = BitConverter.GetBytes((uint)bytes.Length);

            await _stdin.BaseStream.WriteAsync(header, ct);
            await _stdin.BaseStream.WriteAsync(bytes, ct);
            await _stdin.BaseStream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }


    public async Task SendPasteTextAsync(string text, CancellationToken ct = default,
        Func<bool>? canSend = null)
    {
        var generation = Interlocked.Read(ref _readyGeneration);
        if (!IsReady || _stdin is null)
            throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));
        await _sendLock.WaitAsync(ct);
        try
        {
            if (canSend?.Invoke() == false) return;
            // Recovery may close the gate while this packet waits for the writer.
            if (generation != Interlocked.Read(ref _readyGeneration)) return;
            if (!IsReady || _stdin is null)
                throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));
            var frame = new
            {
                schema = CoreDeviceTouchProtocol.MessageSchema,
                generation,
                kind = CoreDeviceTouchProtocol.PasteTextMessageKind,
                seq = NextSequence(),
                text,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            // Validate the encoded payload before writing even the header;
            // an oversized frame terminates the receiver's input loop.
            if (bytes.Length > CoreDeviceTouchProtocol.MaxFrameSize)
                throw new ArgumentException(LocalizationService.Get("ClipboardTextTooLarge"), nameof(text));
            var header = BitConverter.GetBytes((uint)bytes.Length);
            await _stdin.BaseStream.WriteAsync(header, ct);
            await _stdin.BaseStream.WriteAsync(bytes, ct);
            await _stdin.BaseStream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }

    public async Task SendReadClipboardAsync(CancellationToken ct = default)
    {
        var generation = Interlocked.Read(ref _readyGeneration);
        if (!IsReady || _stdin is null)
            throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));
        await _sendLock.WaitAsync(ct);
        try
        {
            // Recovery may close the gate while this packet waits for the writer.
            if (generation != Interlocked.Read(ref _readyGeneration)) return;
            if (!IsReady || _stdin is null)
                throw new InvalidOperationException(LocalizationService.Get("TouchBridgeNotReady"));
            var frame = new
            {
                schema = CoreDeviceTouchProtocol.MessageSchema,
                generation,
                kind = CoreDeviceTouchProtocol.ReadClipboardMessageKind,
                seq = NextSequence(),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            var header = BitConverter.GetBytes((uint)bytes.Length);
            await _stdin.BaseStream.WriteAsync(header, ct);
            await _stdin.BaseStream.WriteAsync(bytes, ct);
            await _stdin.BaseStream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }


    private async Task DrainErrorAsync(StreamReader reader, int bridgeProcessId)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _lastStandardError = line;
                _lastDiagnostic = line;
                // Preserve transport failures and tracebacks before later cleanup
                // messages overwrite the last-line startup diagnostic.
                DiagnosticLogger.ReverseControl(_requestedWireless ? "wireless" : "usb",
                    "bridge_stderr", ("bridge_pid", bridgeProcessId), ("message", line));
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task HandleProcessExitAsync(Process process)
    {
        int exitCode;
        try { exitCode = process.ExitCode; }
        catch { exitCode = -1; }

        // Process.Exited can run before redirected stdout has delivered its
        // final structured error. Let the reader win before adding a fallback.
        var reader = _readerTask;
        if (reader is not null)
        {
            try { await reader.ConfigureAwait(false); }
            catch { }
        }
        if (Volatile.Read(ref _stopping) != 0 ||
            Interlocked.Exchange(ref _terminalEventReceived, 1) != 0) return;

        IsReady = false;
        var message = LocalizationService.Format("TouchBridgeExitedFormat", exitCode);
        GateOpen = false;
        _lastErrorCode ??= "bridge_exited";
        _readySignal?.TrySetException(CreateStartupException(message));
        OnEvent?.Invoke(new BridgeEvent("error", "bridge_exited", message));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        _sendLock.Dispose();
    }

    private InvalidOperationException CreateStartupException(string fallback,
        string? reportedMessage = null)
    {
        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(reportedMessage) ? fallback : reportedMessage.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(_lastErrorCode))
            parts.Add(LocalizationService.Format("TouchBridgeErrorCodeFormat", _lastErrorCode));
        if (!string.IsNullOrWhiteSpace(_lastStandardError))
            parts.Add(LocalizationService.Format("TouchBridgeDiagnosticFormat", _lastStandardError.Trim()));
        return new InvalidOperationException(LocalizationService.Join(string.Empty, parts));
    }
}

public static class DateTimeOffsetExtensions
{
    public static long ToUnixTimeNanoseconds(this DateTimeOffset dto)
        => dto.ToUnixTimeMilliseconds() * 1_000_000L;
}
