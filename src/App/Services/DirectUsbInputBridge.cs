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

public sealed record BridgeEvent(string Event, string? Code, string? Message);

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
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _sequence;
    private int _stopping;
    private int _terminalEventReceived;

    public bool IsReady { get; private set; }
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
                $"自研 USB 触控桥接器运行时不完整：{runtimeFailure}。请重新安装完整测试包。");
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
            // Never package Apple DDI payloads. An operator can place an
            // officially obtained, device-compatible bundle in this user-data
            // location or select a different one through the environment.
            psi.ArgumentList.Add("--ddi-dir");
            psi.ArgumentList.Add(ddiDirectory);
        }

        var startedProcess = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 USB 触控桥接器。");
        _process = startedProcess;
        _stdin = startedProcess.StandardInput;
        _stdout = startedProcess.StandardOutput;

        _readySignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        _errorDrainTask = Task.Run(() => DrainErrorAsync(startedProcess.StandardError));
        startedProcess.EnableRaisingEvents = true;
        startedProcess.Exited += (_, _) => _ = HandleProcessExitAsync(startedProcess);
        if (startedProcess.HasExited)
            _ = HandleProcessExitAsync(startedProcess);

        await WaitForReadyAsync(ct);
    }

    private static string? GetPersonalizedDdiDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("IPHONE_MIRROR_DDI_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var defaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "iPhoneMirror", "developer-image");
        return HasCompletePersonalizedDdiBundle(defaultDirectory) ? defaultDirectory : null;
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

    public async Task SendTouchBatchAsync(IReadOnlyList<TouchPoint> points, long timestampNs, long sequence, CancellationToken ct = default)
    {
        if (!IsReady || _stdin is null)
            throw new InvalidOperationException("USB 触控桥接器尚未就绪。");

        if (points.Count == 0 || points.Count > CoreDeviceTouchProtocol.MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(points), "触控批次必须包含 1 到 5 个触点。");
        foreach (var point in points)
        {
            if (point.PointerId < 0)
                throw new ArgumentOutOfRangeException(nameof(points), "触点编号必须为非负整数。");
            if (!CoreDeviceTouchProtocol.IsNormalizedCoordinate(point.NormalizedX) ||
                !CoreDeviceTouchProtocol.IsNormalizedCoordinate(point.NormalizedY))
                throw new ArgumentOutOfRangeException(nameof(points), "触点坐标必须是 0 到 1 之间的有限数值。");
        }
        var json = JsonSerializer.Serialize(new
            {
                schema = CoreDeviceTouchProtocol.MessageSchema,
                kind = CoreDeviceTouchProtocol.MessageKind,
                seq = sequence,
                timestampNs,
                points,
            });
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes((uint)bytes.Length);

        await _sendLock.WaitAsync(ct);
        try
        {
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
        try { _stdin?.Close(); } catch { }
        if (_process is { HasExited: false })
        {
            try { _process.Kill(); } catch { }
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
            if (!IsReady)
                _readySignal?.TrySetException(new InvalidOperationException(
                    "USB 触控桥接器在就绪前关闭了输出通道。"));
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
                    if (string.Equals(statusCode, "terminated", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Exchange(ref _terminalEventReceived, 1);
                        IsReady = false;
                    }
                    OnEvent?.Invoke(new BridgeEvent("status",
                        statusCode, null));
                    break;
                case "ready":
                    Udid = root.TryGetProperty("udid", out var u) ? u.GetString() : null;
                    var transport = root.TryGetProperty("transport", out var t) ? t.GetString() : null;
                    if (!string.Equals(Udid, _requestedUdid, StringComparison.OrdinalIgnoreCase))
                    {
                        _readySignal?.TrySetException(new InvalidOperationException($"反控桥接目标不匹配：请求 {_requestedUdid}，实际 {Udid ?? "未知"}。"));
                        return;
                    }
                    var expectedTransport = _requestedWireless ? "wireless" : "usb";
                    if (!string.Equals(transport, expectedTransport,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _readySignal?.TrySetException(new InvalidOperationException($"反控桥接传输类型不匹配：请求 {expectedTransport}，实际 {transport}。"));
                        return;
                    }
                    RateHz = root.TryGetProperty("rateHz", out var r) ? r.GetInt32() : 0;
                    GateOpen = root.TryGetProperty("gateOpen", out var g) && g.GetBoolean();
                    AuthMode = root.TryGetProperty("authMode", out var am)
                        ? am.GetString() : null;
                    if (!GateOpen)
                    {
                        IsReady = false;
                        _lastErrorCode = "remote_control_gate_unavailable";
                        const string gateMessage =
                            "设备没有确认触控认证 gate 已打开，无法安全地开始反控。";
                        OnEvent?.Invoke(new BridgeEvent("error", _lastErrorCode, gateMessage));
                        _readySignal?.TrySetException(CreateStartupException(gateMessage));
                        return;
                    }
                    IsReady = true;
                    OnEvent?.Invoke(new BridgeEvent("ready", null, "gate_open"));
                    _readySignal?.TrySetResult(true);
                    break;
                case "warning":
                    OnEvent?.Invoke(new BridgeEvent("warning",
                        root.TryGetProperty("code", out var wc) ? wc.GetString() : null,
                        root.TryGetProperty("message", out var m) ? m.GetString() : null));
                    break;
                case "error":
                    Interlocked.Exchange(ref _terminalEventReceived, 1);
                    _lastErrorCode = root.TryGetProperty("code", out var ec)
                        ? ec.GetString() : null;
                    var message = root.TryGetProperty("message", out var em)
                        ? em.GetString() : null;
                    OnEvent?.Invoke(new BridgeEvent("error",
                        _lastErrorCode, message));
                    IsReady = false;
                    _readySignal?.TrySetException(CreateStartupException(
                        "USB 触控桥接器报告错误。", message));
                    break;
            }
        }
        catch
        {
            _readySignal?.TrySetException(CreateStartupException(
                "USB 触控桥接器输出格式无效。"));
        }
    }

    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        var readySignal = _readySignal ?? throw new InvalidOperationException("USB 触控桥接器未启动。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InitialReadyTimeout);
        try { await readySignal.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"USB 触控桥接器在 {InitialReadyTimeout.TotalSeconds:0} 秒内未就绪。{(_lastDiagnostic is null ? string.Empty : $" 最近信息：{_lastDiagnostic}")}");
        }
    }

    public async Task SendKeyboardAsync(IReadOnlyCollection<byte> usages,
        CancellationToken ct = default)
    {
        if (!IsReady || _stdin is null)
            throw new InvalidOperationException("USB 触控桥接器尚未就绪。");
        if (usages.Count > CoreDeviceTouchProtocol.MaxKeyboardUsages)
            throw new ArgumentOutOfRangeException(nameof(usages));
        // System.Text.Json encodes byte[] as a base64 string. The bridge
        // protocol requires a JSON numeric array (for example [4] or []), so
        // widen the usages before serialization instead of passing byte[].
        var normalized = usages.Distinct().OrderBy(value => value)
            .Select(value => (int)value).ToArray();
        var frame = new
        {
            schema = CoreDeviceTouchProtocol.MessageSchema,
            kind = CoreDeviceTouchProtocol.KeyboardMessageKind,
            seq = NextSequence(),
            timestampNs = DateTimeOffset.UtcNow.ToUnixTimeNanoseconds(),
            usages = normalized,
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame));
        var header = BitConverter.GetBytes((uint)bytes.Length);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _stdin.BaseStream.WriteAsync(header, ct);
            await _stdin.BaseStream.WriteAsync(bytes, ct);
            await _stdin.BaseStream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }

    public async Task SendButtonAsync(ushort usagePage, ushort usageCode,
        string state, CancellationToken ct = default)
    {
        if (!IsReady || _stdin is null)
            throw new InvalidOperationException("USB 触控桥接器尚未就绪。");
        if (state is not ("down" or "up" or "canceled"))
            throw new ArgumentOutOfRangeException(nameof(state));
        var frame = new
        {
            schema = CoreDeviceTouchProtocol.MessageSchema,
            kind = CoreDeviceTouchProtocol.ButtonMessageKind,
            seq = NextSequence(),
            usagePage = (int)usagePage,
            usageCode = (int)usageCode,
            state,
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame));
        var header = BitConverter.GetBytes((uint)bytes.Length);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _stdin.BaseStream.WriteAsync(header, ct);
            await _stdin.BaseStream.WriteAsync(bytes, ct);
            await _stdin.BaseStream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }

    private async Task DrainErrorAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _lastStandardError = line;
                _lastDiagnostic = line;
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
        var message = $"USB 触控桥接器意外退出（代码 {exitCode}）。";
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
        var message = string.IsNullOrWhiteSpace(reportedMessage)
            ? fallback : reportedMessage.Trim();
        if (!string.IsNullOrWhiteSpace(_lastErrorCode))
            message += $"（错误代码：{_lastErrorCode}）";
        if (!string.IsNullOrWhiteSpace(_lastStandardError))
            message += $" 最近诊断：{_lastStandardError.Trim()}";
        return new InvalidOperationException(message);
    }
}

public static class DateTimeOffsetExtensions
{
    public static long ToUnixTimeNanoseconds(this DateTimeOffset dto)
        => dto.ToUnixTimeMilliseconds() * 1_000_000L;
}
