using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace IPhoneMirror.App.Services;

// Keep camera delivery precise while Windows occludes/minimizes the preview.
internal sealed class CameraFrameTimer : IDisposable
{
    private readonly EventWaitHandle _wait = new(false, EventResetMode.AutoReset);
    private readonly RegisteredWaitHandle _registration;
    private readonly Channel<bool> _ticks = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    internal CameraFrameTimer(TimeSpan interval)
    {
        var timer = CreateWaitableTimerExW(0, null, 0x2, 0x1F0003);
        if (timer.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            timer.Dispose();
            _wait.Dispose();
            throw new Win32Exception(error);
        }
        _wait.SafeWaitHandle.Dispose();
        _wait.SafeWaitHandle = timer;
        var due = -interval.Ticks;
        if (!SetWaitableTimer(timer, ref due, Math.Max(1, (int)Math.Round(interval.TotalMilliseconds)), 0, 0, false))
        {
            var error = Marshal.GetLastWin32Error();
            _wait.Dispose();
            throw new Win32Exception(error);
        }
        _registration = ThreadPool.RegisterWaitForSingleObject(_wait,
            (_, _) => _ticks.Writer.TryWrite(true), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    internal ValueTask<bool> WaitForNextTickAsync(CancellationToken token) =>
        _ticks.Reader.ReadAsync(token);

    public void Dispose()
    {
        _registration.Unregister(null);
        CancelWaitableTimer(_wait.SafeWaitHandle);
        _wait.Dispose();
        _ticks.Writer.TryComplete();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes,
        string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due,
        int period, nint callback, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
