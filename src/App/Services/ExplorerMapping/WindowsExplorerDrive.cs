using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace IPhoneMirror.App.Services.ExplorerMapping;

internal sealed record ExplorerDriveAvailability(bool ServiceExists, bool ServiceRunning,
    bool ServiceDisabled, long DownloadLimitBytes, int ErrorCode)
{
    internal bool CanTryConnect => ServiceExists && !ServiceDisabled;
}

// A temporary mapping in the interactive user's logon session. The random
// remote endpoint is its ownership token; a drive letter alone never is.
internal sealed class WindowsExplorerDrive : IAsyncDisposable
{
    private const int NotConnected = 2250;
    private const int AlreadyAssigned = 85;
    private const int DeviceRemembered = 1202;
    private const uint ConnectTemporary = 4;
    private static readonly SemaphoreSlim MappingGate = new(1, 1);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(8);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    private WindowsExplorerDrive(string driveName, string uncPath)
    {
        DriveName = driveName;
        UncPath = uncPath;
    }

    internal string DriveName { get; }
    internal string RootPath => DriveName + "\\";
    // Do not write this capability-bearing value to ordinary diagnostic logs.
    internal string UncPath { get; }

    internal static ExplorerDriveAvailability GetAvailability()
    {
        var downloadLimit = GetDownloadLimit();
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) return new(false, false, false, downloadLimit, Marshal.GetLastWin32Error());
        using var service = OpenServiceW(manager, "WebClient", 1 | 4);
        if (service.IsInvalid) return new(false, false, false, downloadLimit, Marshal.GetLastWin32Error());
        if (!QueryServiceStatus(service, out var status))
            return new(true, false, false, downloadLimit, Marshal.GetLastWin32Error());
        _ = QueryServiceConfigW(service, 0, 0, out var required);
        if (required == 0 || required > 64 * 1024)
            return new(true, status.CurrentState == 4, false, downloadLimit, Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfigW(service, buffer, required, out _))
                return new(true, status.CurrentState == 4, false, downloadLimit, Marshal.GetLastWin32Error());
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            return new(true, status.CurrentState == 4, config.StartType == 4, downloadLimit, 0);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static long GetDownloadLimit()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WebClient\Parameters",
                "FileSizeLimitInBytes", null);
            return value switch
            {
                int number => unchecked((uint)number),
                long number when number >= 0 => number,
                _ => 50_000_000,
            };
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return 50_000_000; }
    }

    internal static async Task<WindowsExplorerDrive> ConnectAsync(Uri endpoint,
        CancellationToken cancellation = default)
    {
        var uncPath = ToUncPath(endpoint);
        cancellation.ThrowIfCancellationRequested();
        var availability = GetAvailability();
        if (!availability.CanTryConnect)
            throw new Win32Exception(availability.ErrorCode != 0 ? availability.ErrorCode : 1058,
                "The Windows WebClient service is unavailable or disabled.");
        // WNet can trigger an installed, manual-start redirector. Do not change
        // service configuration, start it with elevation, or alter its limits.
        var pending = Task.Run(() => ConnectCore(uncPath, cancellation), CancellationToken.None);
        try { return await pending.WaitAsync(ConnectTimeout, cancellation).ConfigureAwait(false); }
        catch
        {
            // Native WNet has no cancellable async API. If it succeeds after
            // the caller times out/cancels, remove that exact late connection.
            _ = RetireLateConnectionAsync(pending);
            throw;
        }
    }

    private static async Task RetireLateConnectionAsync(Task<WindowsExplorerDrive> pending)
    {
        try
        {
            var late = await pending.ConfigureAwait(false);
            await late.DisposeAsync().ConfigureAwait(false);
        }
        catch { /* The initial caller already receives the bounded failure. */ }
    }

    private static WindowsExplorerDrive ConnectCore(string uncPath, CancellationToken cancellation)
    {
        MappingGate.Wait(cancellation);
        try
        {
            var used = GetLogicalDrives();
            if (used == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (var letter = 'Z'; letter >= 'T'; letter--)
            {
                cancellation.ThrowIfCancellationRequested();
                var drive = letter + ":";
                if ((used & (1u << (letter - 'A'))) != 0 || QueryRemote(drive).Error != NotConnected) continue;
                var resource = new NetResource { Type = 1, LocalName = drive, RemoteName = uncPath };
                var error = WNetAddConnection2W(ref resource, "", "", ConnectTemporary);
                if (error is AlreadyAssigned or DeviceRemembered) continue;
                if (error != 0) throw new Win32Exception(error, "Windows could not map the phone file service.");
                var mapped = new WindowsExplorerDrive(drive, uncPath);
                try
                {
                    var actual = QueryRemote(drive);
                    if (actual.Error != 0 || !SameRemote(actual.Remote, uncPath))
                        throw new IOException("Windows did not confirm the new phone drive.");
                    // Confirm that Windows' real filesystem redirector can open
                    // the mapping, rather than reporting only WNet registration.
                    using var entries = Directory.EnumerateFileSystemEntries(mapped.RootPath).GetEnumerator();
                    _ = entries.MoveNext();
                    cancellation.ThrowIfCancellationRequested();
                    return mapped;
                }
                catch
                {
                    mapped.DisconnectCore();
                    throw;
                }
            }
            throw new IOException("No free drive letter is available between T: and Z:.");
        }
        finally { MappingGate.Release(); }
    }

    internal static string ToUncPath(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" ||
            endpoint.Port is <= 0 or > 65535 || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("An IPv4 loopback WebDAV endpoint is required.", nameof(endpoint));
        var path = endpoint.GetComponents(UriComponents.Path, UriFormat.Unescaped).Trim('/');
        if (path.Length == 0 || path.Contains('\\') || path.Any(char.IsControl) ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("The mapping endpoint must include its session path.", nameof(endpoint));
        return $@"\\127.0.0.1@{endpoint.Port}\DavWWWRoot\{path.Replace('/', '\\')}";
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                _disposeTask = Task.Run(() =>
                {
                    MappingGate.Wait();
                    try { DisconnectCore(); }
                    finally { MappingGate.Release(); }
                });
                // A bounded caller may already have left when a native
                // disconnect finally fails. Keep that late fault observed.
                _ = _disposeTask.ContinueWith(completed => _ = completed.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            task = _disposeTask;
        }
        return new ValueTask(task.WaitAsync(DisconnectTimeout));
    }

    private void DisconnectCore()
    {
        var current = QueryRemote(DriveName);
        if (current.Error == NotConnected || (current.Error == 0 && !SameRemote(current.Remote, UncPath))) return;
        if (current.Error != 0) throw new Win32Exception(current.Error, "Windows could not inspect the phone mapping before disconnecting.");
        // WNet's remote-name overload only removes deviceless connections; the
        // redirected letter must be removed by local name. Check its exact
        // random endpoint immediately before the call, including late cleanup,
        // to avoid removing an already-reused letter. WNet has no atomic
        // compare-and-disconnect operation against another process's remap.
        var error = WNetCancelConnection2W(DriveName, 0, true);
        if (error is not (0 or NotConnected))
            throw new Win32Exception(error, "Windows could not disconnect the phone mapping.");
    }

    private static bool SameRemote(string? left, string right) =>
        string.Equals(left?.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static (int Error, string? Remote) QueryRemote(string drive)
    {
        var capacity = 512;
        var remote = new StringBuilder(capacity);
        var error = WNetGetConnectionW(drive, remote, ref capacity);
        if (error == 234 && capacity is > 0 and <= 32768)
        {
            remote = new StringBuilder(capacity);
            error = WNetGetConnectionW(drive, remote, ref capacity);
        }
        return (error, error == 0 ? remote.ToString() : null);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        internal uint Scope, Type, DisplayType, Usage;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? LocalName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? RemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Comment;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Provider;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    { internal uint Type, CurrentState, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        internal uint Type, StartType, ErrorControl;
        internal nint BinaryPath, LoadOrderGroup;
        internal uint TagId;
        internal nint Dependencies, StartName, DisplayName;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2W(ref NetResource resource, string password, string username, uint flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2W(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetLogicalDrives();
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfigW(ServiceHandle service, nint config, uint size, out uint required);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint service);
}
