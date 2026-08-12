using System.Diagnostics;
using System.IO;

namespace IPhoneMirror.App.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\IPhoneMirror.App.SingleInstance";
    private const string ShutdownEventName = @"Local\IPhoneMirror.App.SingleInstance.Shutdown";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _shutdownEvent;
    private readonly int _currentProcessId;
    private readonly int _currentSessionId;
    private readonly string _currentProcessName;
    private readonly string? _currentExecutablePath;
    private readonly DateTime _currentStartedAtUtc;
    private RegisteredWaitHandle? _shutdownRegistration;
    private bool _ownsPrimaryInstance;
    private bool _disposed;

    internal SingleInstanceCoordinator()
    {
        using var current = Process.GetCurrentProcess();
        _currentProcessId = current.Id;
        _currentSessionId = current.SessionId;
        _currentProcessName = current.ProcessName;
        _currentExecutablePath = TryGetExecutablePath(current) ??
            NormalizeExecutablePath(Environment.ProcessPath);
        _currentStartedAtUtc = TryGetStartTimeUtc(current) ?? DateTime.UtcNow;
        _mutex = new Mutex(false, MutexName);
        _shutdownEvent = new EventWaitHandle(
            false, EventResetMode.AutoReset, ShutdownEventName);
        _ownsPrimaryInstance = TryAcquireMutex(TimeSpan.Zero);
    }

    internal bool OwnsPrimaryInstance => _ownsPrimaryInstance;

    internal bool HasPreExistingInstance()
    {
        var processes = FindOtherInstances();
        try
        {
            return processes.Any(process =>
            {
                var startedAtUtc = TryGetStartTimeUtc(process);
                return startedAtUtc is null || startedAtUtc < _currentStartedAtUtc;
            });
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    internal void StartShutdownListener(Action shutdownRequested)
    {
        ArgumentNullException.ThrowIfNull(shutdownRequested);
        if (!_ownsPrimaryInstance || _shutdownRegistration is not null) return;

        _shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
            _shutdownEvent,
            (_, timedOut) =>
            {
                if (!timedOut) shutdownRequested();
            },
            null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    internal Task<SingleInstanceCloseResult> CloseOtherInstancesAsync(
        TimeSpan timeout) => Task.Run(() => CloseOtherInstances(timeout));

    internal bool TryAcquirePrimaryInstance(TimeSpan timeout) =>
        TryAcquireMutex(timeout);

    private SingleInstanceCloseResult CloseOtherInstances(TimeSpan timeout)
    {
        ThrowIfDisposed();
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var elapsed = Stopwatch.StartNew();
        var gracefulTimeout = TimeSpan.FromMilliseconds(Math.Min(
            12_000, timeout.TotalMilliseconds * 0.65));
        var processes = FindOtherInstances();
        var closeRequested = new HashSet<int>();

        try
        {
            // A secondary updated instance can request shutdown even while the
            // primary window is loading. A primary closing a legacy instance
            // must not signal its own listener.
            if (!_ownsPrimaryInstance) _shutdownEvent.Set();

            while (elapsed.Elapsed < gracefulTimeout)
            {
                var remaining = 0;
                foreach (var process in processes)
                {
                    var counted = false;
                    try
                    {
                        process.Refresh();
                        if (process.HasExited) continue;
                        ++remaining;
                        counted = true;
                        if (process.MainWindowHandle != 0 &&
                            !closeRequested.Contains(process.Id) &&
                            process.CloseMainWindow())
                            closeRequested.Add(process.Id);
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between Refresh and inspection.
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        if (!counted) ++remaining;
                    }
                }

                if (remaining == 0)
                    return new SingleInstanceCloseResult(true, 0);

                Thread.Sleep(120);
            }

            // Legacy builds do not listen for the named shutdown event and may
            // have no discoverable main window. The explicit user action grants
            // a bounded forced-close fallback after the graceful period.
            foreach (var process in processes)
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (NotSupportedException) { }
            }

            while (elapsed.Elapsed < timeout)
            {
                if (CountRemainingInstances(processes) == 0)
                    return new SingleInstanceCloseResult(true, 0);
                Thread.Sleep(100);
            }

            var remainingCount = CountRemainingInstances(processes);
            return new SingleInstanceCloseResult(
                false, Math.Max(remainingCount, 1));
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static int CountRemainingInstances(IEnumerable<Process> processes) =>
        processes.Count(process =>
        {
            try
            {
                process.Refresh();
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return true;
            }
            catch (NotSupportedException)
            {
                return true;
            }
        });

    private List<Process> FindOtherInstances()
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName(_currentProcessName))
        {
            if (process.Id != _currentProcessId &&
                TryGetSessionId(process) == _currentSessionId &&
                IsSameExecutable(_currentExecutablePath, TryGetExecutablePath(process)))
            {
                matches.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }
        return matches;
    }

    internal static bool IsSameExecutable(string? expected, string? candidate)
    {
        var normalizedExpected = NormalizeExecutablePath(expected);
        var normalizedCandidate = NormalizeExecutablePath(candidate);
        return normalizedExpected is not null && normalizedCandidate is not null &&
            string.Equals(normalizedExpected, normalizedCandidate,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try { return NormalizeExecutablePath(process.MainModule?.FileName); }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static string? NormalizeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or
                                           NotSupportedException or
                                           PathTooLongException)
        {
            return null;
        }
    }

    private static int TryGetSessionId(Process process)
    {
        try { return process.SessionId; }
        catch { return -1; }
    }

    private static DateTime? TryGetStartTimeUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    private bool TryAcquireMutex(TimeSpan timeout)
    {
        if (_ownsPrimaryInstance) return true;
        try
        {
            _ownsPrimaryInstance = _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            _ownsPrimaryInstance = true;
        }
        return _ownsPrimaryInstance;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdownRegistration?.Unregister(null);
        _shutdownRegistration = null;
        _shutdownEvent.Dispose();
        if (_ownsPrimaryInstance)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _ownsPrimaryInstance = false;
        }
        _mutex.Dispose();
    }
}

internal readonly record struct SingleInstanceCloseResult(
    bool Succeeded, int RemainingInstanceCount);
