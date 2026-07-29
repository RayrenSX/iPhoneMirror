using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed record AppleSupportInstallResult(
    bool Success,
    bool RequiresStoreInteraction,
    string Message);

internal enum ServiceStartOutcome
{
    Started,
    Cancelled,
    Failed,
}

/// <summary>
/// Installs Apple USB support without requiring the user to open Apple Devices.
/// An offline AppleMobileDeviceSupport MSI is preferred. Apple Devices is
/// installed from Microsoft Store only when the Apple USB INF is missing; the
/// official Apple package supplies the missing service MSI. No Apple package
/// is bundled.
/// </summary>
internal sealed class AppleSupportInstaller(DeviceCatalog catalog)
{
    private static readonly HttpClient Http = CreateHttpClient();

    internal async Task<AppleSupportInstallResult> InstallAsync(
        IProgress<string>? progress = null)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        var current = catalog.InspectAppleSupport();
        DriverLogger.WriteEvent("apple-support", "install_requested",
            ("operation", operationId), ("service_installed", current.ServiceInstalled),
            ("service_running", current.ServiceRunning),
            ("usb_driver_installed", current.UsbDriverInstalled),
            ("usb_driver_inf", current.UsbDriverInf));
        if (current.Ready)
        {
            DriverLogger.WriteEvent("apple-support", "already_ready",
                ("operation", operationId), ("elapsed_ms", timer.ElapsedMilliseconds));
            return new AppleSupportInstallResult(true, false, current.Diagnostic);
        }
        if (ShouldRecoverExistingService(current))
        {
            ReportProgress(progress, "AppleSupportStartingService");
            var startOutcome = await StartServiceElevatedAsync(
                current.ServiceName!, operationId);
            if (startOutcome == ServiceStartOutcome.Cancelled)
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Get("UacCancelled"));
            if (startOutcome == ServiceStartOutcome.Started)
            {
                current = await WaitForAppleSupportAsync(TimeSpan.FromSeconds(30), operationId);
                if (current.Ready)
                {
                    DriverLogger.WriteEvent("apple-support", "service_recovered",
                        ("operation", operationId),
                        ("elapsed_ms", timer.ElapsedMilliseconds));
                    return new AppleSupportInstallResult(true, false, current.Diagnostic);
                }
            }
        }

        DriverPayload.CreateSafeDirectory(DriverConstants.PackagesRoot);
        var packageLog = Path.Combine(DriverConstants.PackagesRoot, "apple-support-install.log");
        int? installerExitCode = null;
        var offlineMsi = FindOfflineMsi();
        if (offlineMsi is not null)
        {
            ReportProgress(progress, "AppleSupportInstallingCompatibility");
            var packageHash = DriverPayload.ComputeHash(offlineMsi);
            DriverLogger.WriteEvent("apple-support", "offline_package_selected",
                ("operation", operationId), ("package", DriverLogger.DescribePath(offlineMsi)),
                ("signature", "trusted"),
                ("sha256", DriverLogger.HashTag(packageHash)));
            var result = await RunMsiAsync(offlineMsi, packageHash, packageLog, operationId);
            installerExitCode = result.ExitCode;
            if (!IsInstallerSuccess(result.ExitCode))
            {
                DriverLogger.WriteError("apple-support", "offline_install_failed",
                    ("operation", operationId), ("exit_code", result.ExitCode),
                    ("elapsed_ms", timer.ElapsedMilliseconds),
                    ("output", LimitOutput(result.CombinedOutput)));
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Format("OfflineMsiFailed", result.ExitCode,
                        LimitOutput(result.CombinedOutput), packageLog));
            }
        }
        else
        {
            ProcessResult? wingetResult = null;
            if (ShouldInstallAppleDevicesFromStore(current))
            {
                ReportProgress(progress, "AppleSupportInstallingStore");
                wingetResult = await TryInstallAppleDevicesWithWingetAsync(operationId);
            }
            else
            {
                DriverLogger.WriteEvent("apple-support", "store_install_skipped",
                    ("operation", operationId),
                    ("reason", "usb_driver_present_service_not_ready"),
                    ("service_installed", current.ServiceInstalled),
                    ("service_running", current.ServiceRunning),
                    ("usb_driver_inf", current.UsbDriverInf));
            }

            if (wingetResult is not null && wingetResult.ExitCode == 0)
            {
                ReportProgress(progress, "AppleSupportVerifying");
                var storeReady = await WaitAndRecoverAppleSupportAsync(
                    TimeSpan.FromSeconds(20), operationId);
                if (storeReady.Ready)
                {
                    DriverLogger.WriteEvent("apple-support", "store_install_completed",
                        ("operation", operationId), ("success", true),
                        ("usb_driver_inf", storeReady.UsbDriverInf),
                        ("elapsed_ms", timer.ElapsedMilliseconds));
                    return new AppleSupportInstallResult(true, false,
                        storeReady.Diagnostic);
                }
                DriverLogger.WriteWarning("apple-support", "store_install_not_ready",
                    ("operation", operationId),
                    ("service_installed", storeReady.ServiceInstalled),
                    ("service_running", storeReady.ServiceRunning),
                    ("usb_driver_installed", storeReady.UsbDriverInstalled),
                    ("usb_driver_inf", storeReady.UsbDriverInf));
                current = storeReady;
            }

            var setup = FindLocalItunesSetup();
            try
            {
                ReportProgress(progress, setup is null
                    ? "AppleSupportDownloadingCompatibility"
                    : "AppleSupportInstallingCompatibility");
                DriverLogger.WriteEvent("apple-support", "itunes_fallback_selected",
                    ("operation", operationId), ("local_package", setup is not null),
                    ("winget_attempted", wingetResult is not null),
                    ("winget_exit_code", wingetResult?.ExitCode));
                setup ??= await DownloadOfficialItunesAsync(operationId, progress);
            }
            catch (Exception error)
            {
                DriverLogger.WriteException("apple-support", "package_acquisition_failed", error,
                    ("operation", operationId), ("elapsed_ms", timer.ElapsedMilliseconds));
                var storeCanSupplyMissingDriver =
                    ShouldInstallAppleDevicesFromStore(current);
                if (storeCanSupplyMissingDriver) OpenMicrosoftStore();
                var messageKey = storeCanSupplyMissingDriver
                    ? "AppleDownloadUnavailable"
                    : "AppleCompatibilityDownloadUnavailable";
                return new AppleSupportInstallResult(false,
                    storeCanSupplyMissingDriver,
                    DriverLocalization.Get(messageKey) + "\n" +
                    DriverLogger.Sanitize(error.Message));
            }

            var packageHash = DriverPayload.ComputeHash(setup);
            var signatureTrusted = DriverPayload.IsTrustedAppleSignature(setup);
            DriverLogger.WriteEvent("apple-support", "package_security_checked",
                ("operation", operationId), ("package", DriverLogger.DescribePath(setup)),
                ("signature", signatureTrusted ? "trusted" : "rejected"),
                ("sha256", DriverLogger.HashTag(packageHash)));
            if (!signatureTrusted)
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Get("AppleSignatureInvalid"));

            ReportProgress(progress, "AppleSupportExtractingCompatibility");
            var result = await ExtractAndInstallMobileDeviceSupportAsync(setup,
                packageHash, packageLog, operationId, progress);
            installerExitCode = result.ExitCode;
            if (!IsInstallerSuccess(result.ExitCode))
            {
                DriverLogger.WriteError("apple-support", "mobile_device_support_install_failed",
                    ("operation", operationId), ("exit_code", result.ExitCode),
                    ("elapsed_ms", timer.ElapsedMilliseconds),
                    ("output", LimitOutput(result.CombinedOutput)));
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Format("AppleInstallerFailed", result.ExitCode,
                        LimitOutput(result.CombinedOutput), packageLog));
            }
        }

        ReportProgress(progress, "AppleSupportVerifying");
        var ready = await WaitAndRecoverAppleSupportAsync(TimeSpan.FromSeconds(90),
            operationId);
        if (ready.Ready)
        {
            DriverLogger.WriteEvent("apple-support", "install_completed",
                ("operation", operationId), ("success", true),
                ("usb_driver_inf", ready.UsbDriverInf),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            return new AppleSupportInstallResult(true, false, ready.Diagnostic);
        }

        DriverLogger.WriteError("apple-support", "install_not_ready",
            ("operation", operationId), ("service_installed", ready.ServiceInstalled),
            ("service_running", ready.ServiceRunning),
            ("usb_driver_installed", ready.UsbDriverInstalled),
            ("usb_driver_inf", ready.UsbDriverInf),
            ("elapsed_ms", timer.ElapsedMilliseconds));
        if (installerExitCode is { } exitCode && IsRestartRequired(exitCode))
        {
            DriverLogger.WriteWarning("apple-support", "install_restart_required",
                ("operation", operationId), ("exit_code", exitCode));
            return new AppleSupportInstallResult(false, false,
                DriverLocalization.Format("AppleRestartRequired", packageLog));
        }
        return new AppleSupportInstallResult(false, false,
            DriverLocalization.Format("AppleServiceNotReady", packageLog));
    }

    internal void OpenMicrosoftStore()
    {
        var uri = $"ms-windows-store://pdp/?ProductId={DriverConstants.AppleStoreProductId}";
        DriverLogger.WriteEvent("apple-support", "store_open_requested",
            ("product", DriverConstants.AppleStoreProductId));
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    internal async Task<AppleSupportStatus> WaitForAppleSupportAsync(TimeSpan timeout,
        string? operationId = null, bool returnWhenServiceCanBeRecovered = false)
    {
        var timer = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + timeout;
        var polls = 0;
        DriverLogger.WriteEvent("apple-support", "wait_start",
            ("operation", operationId), ("timeout_ms", timeout.TotalMilliseconds));
        AppleSupportStatus status;
        do
        {
            polls++;
            status = catalog.InspectAppleSupport(writeLog: false);
            if (status.Ready)
            {
                DriverLogger.WriteEvent("apple-support", "wait_ready",
                    ("operation", operationId), ("polls", polls),
                    ("elapsed_ms", timer.ElapsedMilliseconds));
                return status;
            }
            if (returnWhenServiceCanBeRecovered &&
                ShouldRecoverExistingService(status))
            {
                DriverLogger.WriteEvent("apple-support", "wait_service_recoverable",
                    ("operation", operationId), ("polls", polls),
                    ("service", status.ServiceName),
                    ("elapsed_ms", timer.ElapsedMilliseconds));
                return status;
            }
            await Task.Delay(1000);
        } while (DateTime.UtcNow < deadline);
        DriverLogger.WriteWarning("apple-support", "wait_timeout",
            ("operation", operationId), ("polls", polls),
            ("timeout_ms", timeout.TotalMilliseconds),
            ("service_installed", status.ServiceInstalled),
            ("service_running", status.ServiceRunning),
            ("usb_driver_installed", status.UsbDriverInstalled),
            ("usb_driver_inf", status.UsbDriverInf));
        return status;
    }

    internal static string? FindWinget()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) return null;
        var alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    internal static string[] BuildWingetInstallArguments() =>
    [
        "install", "--id", DriverConstants.AppleStoreProductId, "--exact",
        "--source", DriverConstants.AppleStoreSource,
        "--accept-source-agreements", "--accept-package-agreements",
        "--silent", "--disable-interactivity",
    ];

    internal static bool ShouldInstallAppleDevicesFromStore(AppleSupportStatus status) =>
        !status.UsbDriverInstalled;

    internal static bool ShouldRecoverExistingService(AppleSupportStatus status) =>
        status.UsbDriverInstalled && status.ServiceInstalled &&
        !status.ServiceRunning && status.ServiceName is not null;

    private static void ReportProgress(IProgress<string>? progress, string resourceKey) =>
        progress?.Report(DriverLocalization.Get(resourceKey));

    private async Task<AppleSupportStatus> WaitAndRecoverAppleSupportAsync(
        TimeSpan timeout, string operationId)
    {
        var status = await WaitForAppleSupportAsync(timeout, operationId,
            returnWhenServiceCanBeRecovered: true);
        if (status.Ready || !status.ServiceInstalled || status.ServiceRunning ||
            status.ServiceName is null)
            return status;

        if (await StartServiceElevatedAsync(status.ServiceName, operationId) !=
            ServiceStartOutcome.Started)
            return status;
        return await WaitForAppleSupportAsync(TimeSpan.FromSeconds(30), operationId);
    }

    private static async Task<ProcessResult?> TryInstallAppleDevicesWithWingetAsync(
        string operationId)
    {
        var winget = FindWinget();
        if (winget is null)
        {
            DriverLogger.WriteWarning("apple-support", "winget_unavailable",
                ("operation", operationId));
            return null;
        }

        var timer = Stopwatch.StartNew();
        DriverLogger.WriteEvent("apple-support", "winget_install_start",
            ("operation", operationId),
            ("product", DriverConstants.AppleStoreProductId),
            ("source", DriverConstants.AppleStoreSource));
        try
        {
            var result = await RunCapturedProcessAsync(winget,
                BuildWingetInstallArguments(), TimeSpan.FromMinutes(20));
            var fields = new (string Key, object? Value)[]
            {
                ("operation", operationId), ("exit_code", result.ExitCode),
                ("elapsed_ms", timer.ElapsedMilliseconds),
                ("output", LimitOutput(result.CombinedOutput)),
            };
            if (result.ExitCode == 0)
                DriverLogger.WriteEvent("apple-support", "winget_install_exit", fields);
            else
                DriverLogger.WriteWarning("apple-support", "winget_install_exit", fields);
            return result;
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("apple-support", "winget_install_failed", error,
                ("operation", operationId), ("elapsed_ms", timer.ElapsedMilliseconds));
            return new ProcessResult(-1, string.Empty, error.Message);
        }
    }

    private static async Task<ProcessResult> RunCapturedProcessAsync(string executable,
        IReadOnlyList<string> arguments, TimeSpan timeout,
        string? workingDirectory = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (workingDirectory is not null)
        {
            DriverPayload.EnsureNoReparsePoints(workingDirectory);
            start.WorkingDirectory = workingDirectory;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The package manager did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            throw new TimeoutException("The Apple support process timed out.");
        }
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string? FindOfflineMsi()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, DriverConstants.AppleSupportMsiFileName),
            Path.Combine(DriverConstants.PackagesRoot,
                DriverConstants.AppleSupportMsiFileName),
        };
        return candidates.FirstOrDefault(IsTrustedAppleMsi);
    }

    private static string? FindLocalItunesSetup()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "iTunes64Setup.exe"),
            Path.Combine(DriverConstants.PackagesRoot, "iTunes64Setup.exe"),
        };
        return candidates.FirstOrDefault(path => File.Exists(path) &&
            DriverPayload.IsTrustedAppleSignature(path));
    }

    private static async Task<string> DownloadOfficialItunesAsync(string operationId,
        IProgress<string>? progress)
    {
        var destination = Path.Combine(DriverConstants.PackagesRoot, "iTunes64Setup.exe");
        if (File.Exists(destination) && DriverPayload.IsTrustedAppleSignature(destination))
        {
            DriverLogger.WriteEvent("apple-support", "download_cache_used",
                ("operation", operationId), ("package", DriverLogger.DescribePath(destination)),
                ("signature", "trusted"), ("sha256", DriverPayload.ComputeHashTag(destination)));
            return destination;
        }
        if (File.Exists(destination)) File.Delete(destination);
        DriverLogger.WriteEvent("apple-support", "download_start",
            ("operation", operationId),
            ("origin", DriverLogger.DescribeUri(DriverConstants.OfficialItunesDownloadUrl)));

        var timer = Stopwatch.StartNew();
        using var response = await Http.GetAsync(DriverConstants.OfficialItunesDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead);
        DriverLogger.WriteEvent("apple-support", "download_headers",
            ("operation", operationId), ("status", (int)response.StatusCode),
            ("content_length", response.Content.Headers.ContentLength),
            ("final_origin", DriverLogger.DescribeUri(response.RequestMessage?.RequestUri?.ToString())));
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            finalUri.Scheme != Uri.UriSchemeHttps ||
            !finalUri.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Apple download redirected to an untrusted host.");
        if (response.Content.Headers.ContentLength is > 512L * 1024 * 1024)
            throw new InvalidOperationException("The Apple installer is unexpectedly large.");

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync();
            await using (var target = new FileStream(destination, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                var lastReportedPercent = -1;
                long lastReportedBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) != 0)
                {
                    total += read;
                    if (total > 512L * 1024 * 1024)
                        throw new InvalidOperationException(
                            "The Apple installer exceeded the size limit.");
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    ReportDownloadProgress(progress, total,
                        response.Content.Headers.ContentLength,
                        ref lastReportedPercent, ref lastReportedBytes);
                }
                await target.FlushAsync();
                DriverLogger.WriteEvent("apple-support", "download_body_complete",
                    ("operation", operationId), ("bytes", total),
                    ("elapsed_ms", timer.ElapsedMilliseconds));
            }

            // WinVerifyTrust must be able to reopen the completed file. The
            // download stream uses FileShare.None, so dispose it first.
            if (!DriverPayload.IsTrustedAppleSignature(destination))
                throw new InvalidOperationException(
                    "The downloaded Apple installer signature is invalid.");
            DriverLogger.WriteEvent("apple-support", "download_security_verified",
                ("operation", operationId), ("package", DriverLogger.DescribePath(destination)),
                ("signature", "trusted"), ("sha256", DriverPayload.ComputeHashTag(destination)),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            return destination;
        }
        catch (Exception error)
        {
            try { File.Delete(destination); } catch { }
            DriverLogger.WriteException("apple-support", "download_failed", error,
                ("operation", operationId), ("elapsed_ms", timer.ElapsedMilliseconds));
            throw;
        }
    }

    private static bool IsTrustedAppleMsi(string path) =>
        File.Exists(path) && DriverPayload.IsTrustedAppleSignature(path);

    private static void ReportDownloadProgress(IProgress<string>? progress,
        long downloadedBytes, long? totalBytes, ref int lastReportedPercent,
        ref long lastReportedBytes)
    {
        if (progress is null) return;
        const double bytesPerMegabyte = 1024d * 1024d;
        if (totalBytes is > 0)
        {
            var percent = (int)Math.Clamp(downloadedBytes * 100L / totalBytes.Value,
                0, 100);
            if (percent == lastReportedPercent) return;
            lastReportedPercent = percent;
            progress.Report(DriverLocalization.Format("AppleSupportDownloadProgress",
                percent, downloadedBytes / bytesPerMegabyte,
                totalBytes.Value / bytesPerMegabyte));
            return;
        }

        if (downloadedBytes - lastReportedBytes < 1024 * 1024) return;
        lastReportedBytes = downloadedBytes;
        progress.Report(DriverLocalization.Format("AppleSupportDownloadProgressUnknown",
            downloadedBytes / bytesPerMegabyte));
    }

    private static async Task<ProcessResult> ExtractAndInstallMobileDeviceSupportAsync(
        string setup, string setupHash, string packageLog, string operationId,
        IProgress<string>? progress)
    {
        var extractionRoot = Path.Combine(DriverConstants.PackagesRoot,
            "itunes-extract-" + Guid.NewGuid().ToString("N"));
        DriverPayload.CreateSafeDirectory(extractionRoot);
        try
        {
            DriverLogger.WriteEvent("apple-support", "itunes_extract_start",
                ("operation", operationId),
                ("directory", DriverLogger.DescribePath(extractionRoot)));
            ProcessResult extraction;
            using (DriverPayload.LockAndValidateApplePackage(setup, setupHash))
            {
                extraction = await RunCapturedProcessAsync(setup, ["/extract"],
                    TimeSpan.FromMinutes(5), extractionRoot);
            }
            DriverLogger.WriteEvent("apple-support", "itunes_extract_exit",
                ("operation", operationId), ("exit_code", extraction.ExitCode),
                ("output", LimitOutput(extraction.CombinedOutput)));
            if (!IsInstallerSuccess(extraction.ExitCode))
            {
                DriverLogger.WriteError("apple-support", "itunes_extract_failed",
                    ("operation", operationId), ("exit_code", extraction.ExitCode));
                return extraction;
            }

            DriverPayload.EnsureNoReparsePoints(extractionRoot);
            var msi = DriverPayload.GetSafeChildPath(extractionRoot,
                DriverConstants.AppleSupportMsiFileName);
            if (!IsTrustedAppleMsi(msi))
                throw new InvalidOperationException(
                    "The extracted Apple Mobile Device Support package is missing or untrusted.");
            var msiHash = DriverPayload.ComputeHash(msi);
            DriverLogger.WriteEvent("apple-support", "extracted_package_verified",
                ("operation", operationId),
                ("package", DriverLogger.DescribePath(msi)),
                ("signature", "trusted"),
                ("sha256", DriverLogger.HashTag(msiHash)));
            ReportProgress(progress, "AppleSupportInstallingCompatibility");
            return await RunMsiAsync(msi, msiHash, packageLog, operationId);
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractionRoot))
                {
                    DriverPayload.EnsureNoReparsePoints(extractionRoot);
                    Directory.Delete(extractionRoot, recursive: true);
                }
                DriverLogger.WriteEvent("apple-support", "itunes_extract_cleanup",
                    ("operation", operationId), ("success", true));
            }
            catch (Exception error)
            {
                DriverLogger.WriteException("apple-support", "itunes_extract_cleanup_failed",
                    error, ("operation", operationId),
                    ("directory", DriverLogger.DescribePath(extractionRoot)));
            }
        }
    }

    private static async Task<ProcessResult> RunMsiAsync(string msi, string packageHash,
        string logPath, string operationId)
    {
        string[] arguments = ["/i", msi, "/quiet", "/norestart", "/l*v", logPath];
        return await RunElevatedAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            arguments, TimeSpan.FromMinutes(15), operationId, "msiexec", msi, packageHash);
    }

    private static bool IsInstallerSuccess(int exitCode) => exitCode is 0 or 1641 or 3010;

    internal static bool IsRestartRequired(int exitCode) => exitCode is 1641 or 3010;

    private async Task<ServiceStartOutcome> StartServiceElevatedAsync(string serviceName,
        string operationId)
    {
        if (serviceName is not ("Apple Mobile Device Service" or "AppleMobileDeviceService"))
        {
            DriverLogger.WriteWarning("apple-support", "service_start_rejected",
                ("operation", operationId), ("service", serviceName));
            return ServiceStartOutcome.Failed;
        }
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        var timer = Stopwatch.StartNew();
        DriverLogger.WriteEvent("apple-support", "service_start_process",
            ("operation", operationId), ("service", serviceName));
        try
        {
            var result = await RunElevatedAsync(sc, ["start", serviceName],
                TimeSpan.FromSeconds(30), operationId, "sc-start-apple-service");
            var success = result.ExitCode is 0 or 1056;
            DriverLogger.WriteEvent("apple-support", "service_start_result",
                ("operation", operationId), ("service", serviceName),
                ("exit_code", result.ExitCode), ("success", success),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            return success ? ServiceStartOutcome.Started :
                result.ExitCode == 1223 ? ServiceStartOutcome.Cancelled :
                ServiceStartOutcome.Failed;
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("apple-support", "service_start_exception", error,
                ("operation", operationId), ("service", serviceName),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            return ServiceStartOutcome.Failed;
        }
    }

    private static async Task<ProcessResult> RunElevatedAsync(string executable,
        IReadOnlyList<string> arguments, TimeSpan timeout, string operationId,
        string processName, string? trustedApplePackage = null,
        string? expectedPackageHash = null)
    {
        if ((trustedApplePackage is null) != (expectedPackageHash is null))
            throw new ArgumentException(
                "A trusted Apple package path and expected hash must be provided together.");
        using var packageLock = trustedApplePackage is null
            ? null
            : DriverPayload.LockAndValidateApplePackage(trustedApplePackage,
                expectedPackageHash!);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var timer = Stopwatch.StartNew();
        DriverLogger.WriteEvent("apple-support", "elevated_process_start",
            ("operation", operationId), ("process", processName),
            ("argument_count", arguments.Count), ("timeout_ms", timeout.TotalMilliseconds));
        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The Apple installer did not start.");
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                var terminationRequested = false;
                var terminated = false;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        terminationRequested = true;
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    terminated = process.HasExited;
                }
                catch (Exception terminationError)
                {
                    DriverLogger.WriteException("apple-support", "elevated_timeout_termination_failed",
                        terminationError, ("operation", operationId), ("process", processName));
                }
                DriverLogger.WriteError("apple-support", "elevated_process_timeout",
                    ("operation", operationId), ("process", processName),
                    ("elapsed_ms", timer.ElapsedMilliseconds),
                    ("termination_requested", terminationRequested), ("terminated", terminated));
                throw new TimeoutException("Apple USB support installation timed out.");
            }
            var result = new ProcessResult(process.ExitCode, string.Empty, string.Empty);
            DriverLogger.WriteEvent("apple-support", "elevated_process_exit",
                ("operation", operationId), ("process", processName),
                ("exit_code", result.ExitCode), ("elapsed_ms", timer.ElapsedMilliseconds));
            return result;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            DriverLogger.WriteWarning("apple-support", "elevated_process_uac_cancelled",
                ("operation", operationId), ("process", processName),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            return new ProcessResult(1223, string.Empty, DriverLocalization.Get("UacCancelled"));
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("apple-support", "elevated_process_failed", error,
                ("operation", operationId), ("process", processName),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            throw;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("iPhoneMirror.Driver/1.0");
        return client;
    }

    private static string LimitOutput(string value)
    {
        const int maximum = 1200;
        var trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[^maximum..];
    }
}
