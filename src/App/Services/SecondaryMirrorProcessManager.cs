using System.Diagnostics;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Services;

internal sealed class SecondaryMirrorProcessManager : IDisposable
{
    private readonly Dictionary<string, Process> _processes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    internal (bool Success, string Message) Show(DeviceViewModel device)
    {
        Process? existing;
        lock (_gate)
            if (_processes.TryGetValue(device.Udid, out existing) && !existing.HasExited)
                return (true, string.Empty);
        try
        {
            existing?.Dispose();
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--secondary-mirror");
            startInfo.ArgumentList.Add(SecondaryMirrorMode.Encode(device));
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process creation failed.");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                lock (_gate) _processes.Remove(device.Udid);
            };
            lock (_gate) _processes[device.Udid] = process;
            return (true, string.Empty);
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
    }

    public void Dispose()
    {
        Process[] processes;
        lock (_gate)
        {
            processes = _processes.Values.ToArray();
            _processes.Clear();
        }
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    _ = process.CloseMainWindow();
                    if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            process.Dispose();
        }
    }
}
