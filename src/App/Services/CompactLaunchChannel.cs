using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace IPhoneMirror.App.Services;

// Local-session, current-user-only IPC. A shortcut reuses an existing app;
// it never requests that another instance or capture session be terminated.
internal sealed class CompactLaunchChannel : IDisposable
{
    private static string DefaultPipeName
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return $"iPhoneMirror.CompactLaunch.{process.SessionId}";
        }
    }
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _listener;
    private int _disposed;

    internal CompactLaunchChannel(Action<CompactLaunchOptions> receive, string? pipeName = null)
    {
        _listener = ListenAsync(receive, pipeName ?? DefaultPipeName);
    }

    private async Task ListenAsync(Action<CompactLaunchOptions> receive, string name)
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > 4096) continue;
                var data = new byte[length];
                await pipe.ReadExactlyAsync(data, timeout.Token).ConfigureAwait(false);
                var options = JsonSerializer.Deserialize<CompactLaunchOptions>(data);
                if (options is not { Enabled: true } || options.DeviceId?.Length > 512 ||
                    options.DeviceId?.Any(char.IsControl) == true) continue;
                receive(options);
                await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { break; }
            catch (Exception error) when (error is System.IO.IOException or OperationCanceledException or JsonException)
            {
                // A malformed or abandoned request must not stop the listener.
                DiagnosticLogger.Exception("startup", "compact_launch_request_failed", error);
                // Back off if pipe creation itself fails (for example, an
                // instance is exiting). Never spin on a persistent OS error.
                try { await Task.Delay(100, _cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception error)
            {
                DiagnosticLogger.Exception("startup", "compact_launch_listener_failed", error);
                break;
            }
        }
    }

    internal static async Task<bool> ForwardAsync(CompactLaunchOptions options, string? pipeName = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToUtf8Bytes(options);
            if (data.Length > 4096) return false;
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
            await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(data, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            var response = new byte[1];
            await pipe.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            return response[0] == 1;
        }
        catch (Exception error) when (error is System.IO.IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel();
        _ = _listener.ContinueWith(completed =>
        {
            if (completed.Exception is { } error)
                DiagnosticLogger.Exception("startup", "compact_launch_listener_failed", error);
            _cancellation.Dispose();
        }, TaskScheduler.Default);
    }
}
