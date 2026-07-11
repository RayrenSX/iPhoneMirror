using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IPhoneMirror.App.Interop;
using Microsoft.Win32;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Privileged, non-UI entry point hosted by iPhoneMirror.exe itself.  Keeping
/// the elevation boundary in compiled code avoids running a mutable PowerShell
/// script as administrator and makes this executable the process shown by UAC.
/// A production installer should additionally Authenticode-sign the executable.
/// </summary>
internal static partial class DriverHelperMode
{
    internal const string InstallSwitch = "--driver-helper-install";

    private const string InstallerHash =
        "DF2ABF387893332F28C4DF68B10A6B176DC9706142055DCCCCF447F5A9CEDE2D";
    private const string DriverHash =
        "8058F2AFE6EF96A7D2DED432997FD8655970C9EA75A938EE4557D6A2CB4CC989";
    private const string Dll64Hash =
        "4F18B5D2C28AA66B648C8683C6D09B52B92CBBEE85984BBEFAD5F38A64BC2A14";
    private const string Dll32Hash =
        "00CACA07869B19D10B370552AC7CC2F6F2EE246FC15DB11650F6CD3F4EF9B666";

    private const uint CrSuccess = 0;
    private const uint CrNoSuchDevNode = 0x0000000D;

    private static readonly string PackageRoot = Path.Combine(
        AppContext.BaseDirectory, "Drivers", "libusb-win32-1.2.6.0");
    private static readonly string OperationsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "iPhoneMirror", "DriverOperations");
    private static readonly string BackupsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "iPhoneMirror", "DriverBackups");

    [GeneratedRegex(@"^USB\\VID_05AC&PID_[0-9A-Fa-f]{4}\\[A-Za-z0-9]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AppleParentPattern();

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && string.Equals(arguments[0], InstallSwitch,
            StringComparison.Ordinal);

    internal static (string ResultPath, string LogPath) GetOperationPaths(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out _))
            throw new ArgumentException("Invalid driver operation ID.", nameof(operationId));
        var directory = Path.Combine(OperationsRoot, operationId);
        return (Path.Combine(directory, "result.json"), Path.Combine(directory, "install.log"));
    }

    internal static bool IsDevicePresent(string instanceId) =>
        TryGetDeviceStatus(instanceId, out _, out _);

    internal static int Run(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 4 || !IsRequested(arguments)) return 2;

        var instanceId = arguments[1];
        var expectedSerial = IPhoneFilterDriverService.NormalizeSerial(arguments[2]);
        var operationId = arguments[3];
        if (!Guid.TryParseExact(operationId, "N", out _) ||
            !AppleParentPattern().IsMatch(instanceId) ||
            instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) ||
            expectedSerial.Length == 0)
            return 2;

        var paths = GetOperationPaths(operationId);
        try
        {
            PrepareControlledDirectory(Path.GetDirectoryName(paths.ResultPath)!);
            PrepareControlledDirectory(BackupsRoot);
        }
        catch
        {
            return 3;
        }

        var log = new OperationLog(paths.LogPath);
        FilterSnapshot[]? snapshot = null;
        string? backupPath = null;
        bool? serviceExistedBefore = null;
        var createdSystemFiles = new List<string>();
        try
        {
            if (!IsAdministrator()) throw new InvalidOperationException(
                "The driver helper was not elevated.");

            using var mutex = new Mutex(false, @"Global\iPhoneMirror.DriverInstall");
            var lockTaken = false;
            try
            {
                try { lockTaken = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { lockTaken = true; }
                if (!lockTaken) throw new InvalidOperationException(
                    "Another iPhone driver installation is already running.");

                ValidateTarget(instanceId, expectedSerial);
                ValidatePackage();

                var serviceExisted = ServiceExists();
                serviceExistedBefore = serviceExisted;
                if (serviceExisted) ValidateInstalledStack();

                snapshot = CaptureSnapshot(instanceId);
                backupPath = Path.Combine(BackupsRoot,
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{expectedSerial}-{operationId}.json");
                File.WriteAllText(backupPath, JsonSerializer.Serialize(new
                {
                    CreatedUtc = DateTime.UtcNow,
                    TargetInstanceId = instanceId,
                    ServiceExisted = serviceExisted,
                    Devices = snapshot,
                }, JsonOptions), Encoding.UTF8);
                log.Write($"Saved exact-device rollback snapshot: {backupPath}");

                if (!serviceExisted)
                {
                    BootstrapSystemFiles(createdSystemFiles);
                    log.Write("Installed verified libusb0 system files for the first-time service bootstrap.");
                }

                var installer = Path.Combine(PackageRoot, "amd64", "install-filter.exe");
                var installerResult = RunProcess(installer,
                    ["i", "-di=" + instanceId], TimeSpan.FromMinutes(2));
                log.Write($"install-filter exit={installerResult.ExitCode}");
                foreach (var line in installerResult.OutputLines)
                    log.Write("install-filter: " + line);
                if (installerResult.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"Exact-instance filter installer failed with code {installerResult.ExitCode}.");

                var pnpOk = WaitForHealthyTarget(instanceId, TimeSpan.FromSeconds(20));
                VerifySnapshotAndFilter(instanceId, snapshot);
                ValidateInstalledStack();

                var exactBackendAvailable = false;
                try
                {
                    using var core = new NativeCore();
                    exactBackendAvailable = core.IsLibUsb0DeviceAvailable(expectedSerial);
                }
                catch (Exception error)
                {
                    log.Write("Exact native readiness probe deferred: " + error.Message);
                }

                var requiresReplug = !pnpOk || !exactBackendAvailable;
                var message = requiresReplug
                    ? "The filter is installed. Unlock, unplug and reconnect this iPhone once."
                    : "The capture filter is installed and ready for this iPhone.";
                log.Write(message);
                WriteResult(paths.ResultPath, new HelperResult(true, requiresReplug,
                    message, instanceId, ReadInstalledVersion(), paths.LogPath, backupPath));
                return 0;
            }
            finally
            {
                if (lockTaken) mutex.ReleaseMutex();
            }
        }
        catch (Exception error)
        {
            log.Write("ERROR: " + error);
            var rollbackComplete = true;
            if (snapshot is not null)
            {
                foreach (var entry in snapshot)
                {
                    try { RestoreSnapshot(entry); }
                    catch (Exception rollbackError)
                    {
                        rollbackComplete = false;
                        log.Write($"Rollback failed for {entry.InstanceId}: {rollbackError.Message}");
                    }
                }
            }
            if (serviceExistedBefore is false)
            {
                var serviceRemoved = TryRemoveNewService(log);
                if (serviceRemoved)
                {
                    foreach (var path in createdSystemFiles)
                    {
                        try { File.Delete(path); }
                        catch (Exception cleanupError)
                        {
                            rollbackComplete = false;
                            log.Write($"Failed to remove newly copied file {path}: {cleanupError.Message}");
                        }
                    }
                }
                else
                {
                    rollbackComplete = false;
                    log.Write("The newly created libusb0 service could not be removed; system files were retained.");
                }
            }
            var rollbackText = snapshot is null
                ? "No device filters had been changed."
                : rollbackComplete
                    ? "The exact parent and child filter snapshot was restored."
                    : "Rollback was incomplete; see the driver log.";
            WriteResult(paths.ResultPath, new HelperResult(false, false,
                $"Driver installation failed. {rollbackText} {error.Message}", instanceId,
                ReadInstalledVersion(), paths.LogPath, backupPath));
            return 1;
        }
    }

    private static void PrepareControlledDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Driver operation directory cannot be a reparse point.");
    }

    private static void ValidateTarget(string instanceId, string expectedSerial)
    {
        var actualSerial = IPhoneFilterDriverService.NormalizeSerial(
            instanceId[(instanceId.LastIndexOf('\\') + 1)..]);
        if (!string.Equals(actualSerial, expectedSerial, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The requested iPhone instance does not match the selected UDID.");
        if (!TryGetDeviceStatus(instanceId, out _, out var problem) || problem != 0)
            throw new InvalidOperationException(
                $"The selected iPhone parent is not present and healthy (problem {problem}).");

        using var key = OpenDeviceKey(instanceId, writable: false);
        var service = key.GetValue("Service") as string;
        if (!string.Equals(service, "usbccgp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unexpected Apple parent service: {service ?? "(none)"}.");
    }

    private static void ValidatePackage()
    {
        ValidateHash(Path.Combine(PackageRoot, "amd64", "install-filter.exe"), InstallerHash);
        var driver = Path.Combine(PackageRoot, "amd64", "libusb0.sys");
        ValidateHash(driver, DriverHash);
        ValidateHash(Path.Combine(PackageRoot, "amd64", "libusb0.dll"), Dll64Hash);
        ValidateHash(Path.Combine(PackageRoot, "x86", "libusb0_x86.dll"), Dll32Hash);
        if (!IsAuthenticodeTrusted(driver))
            throw new InvalidOperationException(
                "The bundled libusb0 kernel driver's Authenticode signature is not trusted.");
    }

    private static void ValidateInstalledStack()
    {
        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\libusb0", writable: false)
            ?? throw new InvalidOperationException("The libusb0 kernel service is missing.");
        if (Convert.ToInt32(service.GetValue("Type", 0)) != 1)
            throw new InvalidOperationException("The existing libusb0 service is not a kernel driver.");

        var expectedDriver = Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys");
        var actualImage = ResolveServiceImage(service.GetValue("ImagePath") as string);
        if (!string.Equals(Path.GetFullPath(actualImage), Path.GetFullPath(expectedDriver),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The existing libusb0 service has an unexpected ImagePath: {actualImage}.");

        ValidateHash(expectedDriver, DriverHash);
        ValidateHash(Path.Combine(Environment.SystemDirectory, "libusb0.dll"), Dll64Hash);
        ValidateHash(Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!,
            "SysWOW64", "libusb0.dll"), Dll32Hash);
        if (!IsAuthenticodeTrusted(expectedDriver))
            throw new InvalidOperationException(
                "The installed libusb0 kernel driver signature is not trusted.");
    }

    private static void BootstrapSystemFiles(List<string> createdFiles)
    {
        var deployments = new[]
        {
            (Source: Path.Combine(PackageRoot, "amd64", "libusb0.sys"),
                Destination: Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys"),
                Hash: DriverHash),
            (Source: Path.Combine(PackageRoot, "amd64", "libusb0.dll"),
                Destination: Path.Combine(Environment.SystemDirectory, "libusb0.dll"),
                Hash: Dll64Hash),
            (Source: Path.Combine(PackageRoot, "x86", "libusb0_x86.dll"),
                Destination: Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!,
                    "SysWOW64", "libusb0.dll"),
                Hash: Dll32Hash),
        };
        foreach (var deployment in deployments)
        {
            ValidateHash(deployment.Source, deployment.Hash);
            if (File.Exists(deployment.Destination))
            {
                // Never overwrite an unrelated or newer global USB stack.
                ValidateHash(deployment.Destination, deployment.Hash);
                continue;
            }
            File.Copy(deployment.Source, deployment.Destination, overwrite: false);
            createdFiles.Add(deployment.Destination);
        }
    }

    private static bool TryRemoveNewService(OperationLog log)
    {
        if (!ServiceExists()) return true;
        try
        {
            var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
            var stop = RunProcess(sc, ["stop", "libusb0"], TimeSpan.FromSeconds(20));
            log.Write($"rollback sc stop exit={stop.ExitCode}");
            var delete = RunProcess(sc, ["delete", "libusb0"], TimeSpan.FromSeconds(20));
            log.Write($"rollback sc delete exit={delete.ExitCode}");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                if (!ServiceExists()) return true;
                Thread.Sleep(100);
            } while (DateTime.UtcNow < deadline);
            return !ServiceExists();
        }
        catch (Exception error)
        {
            log.Write("Service rollback failed: " + error.Message);
            return false;
        }
    }

    private static string ResolveServiceImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new InvalidOperationException("The libusb0 service ImagePath is empty.");
        var value = Environment.ExpandEnvironmentVariables(imagePath.Trim().Trim('"'));
        if (value.StartsWith(@"\??\", StringComparison.Ordinal)) value = value[4..];
        if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                value[12..]);
        else if (value.StartsWith("system32\\", StringComparison.OrdinalIgnoreCase))
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), value);
        return value;
    }

    private static FilterSnapshot[] CaptureSnapshot(string instanceId)
    {
        var instances = new List<string> { instanceId };
        instances.AddRange(GetDirectChildren(instanceId));
        return instances.Select(CaptureOne).ToArray();
    }

    private static FilterSnapshot CaptureOne(string instanceId)
    {
        using var key = OpenDeviceKey(instanceId, writable: false);
        var existed = key.GetValueNames().Contains("UpperFilters", StringComparer.OrdinalIgnoreCase);
        var values = ReadMultiString(key, "UpperFilters");
        return new FilterSnapshot(instanceId, existed, values);
    }

    private static void VerifySnapshotAndFilter(string instanceId, FilterSnapshot[] snapshot)
    {
        var parentBefore = snapshot.Single(entry => string.Equals(entry.InstanceId,
            instanceId, StringComparison.OrdinalIgnoreCase));
        var parentAfter = CaptureOne(instanceId);
        if (!parentAfter.UpperFilters.Contains("libusb0", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The parent libusb0 UpperFilter is missing.");
        foreach (var existing in parentBefore.UpperFilters)
        {
            if (!parentAfter.UpperFilters.Contains(existing, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"An existing parent UpperFilter was lost: {existing}.");
        }
        foreach (var childBefore in snapshot.Where(entry => !string.Equals(entry.InstanceId,
                     instanceId, StringComparison.OrdinalIgnoreCase)))
        {
            var childAfter = CaptureOne(childBefore.InstanceId);
            if (childAfter.UpperFiltersExisted != childBefore.UpperFiltersExisted ||
                !childAfter.UpperFilters.SequenceEqual(childBefore.UpperFilters,
                    StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"A child-interface UpperFilter changed: {childBefore.InstanceId}.");
        }
    }

    private static void RestoreSnapshot(FilterSnapshot snapshot)
    {
        using var key = OpenDeviceKey(snapshot.InstanceId, writable: true);
        if (!snapshot.UpperFiltersExisted)
        {
            key.DeleteValue("UpperFilters", throwOnMissingValue: false);
            return;
        }
        key.SetValue("UpperFilters", snapshot.UpperFilters, RegistryValueKind.MultiString);
    }

    private static RegistryKey OpenDeviceKey(string instanceId, bool writable) =>
        Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\" + instanceId, writable)
        ?? throw new InvalidOperationException($"Device registry key is unavailable: {instanceId}.");

    private static string[] ReadMultiString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            string[] values => values,
            string value when value.Length != 0 => [value],
            _ => [],
        };

    private static IEnumerable<string> GetDirectChildren(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var parent, instanceId, 0) != CrSuccess) yield break;
        if (CM_Get_Child(out var current, parent, 0) != CrSuccess) yield break;
        while (true)
        {
            var id = GetDeviceId(current);
            if (id is not null) yield return id;
            if (CM_Get_Sibling(out current, current, 0) != CrSuccess) yield break;
        }
    }

    private static string? GetDeviceId(uint deviceNode)
    {
        if (CM_Get_Device_ID_Size(out var length, deviceNode, 0) != CrSuccess) return null;
        var buffer = new StringBuilder(checked((int)length + 1));
        return CM_Get_Device_IDW(deviceNode, buffer, (uint)buffer.Capacity, 0) == CrSuccess
            ? buffer.ToString() : null;
    }

    private static bool TryGetDeviceStatus(string instanceId, out uint status, out uint problem)
    {
        status = 0;
        problem = uint.MaxValue;
        var locate = CM_Locate_DevNodeW(out var node, instanceId, 0);
        if (locate == CrNoSuchDevNode || locate != CrSuccess) return false;
        return CM_Get_DevNode_Status(out status, out problem, node, 0) == CrSuccess;
    }

    private static bool WaitForHealthyTarget(string instanceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (TryGetDeviceStatus(instanceId, out _, out var problem) && problem == 0)
                return true;
            Thread.Sleep(250);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private static ProcessResult RunProcess(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The filter installer did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("The filter installer timed out.");
        }
        Task.WaitAll(stdout, stderr);
        var lines = (stdout.Result + Environment.NewLine + stderr.Result)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ProcessResult(process.ExitCode, lines);
    }

    private static void ValidateHash(string path, string expected)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required driver file is missing.", path);
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Driver file hash mismatch: {path}.");
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool ServiceExists()
    {
        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\libusb0", writable: false);
        return service is not null;
    }

    private static string? ReadInstalledVersion()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys");
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
        }
        catch { return null; }
    }

    private static void WriteResult(string resultPath, HelperResult payload)
    {
        var temporary = resultPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        File.Move(temporary, resultPath, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private static bool IsAuthenticodeTrusted(string path)
    {
        var filePath = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
        };
        var fileInfoPointer = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(
            System.Runtime.InteropServices.Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData
            {
                StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00000010,
                UiContext = 0,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(0, ref action, ref data) == 0;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(fileInfoPointer);
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(filePath);
        }
    }

    private sealed record FilterSnapshot(
        string InstanceId, bool UpperFiltersExisted, string[] UpperFilters);
    private sealed record HelperResult(
        bool Success, bool RequiresReplug, string Message, string InstanceId,
        string? DriverVersion, string LogPath, string? BackupPath);
    private sealed record ProcessResult(int ExitCode, string[] OutputLines);

    private sealed class OperationLog(string path)
    {
        internal void Write(string message) => File.AppendAllText(path,
            $"{DateTime.UtcNow:O} {message}{Environment.NewLine}", Encoding.UTF8);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint StructSize;
        internal nint FilePath;
        internal nint FileHandle;
        internal nint KnownSubject;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct WinTrustData
    {
        internal uint StructSize;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint FileInfo;
        internal uint StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal nint SignatureSettings;
    }

    [System.Runtime.InteropServices.DllImport("wintrust.dll", ExactSpelling = true,
        PreserveSig = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action,
        ref WinTrustData data);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet =
        System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint deviceNode,
        string deviceId, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status,
        out uint problemNumber, uint deviceNode, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Child(out uint child, uint parent, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Sibling(out uint sibling, uint deviceNode, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Device_ID_Size(out uint length, uint deviceNode, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet =
        System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint deviceNode, StringBuilder buffer,
        uint bufferLength, uint flags);
}
