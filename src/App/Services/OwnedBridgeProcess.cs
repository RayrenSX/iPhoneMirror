using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IPhoneMirror.App.Services;

/// <summary>Owns redirected bridge I/O and every descendant from before execution starts.</summary>
internal sealed class OwnedBridgeProcess : IDisposable
{
    private readonly KernelHandle _job;
    private int _disposed;
    internal Process Process { get; }
    internal StreamWriter Input { get; }
    internal StreamReader Output { get; }
    internal StreamReader Error { get; }
    internal bool WasForced { get; private set; }
    private readonly SemaphoreSlim _retire = new(1);

    private OwnedBridgeProcess(KernelHandle job, Process process,
        SafeFileHandle input, SafeFileHandle output, SafeFileHandle error,
        Encoding outputEncoding, Encoding errorEncoding)
    {
        _job = job;
        Process = process;
        Input = new StreamWriter(new FileStream(input, FileAccess.Write),
            new UTF8Encoding(false)) { AutoFlush = true };
        Output = new StreamReader(new FileStream(output, FileAccess.Read), outputEncoding);
        Error = new StreamReader(new FileStream(error, FileAccess.Read), errorEncoding);
    }

    internal static OwnedBridgeProcess Start(ProcessStartInfo info)
    {
        if (info.UseShellExecute || !info.RedirectStandardInput ||
            !info.RedirectStandardOutput || !info.RedirectStandardError)
            throw new ArgumentException("Owned bridge requires all three redirected streams.", nameof(info));
        using var pipes = new PipeSet();
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw LastError("Create bridge job");
        Process? process = null;
        ProcessInformation native = default;
        OwnedBridgeProcess? owner = null;
        var resumed = false;
        try
        {
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw LastError("Configure bridge job");
            using var attributes = new InheritedHandles(pipes.ChildHandles);
            var startup = new StartupInfoEx();
            startup.Info.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.Info.Flags = 0x100; // STARTF_USESTDHANDLES
            startup.Info.Input = pipes.InputRead.DangerousGetHandle();
            startup.Info.Output = pipes.OutputWrite.DangerousGetHandle();
            startup.Info.Error = pipes.ErrorWrite.DangerousGetHandle();
            startup.Attributes = attributes.List;
            var executable = ResolveExecutable(info.FileName);
            var command = new StringBuilder(string.Join(' ',
                new[] { executable }.Concat(info.ArgumentList).Select(QuoteArgument)));
            var environment = string.Join('\0', info.Environment
                .Where(pair => pair.Value is not null)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
            var environmentPtr = Marshal.StringToHGlobalUni(environment);
            try
            {
                const uint flags = 0x08000000 | 0x00080000 | 0x00000400 | 0x00000004;
                // Suspend before assigning the job: an interpreter/bootloader
                // must never be able to fork between Start and Assign.
                if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero,
                        true, flags, environmentPtr, info.WorkingDirectory,
                        ref startup, out native))
                    throw LastError("Start bridge process");
            }
            finally { Marshal.FreeHGlobal(environmentPtr); }
            if (!AssignProcessToJobObject(job, native.Process))
                throw LastError("Assign bridge process to job");
            process = Process.GetProcessById((int)native.ProcessId);
            _ = process.Handle; // Retain this exact suspended process, avoiding PID reuse.
            owner = new OwnedBridgeProcess(job, process, pipes.InputWrite,
                pipes.OutputRead, pipes.ErrorRead,
                info.StandardOutputEncoding ?? Encoding.UTF8,
                info.StandardErrorEncoding ?? Encoding.UTF8);
            if (ResumeThread(native.Thread) == uint.MaxValue)
                throw LastError("Resume bridge process");
            resumed = true;
            pipes.TransferParentHandles();
            return owner;
        }
        finally
        {
            if (!resumed)
            {
                if (native.Process != IntPtr.Zero) TerminateProcess(native.Process, 1);
                owner?.Dispose();
                process?.Dispose();
                job.Dispose();
            }
            if (native.Thread != IntPtr.Zero) CloseHandle(native.Thread);
            if (native.Process != IntPtr.Zero) CloseHandle(native.Process);
        }
    }

    internal uint ActiveProcesses
    {
        get
        {
            if (!QueryInformationJobObject(_job, 1, out var info,
                    (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw LastError("Query bridge job");
            return info.ActiveProcesses;
        }
    }

    internal async Task RetireAsync(TimeSpan grace)
    {
        // The launcher may have exited while an interpreter still owns USB.
        // Query the job, never infer completion from the launcher's exit alone.
        await _retire.WaitAsync().ConfigureAwait(false);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (ActiveProcesses != 0 && deadline.Elapsed < grace)
                await Task.Delay(20).ConfigureAwait(false);
            var active = ActiveProcesses;
            if (active == 0) return;
            WasForced = true;
            DiagnosticLogger.Warning("reverse_control", "bridge_owned_processes_terminated",
                ("bridge_pid", Process.Id), ("active_processes", active));
            if (!TerminateJobObject(_job, 1)) throw LastError("Terminate bridge job");
            deadline.Restart();
            while (ActiveProcesses != 0)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(3))
                    throw new TimeoutException("Bridge descendants did not exit after termination.");
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
        finally { _retire.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Closing the last job handle also retires children if startup failed
        // or the application could not complete its normal graceful shutdown.
        _job.Dispose();
        try { Input.Dispose(); }
        finally
        {
            try { Output.Dispose(); }
            finally
            {
                try { Error.Dispose(); }
                finally { Process.Dispose(); }
            }
        }
    }

    private static string ResolveExecutable(string name)
    {
        if (Path.IsPathFullyQualified(name)) return name;
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(name);
        var result = new StringBuilder(32768);
        if (SearchPathW(null, name, ".exe", result.Capacity, result, IntPtr.Zero) == 0)
            throw LastError("Find bridge executable");
        return result.ToString();
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static Win32Exception LastError(string operation) =>
        new(Marshal.GetLastWin32Error(), operation);

    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class PipeSet : IDisposable
    {
        internal SafeFileHandle InputRead, InputWrite, OutputRead, OutputWrite, ErrorRead, ErrorWrite;
        private bool _parentsTransferred;
        internal IntPtr[] ChildHandles => [InputRead.DangerousGetHandle(),
            OutputWrite.DangerousGetHandle(), ErrorWrite.DangerousGetHandle()];
        internal PipeSet()
        {
            InputRead = InputWrite = OutputRead = OutputWrite = ErrorRead = ErrorWrite = null!;
            try
            {
                var attributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
                if (!CreatePipe(out InputRead, out InputWrite, ref attributes, 0) ||
                    !CreatePipe(out OutputRead, out OutputWrite, ref attributes, 0) ||
                    !CreatePipe(out ErrorRead, out ErrorWrite, ref attributes, 0))
                    throw LastError("Create bridge pipe");
                foreach (var parent in new[] { InputWrite, OutputRead, ErrorRead })
                    if (!SetHandleInformation(parent, 1, 0)) throw LastError("Protect bridge pipe inheritance");
            }
            catch { Dispose(); throw; }
        }
        internal void TransferParentHandles() => _parentsTransferred = true;
        public void Dispose()
        {
            InputRead?.Dispose(); OutputWrite?.Dispose(); ErrorWrite?.Dispose();
            if (!_parentsTransferred)
            { InputWrite?.Dispose(); OutputRead?.Dispose(); ErrorRead?.Dispose(); }
        }
    }

    private sealed class InheritedHandles : IDisposable
    {
        internal IntPtr List { get; private set; }
        private IntPtr _handles;
        private bool _initialized;
        internal InheritedHandles(IntPtr[] handles)
        {
            nuint bytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            try
            {
                List = Marshal.AllocHGlobal(checked((int)bytes));
                if (!InitializeProcThreadAttributeList(List, 1, 0, ref bytes))
                    throw LastError("Initialize bridge launch attributes");
                _initialized = true;
                _handles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                Marshal.Copy(handles, 0, _handles, handles.Length);
                if (!UpdateProcThreadAttribute(List, 0, 0x20002,
                        _handles, (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw LastError("Restrict bridge inherited handles");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_initialized) DeleteProcThreadAttributeList(List);
            if (List != IntPtr.Zero) Marshal.FreeHGlobal(List);
            if (_handles != IntPtr.Zero) Marshal.FreeHGlobal(_handles);
            List = _handles = IntPtr.Zero;
            _initialized = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { internal int Size; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        internal int Size; internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        internal ushort Show, ReservedSize; internal IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx
    { internal StartupInfo Info; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
    { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        internal long ProcessTime, JobTime; internal uint LimitFlags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet; internal uint ActiveProcessLimit;
        internal nuint Affinity; internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { internal BasicLimits Basic; internal IoCounters Io; internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    {
        internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern KernelHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(KernelHandle job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(KernelHandle job, int kind, out Accounting info, uint size, IntPtr returned);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(KernelHandle job, IntPtr process);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(KernelHandle job, uint code);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint SearchPathW(string? path, string file, string extension, int length, StringBuilder result, IntPtr lastPart);
}
