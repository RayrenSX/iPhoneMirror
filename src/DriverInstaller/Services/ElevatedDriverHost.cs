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
    private const uint DevNodePresent = 0x00000008;
    private const int MaximumLoggedProcessOutputLines = 80;
    private const int MaximumLoggedProcessLineCharacters = 1024;
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
            !Enum.IsDefined(kind) ||
            !DriverConstants.IsAllowedAppleParent(arguments[2]) ||
            !DriverConstants.IsValidSerial(arguments[3]) ||
            !DriverConstants.IsValidOperationId(arguments[4]))
        {
            DriverLogger.WriteWarning("elevated-host", "arguments_rejected",
                ("argument_count", arguments.Count),
                ("kind", arguments.Count > 1 ? arguments[1] : "unknown"),
                ("operation", arguments.Count > 4 ? arguments[4] : "unknown"));
            return 2;
        }

        var instanceId = arguments[2];
        var expectedSerial = DriverConstants.NormalizeSerial(arguments[3]);
        var operationId = arguments[4];
        var paths = DriverConstants.GetOperationPaths(operationId);
        var timer = Stopwatch.StartNew();
        try
        {
            DriverPayload.CreateProtectedSystemDirectory(DriverConstants.DataRoot);
            DriverPayload.CreateProtectedSystemDirectory(DriverConstants.OperationsRoot);
            DriverPayload.CreateProtectedSystemDirectory(DriverConstants.BackupsRoot);
            if (Directory.Exists(paths.Directory))
                throw new IOException("The driver operation directory already exists.");
            DriverPayload.CreateProtectedSystemDirectory(paths.Directory);
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("elevated-host", "operation_directory_failed", error,
                ("operation", operationId), ("kind", kind));
            return 3;
        }

        var log = new OperationLog(paths.LogPath, operationId, kind);
        log.WriteEvent("started", ("device", DriverLogger.DeviceFingerprint(expectedSerial)),
            ("instance", DriverLogger.Sanitize(instanceId)));
        FilterSnapshot[]? snapshot = null;
        var createdSystemFiles = new List<string>();
        var replacedSystemFiles = new List<SystemFileBackup>();
        string? backupPath = null;
        bool? serviceExistedBefore = null;
        var parentRemovalStarted = false;
        try
        {
            if (!IsAdministrator())
                throw new InvalidOperationException("The driver operation is not elevated.");
            log.WriteEvent("privilege_verified", ("administrator", true));

            using var mutex = new Mutex(false, @"Global\iPhoneMirror.Driver.Operation");
            var lockTaken = false;
            try
            {
                try { lockTaken = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { lockTaken = true; }
                if (!lockTaken)
                    throw new InvalidOperationException(
                        "Another iPhone driver operation is already running.");
                log.WriteEvent("operation_lock_acquired");

                var target = ValidateTarget(instanceId, expectedSerial,
                    requirePresent: kind is not DriverOperationKind.Uninstall,
                    allowKnownBadParent: kind == DriverOperationKind.ParentRepair);
                log.WriteEvent("target_validated",
                    ("device", DriverLogger.DescribeDevice(target)));

                log.WriteEvent("payload_extract_start");
                var payloadRoot = DriverPayload.ExtractRuntimeFiles(paths.Directory);
                log.WriteEvent("payload_verified", ("files", 4),
                    ("payload", DriverLogger.DescribePath(payloadRoot)),
                    ("kernel_signature", "trusted"));
                serviceExistedBefore = ServiceExists();
                log.WriteEvent("service_snapshot", ("exists", serviceExistedBefore.Value));
                if (serviceExistedBefore.Value && kind != DriverOperationKind.ParentRepair)
                    ValidateInstalledServiceDefinition();

                snapshot = CaptureSnapshot(instanceId);
                log.WriteEvent("filter_snapshot_captured", ("entries", snapshot.Length));
                backupPath = SaveSnapshot(kind, expectedSerial, operationId, snapshot,
                    serviceExistedBefore.Value);
                log.WriteEvent("rollback_snapshot_saved",
                    ("snapshot", DriverLogger.DescribePath(backupPath)));

                if (kind == DriverOperationKind.ParentRepair)
                {
                    if (!DriverConstants.IsKnownReplaceableParentService(target.Service))
                        throw new InvalidOperationException(
                            $"The Apple parent service is not a known replaceable driver: {target.Service}.");
                    parentRemovalStarted = true;
                    log.WriteEvent("parent_repair_remove_start");
                    RunPnPRemove(instanceId, log);
                    snapshot = null;
                    if (IsDevicePresent(instanceId))
                        throw new InvalidOperationException(
                            "Windows did not remove the incorrect Apple parent device.");
                    var parentMessage =
                        "The incorrect Apple parent device was removed. Reconnect the iPhone to rebind usbccgp.";
                    WriteResult(paths.ResultPath, new DriverOperationResult(true, true,
                        parentMessage, instanceId, backupPath, paths.LogPath));
                    log.WriteEvent("completed", ("success", true),
                        ("requires_replug", true), ("elapsed_ms", timer.ElapsedMilliseconds),
                        ("message", parentMessage));
                    return 0;
                }

                if (kind is DriverOperationKind.Install or DriverOperationKind.Repair)
                {
                    log.WriteEvent("filter_install_start", ("operation_kind", kind));
                    EnsureSystemFiles(payloadRoot, createdSystemFiles,
                        replacedSystemFiles, paths.Directory, log);
                    RunFilterTool(payloadRoot, "i", "-di=" + instanceId, log);
                    var healthy = WaitForHealthyTarget(instanceId, TimeSpan.FromSeconds(20));
                    log.WriteEvent("target_health_checked", ("healthy", healthy));
                    if (!healthy)
                        throw new TimeoutException("The target device did not become healthy in time.");
                    VerifyInstalled(instanceId, snapshot);
                    ValidateInstalledStack();
                    log.WriteEvent("installed_stack_verified", ("hashes", "trusted"));
                    log.WriteEvent("filter_install_verified",
                        ("created_system_files", createdSystemFiles.Count));
                }
                else
                {
                    log.WriteEvent("filter_uninstall_start");
                    RunFilterTool(payloadRoot, "u", "-di=" + instanceId, log);
                    var healthy = WaitForHealthyTarget(instanceId, TimeSpan.FromSeconds(20));
                    log.WriteEvent("target_health_checked", ("healthy", healthy));
                    if (!healthy)
                        throw new TimeoutException("The target device did not become healthy in time.");
                    VerifyUninstalled(instanceId, snapshot);
                    log.WriteEvent("filter_uninstall_verified");
                }

                var message = kind == DriverOperationKind.Uninstall
                    ? "Selected-device capture filter removed. Reconnect the device to complete unload."
                    : "Selected-device capture filter installed. Reconnect the device to complete activation.";
                var result = new DriverOperationResult(true, target.IsPresent, message,
                    instanceId, backupPath, paths.LogPath);
                WriteResult(paths.ResultPath, result);
                log.WriteEvent("completed", ("success", true),
                    ("requires_replug", target.IsPresent),
                    ("elapsed_ms", timer.ElapsedMilliseconds), ("message", message));
                return 0;
            }
            finally
            {
                if (lockTaken) mutex.ReleaseMutex();
            }
        }
        catch (Exception error)
        {
            log.WriteException("operation_failed", error,
                ("elapsed_ms", timer.ElapsedMilliseconds),
                ("parent_removal_started", parentRemovalStarted));
            log.WriteEvent("rollback_start", ("snapshot_entries", snapshot?.Length ?? 0),
                ("created_system_files", createdSystemFiles.Count),
                ("replaced_system_files", replacedSystemFiles.Count));
            var rollbackComplete = kind == DriverOperationKind.ParentRepair
                ? !parentRemovalStarted
                : RollBack(snapshot, serviceExistedBefore, createdSystemFiles,
                    replacedSystemFiles, log);
            log.WriteEvent("rollback_completed", ("complete", rollbackComplete),
                ("elapsed_ms", timer.ElapsedMilliseconds));
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
                log.WriteException("result_write_failed", resultError);
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
        var deviceFingerprint = DriverLogger.DeviceFingerprint(serial);
        var path = Path.Combine(DriverConstants.BackupsRoot,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{kind}-{deviceFingerprint}-{operationId}.json");
        var payload = new
        {
            CreatedUtc = DateTime.UtcNow,
            Operation = kind.ToString(),
            DeviceFingerprint = deviceFingerprint,
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
        using var installerLock = DriverPayload.LockAndValidateHash(installer,
            DriverConstants.InstallerHash);
        log.WriteEvent("payload_hash_verified", ("asset", "install-filter.exe"),
            ("sha256", DriverLogger.HashTag(DriverConstants.InstallerHash)));
        var result = RunProcess(installer, [command, device], TimeSpan.FromMinutes(2), log,
            "install-filter");
        log.WriteEvent("filter_tool_result", ("command", command),
            ("exit_code", result.ExitCode));
        if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
            WriteProcessOutput(log, "filter_tool_output", result.CombinedOutput);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Exact-device filter operation failed with code {result.ExitCode}.");
    }

    private static void EnsureSystemFiles(string payloadRoot, List<string> createdFiles,
        List<SystemFileBackup> replacedFiles, string operationDirectory,
        OperationLog log)
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
            using var sourceLock = DriverPayload.LockAndValidateHash(item.Source, item.Hash);
            log.WriteEvent("payload_hash_verified", ("asset", Path.GetFileName(item.Source)),
                ("sha256", DriverLogger.HashTag(item.Hash)));
            if (File.Exists(item.Destination))
            {
                try
                {
                    DriverPayload.ValidateHash(item.Destination, item.Hash);
                    log.WriteEvent("system_file_verified",
                        ("file", Path.GetFileName(item.Destination)),
                        ("action", "existing"),
                        ("sha256", DriverLogger.HashTag(item.Hash)));
                }
                catch (InvalidOperationException)
                {
                    var backup = Path.Combine(operationDirectory, "system-backup",
                        GetSystemBackupName(item.Destination));
                    DriverPayload.CreateProtectedSystemDirectory(
                        Path.GetDirectoryName(backup)!);
                    DriverPayload.EnsureNoReparsePoints(item.Destination);
                    DriverPayload.EnsureNoReparsePoints(backup);
                    File.Copy(item.Destination, backup, overwrite: false);
                    replacedFiles.Add(new SystemFileBackup(item.Destination, backup));
                    ReplaceSystemFile(item.Source, item.Destination);
                    DriverPayload.ValidateHash(item.Destination, item.Hash);
                    log.WriteEvent("system_file_repaired",
                        ("file", Path.GetFileName(item.Destination)),
                        ("backup", DriverLogger.DescribePath(backup)),
                        ("sha256", DriverLogger.HashTag(item.Hash)));
                }
                continue;
            }
            ReplaceSystemFile(item.Source, item.Destination);
            createdFiles.Add(item.Destination);
            DriverPayload.ValidateHash(item.Destination, item.Hash);
            log.WriteEvent("system_file_deployed", ("file", Path.GetFileName(item.Destination)),
                ("action", "created"), ("sha256", DriverLogger.HashTag(item.Hash)));
        }
    }

    private static string GetSystemBackupName(string destination)
    {
        var fullPath = Path.GetFullPath(destination);
        var name = new string(fullPath.Select(character =>
            char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray());
        return name.Trim('_') + ".bak";
    }

    private static void ReplaceSystemFile(string source, string destination)
    {
        var temporary = destination + ".iPhoneMirror.tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var sourceStream = new FileStream(source, FileMode.Open,
                       FileAccess.Read, FileShare.Read, 64 * 1024,
                       FileOptions.SequentialScan))
            using (var destinationStream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 64 * 1024,
                       FileOptions.WriteThrough))
            {
                sourceStream.CopyTo(destinationStream);
                destinationStream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
                File.Replace(temporary, destination, null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RestoreSystemFile(SystemFileBackup backup)
    {
        ReplaceSystemFile(backup.Backup, backup.Destination);
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

    private sealed record SystemFileBackup(string Destination, string Backup);

    private static bool RollBack(FilterSnapshot[]? snapshot, bool? serviceExistedBefore,
        IReadOnlyList<string> createdFiles,
        IReadOnlyList<SystemFileBackup> replacedFiles, OperationLog log)
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
                    log.WriteException("snapshot_restore_failed", error,
                        ("device", DriverLogger.Sanitize(item.InstanceId)));
                }
            }
        }

        var newServiceRemoved = serviceExistedBefore is not false || TryRemoveNewService(log);
        if (!CanRemoveDeployedSystemFilesAfterRollback(serviceExistedBefore,
                newServiceRemoved))
        {
            // The service may still have the files open or reference them on reboot.
            // Leave this operation's deployed files intact rather than break it.
            log.WriteWarning("system_file_cleanup_deferred",
                ("reason", "new_service_removal_failed"));
            return false;
        }

        foreach (var item in replacedFiles.Reverse())
        {
            try
            {
                RestoreSystemFile(item);
                log.WriteEvent("system_file_restored",
                    ("file", Path.GetFileName(item.Destination)));
            }
            catch (Exception error)
            {
                complete = false;
                log.WriteException("system_file_restore_failed", error,
                    ("file", Path.GetFileName(item.Destination)));
            }
        }

        foreach (var path in createdFiles.Reverse())
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception error)
            {
                complete = false;
                log.WriteException("created_file_cleanup_failed", error,
                    ("file", Path.GetFileName(path)));
            }
        }
        return complete;
    }

    internal static bool CanRemoveDeployedSystemFilesAfterRollback(
        bool? serviceExistedBefore, bool newServiceRemoved) =>
        serviceExistedBefore is not false || newServiceRemoved;

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
                CM_Get_DevNode_Status(out var status, out var problem, node, 0) == CrSuccess &&
                problem == 0 && (status & DevNodePresent) != 0) return true;
            Thread.Sleep(250);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private static ProcessResult RunProcess(string executable,
        IReadOnlyList<string> arguments, TimeSpan timeout, OperationLog? log = null,
        string? processName = null)
    {
        var timer = Stopwatch.StartNew();
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
        var name = processName ?? Path.GetFileName(executable);
        log?.WriteEvent("process_start", ("process", name),
            ("argument_count", arguments.Count), ("timeout_ms", timeout.TotalMilliseconds));
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The filter installer did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
        {
            var terminationRequested = false;
            try
            {
                process.Kill(entireProcessTree: true);
                terminationRequested = true;
                process.WaitForExit(5000);
            }
            catch (Exception error)
            {
                log?.WriteException("process_timeout_termination_failed", error,
                    ("process", name));
            }
            log?.WriteError("process_timeout", ("process", name),
                ("elapsed_ms", timer.ElapsedMilliseconds),
                ("termination_requested", terminationRequested),
                ("terminated", process.HasExited));
            throw new TimeoutException("The filter installer timed out.");
        }
        Task.WaitAll(stdout, stderr);
        var result = new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
        log?.WriteEvent("process_exit", ("process", name), ("exit_code", result.ExitCode),
            ("elapsed_ms", timer.ElapsedMilliseconds),
            ("stdout_length", result.StandardOutput.Length),
            ("stderr_length", result.StandardError.Length));
        return result;
    }

    private static bool TryRemoveNewService(OperationLog log)
    {
        if (!ServiceExists()) return true;
        try
        {
            var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
            var stop = RunProcess(sc, ["stop", "libusb0"], TimeSpan.FromSeconds(20), log,
                "sc-stop");
            log.WriteEvent("service_rollback_stop", ("exit_code", stop.ExitCode));
            var delete = RunProcess(sc, ["delete", "libusb0"], TimeSpan.FromSeconds(20), log,
                "sc-delete");
            log.WriteEvent("service_rollback_delete", ("exit_code", delete.ExitCode));
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
            log.WriteException("service_rollback_failed", error);
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
            TimeSpan.FromMinutes(2), log, "pnputil");
        log.WriteEvent("pnp_remove_result", ("exit_code", result.ExitCode));
        WriteProcessOutput(log, "pnp_remove_output", result.CombinedOutput);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Windows failed to remove the incorrect Apple parent device (code {result.ExitCode}).");
    }

    private static void WriteProcessOutput(OperationLog log, string eventName, string output)
    {
        var lines = output.Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var truncatedLines = 0;
        var headCount = lines.Length <= MaximumLoggedProcessOutputLines
            ? lines.Length
            : MaximumLoggedProcessOutputLines / 2;
        var tailStart = lines.Length <= MaximumLoggedProcessOutputLines
            ? lines.Length
            : lines.Length - MaximumLoggedProcessOutputLines / 2;

        void WriteLine(int index)
        {
            var line = lines[index];
            var truncated = line.Length > MaximumLoggedProcessLineCharacters;
            if (truncated)
            {
                line = line[..MaximumLoggedProcessLineCharacters] + "...<truncated>";
                truncatedLines++;
            }
            log.WriteEvent(eventName, ("line_number", index + 1),
                ("line", line), ("truncated", truncated));
        }

        for (var index = 0; index < headCount; index++) WriteLine(index);
        for (var index = tailStart; index < lines.Length; index++) WriteLine(index);

        var loggedLines = headCount + lines.Length - tailStart;
        log.WriteEvent(eventName + "_summary", ("characters", output.Length),
            ("total_lines", lines.Length), ("logged_lines", loggedLines),
            ("omitted_lines", lines.Length - loggedLines),
            ("truncated_lines", truncatedLines));
    }

    private static bool IsDevicePresent(string instanceId) =>
        CM_Locate_DevNodeW(out var node, instanceId, 0) == CrSuccess &&
        CM_Get_DevNode_Status(out var status, out var problem, node, 0) == CrSuccess &&
        problem == 0 && (status & DevNodePresent) != 0;

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

    private sealed class OperationLog
    {
        private const long MaximumBytes = 1024L * 1024;
        private readonly string _path;
        private readonly string _operationId;
        private readonly DriverOperationKind _kind;
        private readonly object _gate = new();

        internal OperationLog(string path, string operationId, DriverOperationKind kind)
        {
            _path = path;
            _operationId = operationId;
            _kind = kind;
        }

        internal void Write(string message) =>
            WriteEvent("message", ("message", message));

        internal void WriteEvent(string eventName,
            params (string Key, object? Value)[] fields) =>
            Append("INFO", eventName, fields);

        internal void WriteWarning(string eventName,
            params (string Key, object? Value)[] fields) =>
            Append("WARN", eventName, fields);

        internal void WriteError(string eventName,
            params (string Key, object? Value)[] fields) =>
            Append("ERROR", eventName, fields);

        internal void WriteException(string eventName, Exception error,
            params (string Key, object? Value)[] fields)
        {
            var all = new (string Key, object? Value)[fields.Length + 2];
            fields.CopyTo(all, 0);
            all[^2] = ("exception", error.GetType().Name);
            all[^1] = ("error", error.Message);
            Append("ERROR", eventName, all);
        }

        private void Append(string level, string eventName,
            IReadOnlyList<(string Key, object? Value)> fields)
        {
            try
            {
                var all = new (string Key, object? Value)[fields.Count + 2];
                all[0] = ("operation", _operationId);
                all[1] = ("kind", _kind);
                for (var index = 0; index < fields.Count; index++)
                    all[index + 2] = fields[index];
                lock (_gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var entry = DriverLogger.FormatEntry(level, "elevated-driver",
                        eventName, all);
                    var entryBytes = Encoding.UTF8.GetByteCount(entry);
                    if (entryBytes > MaximumBytes)
                    {
                        entry = DriverLogger.FormatEntry("WARN", "elevated-driver",
                            "oversized_log_entry_dropped", ("operation", _operationId),
                            ("kind", _kind), ("event_name", eventName),
                            ("entry_bytes", entryBytes));
                        entryBytes = Encoding.UTF8.GetByteCount(entry);
                    }
                    RotateIfNeeded(entryBytes);
                    File.AppendAllText(_path, entry, Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must not turn a recoverable driver operation into a failure.
            }
        }

        private void RotateIfNeeded(int nextEntryBytes)
        {
            if (!File.Exists(_path)) return;
            var currentBytes = new FileInfo(_path).Length;
            if (currentBytes + nextEntryBytes <= MaximumBytes) return;

            var archivePath = _path + ".1";
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (currentBytes <= MaximumBytes)
                File.Move(_path, archivePath);
            else
                File.Delete(_path);
        }
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
