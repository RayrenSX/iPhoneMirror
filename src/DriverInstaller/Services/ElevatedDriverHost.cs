using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

internal static class ElevatedDriverHost
{
    private const uint CrSuccess = 0;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && string.Equals(arguments[0],
            DriverConstants.ElevatedSwitch, StringComparison.Ordinal);

    internal static int Run(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 5 || !IsRequested(arguments) ||
            !Enum.TryParse<DriverOperationKind>(arguments[1], ignoreCase: false, out var kind) ||
            !DriverConstants.IsAllowedAppleParent(arguments[2]) ||
            string.IsNullOrWhiteSpace(arguments[3]) ||
            !DriverConstants.IsValidOperationId(arguments[4]))
            return 2;

        var instanceId = arguments[2];
        var expectedSerial = DriverConstants.NormalizeSerial(arguments[3]);
        var operationId = arguments[4];
        var paths = DriverConstants.GetOperationPaths(operationId);
        try
        {
            DriverPayload.CreateSafeDirectory(DriverConstants.DataRoot);
            DriverPayload.CreateSafeDirectory(DriverConstants.OperationsRoot);
            DriverPayload.CreateSafeDirectory(DriverConstants.BackupsRoot);
            if (Directory.Exists(paths.Directory))
                throw new IOException("The driver operation directory already exists.");
            DriverPayload.CreateSafeDirectory(paths.Directory);
        }
        catch
        {
            return 3;
        }

        var log = new OperationLog(paths.LogPath);
        FilterSnapshot[]? snapshot = null;
        var createdSystemFiles = new List<string>();
        string? backupPath = null;
        bool? serviceExistedBefore = null;
        var parentRemovalStarted = false;
        try
        {
            if (!IsAdministrator())
                throw new InvalidOperationException("The driver operation is not elevated.");

            using var mutex = new Mutex(false, @"Global\iPhoneMirror.Driver.Operation");
            var lockTaken = false;
            try
            {
                try { lockTaken = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { lockTaken = true; }
                if (!lockTaken)
                    throw new InvalidOperationException(
                        "Another iPhone driver operation is already running.");

                var target = ValidateTarget(instanceId, expectedSerial,
                    requirePresent: kind is not DriverOperationKind.Uninstall,
                    allowKnownBadParent: kind == DriverOperationKind.ParentRepair);
                log.Write($"Validated {kind} target {target.InstanceId} present={target.IsPresent}.");

                var payloadRoot = DriverPayload.ExtractRuntimeFiles(paths.Directory);
                serviceExistedBefore = ServiceExists();
                if (serviceExistedBefore.Value && kind != DriverOperationKind.ParentRepair)
                    ValidateInstalledServiceDefinition();

                snapshot = CaptureSnapshot(instanceId);
                backupPath = SaveSnapshot(kind, expectedSerial, operationId, snapshot,
                    serviceExistedBefore.Value);
                log.Write("Saved rollback snapshot: " + backupPath);

                if (kind == DriverOperationKind.ParentRepair)
                {
                    if (!DriverConstants.IsKnownReplaceableParentService(target.Service))
                        throw new InvalidOperationException(
                            $"The Apple parent service is not a known replaceable driver: {target.Service}.");
                    parentRemovalStarted = true;
                    RunPnPRemove(instanceId, log);
                    snapshot = null;
                    if (IsDevicePresent(instanceId))
                        throw new InvalidOperationException(
                            "Windows did not remove the incorrect Apple parent device.");
                    var parentMessage =
                        "The incorrect Apple parent device was removed. Reconnect the iPhone to rebind usbccgp.";
                    WriteResult(paths.ResultPath, new DriverOperationResult(true, true,
                        parentMessage, instanceId, backupPath, paths.LogPath));
                    log.Write(parentMessage);
                    return 0;
                }

                if (kind is DriverOperationKind.Install or DriverOperationKind.Repair)
                {
                    EnsureSystemFiles(payloadRoot, createdSystemFiles);
                    RunFilterTool(payloadRoot, "i", "-di=" + instanceId, log);
                    WaitForHealthyTarget(instanceId, TimeSpan.FromSeconds(20));
                    VerifyInstalled(instanceId, snapshot);
                    ValidateInstalledStack();
                }
                else
                {
                    RunFilterTool(payloadRoot, "u", "-di=" + instanceId, log);
                    WaitForHealthyTarget(instanceId, TimeSpan.FromSeconds(20));
                    VerifyUninstalled(instanceId, snapshot);
                }

                var message = kind == DriverOperationKind.Uninstall
                    ? "Selected-device capture filter removed. Reconnect the device to complete unload."
                    : "Selected-device capture filter installed. Reconnect the device to complete activation.";
                var result = new DriverOperationResult(true, target.IsPresent, message,
                    instanceId, backupPath, paths.LogPath);
                WriteResult(paths.ResultPath, result);
                log.Write(message);
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
            var rollbackComplete = kind == DriverOperationKind.ParentRepair
                ? !parentRemovalStarted
                : RollBack(snapshot, serviceExistedBefore, createdSystemFiles, log);
            var message = kind == DriverOperationKind.ParentRepair
                ? parentRemovalStarted
                    ? "Parent driver repair stopped after the removal request began. " +
                      "Reconnect the iPhone and review the operation log. " + error.Message
                    : "Parent driver repair was rejected before any system change. " + error.Message
                : rollbackComplete
                    ? "Driver operation failed and all captured state was restored. " + error.Message
                    : "Driver operation failed and rollback was incomplete. Review the operation log. " +
                      error.Message;
            try
            {
                WriteResult(paths.ResultPath, new DriverOperationResult(false, false, message,
                    instanceId, backupPath, paths.LogPath));
            }
            catch (Exception resultError)
            {
                log.Write("Failed to write result: " + resultError.Message);
            }
            return 1;
        }
    }

    private static AppleDeviceRecord ValidateTarget(string instanceId, string expectedSerial,
        bool requirePresent, bool allowKnownBadParent)
    {
        var actualSerial = DriverConstants.NormalizeSerial(
            instanceId[(instanceId.LastIndexOf('\\') + 1)..]);
        if (!string.Equals(actualSerial, expectedSerial, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The selected device instance does not match the expected serial.");

        var target = new DeviceCatalog().FindExact(instanceId, expectedSerial)
            ?? throw new InvalidOperationException("The selected Apple parent device no longer exists.");
        if (!string.Equals(target.Service, "usbccgp", StringComparison.OrdinalIgnoreCase) &&
            !(allowKnownBadParent &&
              DriverConstants.IsKnownReplaceableParentService(target.Service)))
            throw new InvalidOperationException(
                $"Unexpected Apple parent service: {target.Service}. No changes were made.");
        if (requirePresent && !target.IsPresent)
            throw new InvalidOperationException(
                "The selected Apple device is not connected and healthy.");
        return target;
    }

    private static string SaveSnapshot(DriverOperationKind kind, string serial,
        string operationId, FilterSnapshot[] snapshot, bool serviceExisted)
    {
        var path = Path.Combine(DriverConstants.BackupsRoot,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{kind}-{serial}-{operationId}.json");
        var payload = new
        {
            CreatedUtc = DateTime.UtcNow,
            Operation = kind.ToString(),
            Serial = serial,
            ServiceExisted = serviceExisted,
            Devices = snapshot,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        return path;
    }

    private static void RunFilterTool(string payloadRoot, string command, string device,
        OperationLog log)
    {
        var installer = Path.Combine(payloadRoot, @"amd64\install-filter.exe");
        DriverPayload.ValidateHash(installer, DriverConstants.InstallerHash);
        var result = RunProcess(installer, [command, device], TimeSpan.FromMinutes(2));
        log.Write($"install-filter {command} exit={result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
            foreach (var line in result.CombinedOutput.Split(['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                log.Write("install-filter: " + line);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Exact-device filter operation failed with code {result.ExitCode}.");
    }

    private static void EnsureSystemFiles(string payloadRoot, List<string> createdFiles)
    {
        var windows = Path.GetDirectoryName(Environment.SystemDirectory)!;
        var deployments = new[]
        {
            (Source: Path.Combine(payloadRoot, @"amd64\libusb0.sys"),
                Destination: Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys"),
                Hash: DriverConstants.DriverHash),
            (Source: Path.Combine(payloadRoot, @"amd64\libusb0.dll"),
                Destination: Path.Combine(Environment.SystemDirectory, "libusb0.dll"),
                Hash: DriverConstants.Dll64Hash),
            (Source: Path.Combine(payloadRoot, @"x86\libusb0_x86.dll"),
                Destination: Path.Combine(windows, "SysWOW64", "libusb0.dll"),
                Hash: DriverConstants.Dll32Hash),
        };

        foreach (var item in deployments)
        {
            DriverPayload.ValidateHash(item.Source, item.Hash);
            if (File.Exists(item.Destination))
            {
                DriverPayload.ValidateHash(item.Destination, item.Hash);
                continue;
            }
            File.Copy(item.Source, item.Destination, overwrite: false);
            createdFiles.Add(item.Destination);
            DriverPayload.ValidateHash(item.Destination, item.Hash);
        }
    }

    private static void ValidateInstalledServiceDefinition()
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
                "The existing libusb0 service points to an unexpected driver path.");

    }

    private static void ValidateInstalledStack()
    {
        ValidateInstalledServiceDefinition();
        DriverPayload.ValidateHash(Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys"),
            DriverConstants.DriverHash);
        DriverPayload.ValidateHash(Path.Combine(Environment.SystemDirectory, "libusb0.dll"),
            DriverConstants.Dll64Hash);
        DriverPayload.ValidateHash(Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!,
            "SysWOW64", "libusb0.dll"), DriverConstants.Dll32Hash);
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
        var ids = new List<string> { instanceId };
        ids.AddRange(GetDirectChildren(instanceId));
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(CaptureOne).ToArray();
    }

    private static FilterSnapshot CaptureOne(string instanceId)
    {
        using var key = OpenDeviceKey(instanceId, writable: false);
        var existed = key.GetValueNames().Contains("UpperFilters",
            StringComparer.OrdinalIgnoreCase);
        return new FilterSnapshot(instanceId, existed,
            DeviceCatalog.ReadMultiString(key, "UpperFilters"));
    }

    private static void VerifyInstalled(string instanceId, FilterSnapshot[] snapshot)
    {
        var before = snapshot.Single(item => string.Equals(item.InstanceId,
            instanceId, StringComparison.OrdinalIgnoreCase));
        var after = CaptureOne(instanceId);
        if (!after.UpperFilters.Contains("libusb0", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target parent filter was not installed.");
        VerifyExistingFiltersPreserved(before, after, excludingLibUsb0: false);
        VerifyChildrenUnchanged(instanceId, snapshot);
    }

    private static void VerifyUninstalled(string instanceId, FilterSnapshot[] snapshot)
    {
        var before = snapshot.Single(item => string.Equals(item.InstanceId,
            instanceId, StringComparison.OrdinalIgnoreCase));
        var after = CaptureOne(instanceId);
        if (after.UpperFilters.Contains("libusb0", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target parent filter was not removed.");
        VerifyExistingFiltersPreserved(before, after, excludingLibUsb0: true);
        VerifyChildrenUnchanged(instanceId, snapshot);
    }

    private static void VerifyExistingFiltersPreserved(FilterSnapshot before,
        FilterSnapshot after, bool excludingLibUsb0)
    {
        var expected = before.UpperFilters.Where(value =>
            !excludingLibUsb0 || !string.Equals(value, "libusb0",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var filter in expected)
            if (!after.UpperFilters.Contains(filter, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"An existing UpperFilter was lost: {filter}.");
    }

    private static void VerifyChildrenUnchanged(string parentId, FilterSnapshot[] snapshot)
    {
        foreach (var before in snapshot.Where(item => !string.Equals(item.InstanceId,
                     parentId, StringComparison.OrdinalIgnoreCase)))
        {
            var after = CaptureOne(before.InstanceId);
            if (after.UpperFiltersExisted != before.UpperFiltersExisted ||
                !after.UpperFilters.SequenceEqual(before.UpperFilters, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"A child-interface filter changed unexpectedly: {before.InstanceId}.");
        }
    }

    private static bool RollBack(FilterSnapshot[]? snapshot, bool? serviceExistedBefore,
        IReadOnlyList<string> createdFiles, OperationLog log)
    {
        var complete = true;
        if (snapshot is not null)
        {
            foreach (var item in snapshot)
            {
                try { RestoreSnapshot(item); }
                catch (Exception error)
                {
                    complete = false;
                    log.Write($"Snapshot restore failed for {item.InstanceId}: {error.Message}");
                }
            }
        }

        if (serviceExistedBefore is false)
        {
            if (TryRemoveNewService(log))
            {
                foreach (var path in createdFiles)
                {
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch (Exception error)
                    {
                        complete = false;
                        log.Write($"Created file cleanup failed for {path}: {error.Message}");
                    }
                }
            }
            else complete = false;
        }
        return complete;
    }

    private static void RestoreSnapshot(FilterSnapshot snapshot)
    {
        using var key = OpenDeviceKey(snapshot.InstanceId, writable: true);
        if (!snapshot.UpperFiltersExisted)
            key.DeleteValue("UpperFilters", throwOnMissingValue: false);
        else
            key.SetValue("UpperFilters", snapshot.UpperFilters, RegistryValueKind.MultiString);
    }

    private static RegistryKey OpenDeviceKey(string instanceId, bool writable) =>
        Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\" + instanceId, writable)
        ?? throw new InvalidOperationException(
            $"Device registry key is unavailable: {instanceId}.");

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

    private static string? GetDeviceId(uint node)
    {
        if (CM_Get_Device_ID_Size(out var length, node, 0) != CrSuccess) return null;
        var buffer = new StringBuilder(checked((int)length + 1));
        return CM_Get_Device_IDW(node, buffer, (uint)buffer.Capacity, 0) == CrSuccess
            ? buffer.ToString()
            : null;
    }

    private static bool WaitForHealthyTarget(string instanceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (CM_Locate_DevNodeW(out var node, instanceId, 0) == CrSuccess &&
                CM_Get_DevNode_Status(out _, out var problem, node, 0) == CrSuccess &&
                problem == 0) return true;
            Thread.Sleep(250);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private static ProcessResult RunProcess(string executable,
        IReadOnlyList<string> arguments, TimeSpan timeout)
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
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    private static bool TryRemoveNewService(OperationLog log)
    {
        if (!ServiceExists()) return true;
        try
        {
            var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
            var stop = RunProcess(sc, ["stop", "libusb0"], TimeSpan.FromSeconds(20));
            log.Write("rollback sc stop exit=" + stop.ExitCode);
            var delete = RunProcess(sc, ["delete", "libusb0"], TimeSpan.FromSeconds(20));
            log.Write("rollback sc delete exit=" + delete.ExitCode);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (!ServiceExists()) return true;
                Thread.Sleep(100);
            }
            return !ServiceExists();
        }
        catch (Exception error)
        {
            log.Write("Service rollback failed: " + error.Message);
            return false;
        }
    }

    private static bool ServiceExists()
    {
        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\libusb0", writable: false);
        return service is not null;
    }

    private static void RunPnPRemove(string instanceId, OperationLog log)
    {
        var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        var result = RunProcess(pnputil, ["/remove-device", instanceId, "/force"],
            TimeSpan.FromMinutes(2));
        log.Write($"pnputil remove-device exit={result.ExitCode}");
        foreach (var line in result.CombinedOutput.Split(['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            log.Write("pnputil: " + line);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Windows failed to remove the incorrect Apple parent device (code {result.ExitCode}).");
    }

    private static bool IsDevicePresent(string instanceId) =>
        CM_Locate_DevNodeW(out var node, instanceId, 0) == CrSuccess &&
        CM_Get_DevNode_Status(out _, out _, node, 0) == CrSuccess;

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void WriteResult(string resultPath, DriverOperationResult result)
    {
        var temporary = resultPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        File.Move(temporary, resultPath, overwrite: true);
    }

    private sealed record FilterSnapshot(
        string InstanceId, bool UpperFiltersExisted, string[] UpperFilters);

    private sealed class OperationLog(string path)
    {
        internal void Write(string message) => File.AppendAllText(path,
            $"{DateTime.UtcNow:O} {message}{Environment.NewLine}", Encoding.UTF8);
    }

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
