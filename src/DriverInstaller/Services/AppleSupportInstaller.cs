using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.Shared.Networking;

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
/// An offline AppleMobileDeviceSupport MSI is preferred, followed by the
/// standalone MSI published in Apple's Windows software-update catalog. The
/// full official iTunes package is retained only as a final fallback. No Apple
/// package is bundled.
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
        var supportMsi = FindOfflineMsi();
        if (supportMsi is null)
        {
            try
            {
                ReportProgress(progress, "AppleSupportDownloadingCompatibility");
                supportMsi = await DownloadOfficialMobileDeviceSupportAsync(
                    operationId, progress);
            }
            catch (Exception error)
            {
                DriverLogger.WriteException("apple-support",
                    "standalone_package_acquisition_failed", error,
                    ("operation", operationId),
                    ("elapsed_ms", timer.ElapsedMilliseconds));
            }
        }

        if (supportMsi is not null)
        {
            ReportProgress(progress, "AppleSupportInstallingCompatibility");
            var packageHash = DriverPayload.ComputeHash(supportMsi);
            DriverLogger.WriteEvent("apple-support", "support_msi_selected",
                ("operation", operationId), ("package", DriverLogger.DescribePath(supportMsi)),
                ("signature", "trusted"),
                ("sha256", DriverLogger.HashTag(packageHash)));
            var result = await RunMsiAsync(supportMsi, packageHash, packageLog, operationId);
            installerExitCode = result.ExitCode;
            if (IsUserCancellation(result.ExitCode))
            {
                DriverLogger.WriteWarning("apple-support", "offline_install_cancelled",
                    ("operation", operationId),
                    ("elapsed_ms", timer.ElapsedMilliseconds));
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Get("UacCancelled"));
            }
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
            var setup = FindLocalItunesSetup();
            try
            {
                ReportProgress(progress, setup is null
                    ? "AppleSupportDownloadingCompatibility"
                    : "AppleSupportInstallingCompatibility");
                DriverLogger.WriteEvent("apple-support", "itunes_fallback_selected",
                    ("operation", operationId), ("local_package", setup is not null),
                    ("reason", "standalone_msi_unavailable"));
                setup ??= await DownloadOfficialItunesAsync(operationId, progress);
            }
            catch (Exception error)
            {
                DriverLogger.WriteException("apple-support", "package_acquisition_failed", error,
                    ("operation", operationId), ("elapsed_ms", timer.ElapsedMilliseconds));
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Get("AppleCompatibilityDownloadUnavailable") + "\n" +
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
            if (IsUserCancellation(result.ExitCode))
            {
                DriverLogger.WriteWarning("apple-support",
                    "mobile_device_support_install_cancelled",
                    ("operation", operationId),
                    ("elapsed_ms", timer.ElapsedMilliseconds));
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Get("UacCancelled"));
            }
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
        var recovery = await WaitAndRecoverAppleSupportAsync(TimeSpan.FromSeconds(90),
            operationId);
        if (recovery.StartOutcome == ServiceStartOutcome.Cancelled)
            return new AppleSupportInstallResult(false, false,
                DriverLocalization.Get("UacCancelled"));
        var ready = recovery.Status;
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

    internal static string[] BuildWingetInstallArguments(bool force = false)
    {
        var arguments = new List<string>
        {
        "install", "--id", DriverConstants.AppleStoreProductId, "--exact",
        "--source", DriverConstants.AppleStoreSource,
        "--accept-source-agreements", "--accept-package-agreements",
        "--silent", "--disable-interactivity",
        };
        if (force) arguments.Add("--force");
        return arguments.ToArray();
    }

    internal static bool ShouldInstallAppleDevicesFromStore(AppleSupportStatus status) =>
        !status.UsbDriverInstalled || !status.ServiceInstalled;

    internal static bool ShouldRecoverExistingService(AppleSupportStatus status) =>
        status.UsbDriverInstalled && status.ServiceInstalled &&
        !status.ServiceRunning && status.ServiceName is not null;

    private static void ReportProgress(IProgress<string>? progress, string resourceKey) =>
        progress?.Report(DriverLocalization.Get(resourceKey));

    private async Task<(AppleSupportStatus Status, ServiceStartOutcome? StartOutcome)>
        WaitAndRecoverAppleSupportAsync(
        TimeSpan timeout, string operationId)
    {
        var status = await WaitForAppleSupportAsync(timeout, operationId,
            returnWhenServiceCanBeRecovered: true);
        if (status.Ready || !status.ServiceInstalled || status.ServiceRunning ||
            status.ServiceName is null)
            return (status, null);

        var startOutcome = await StartServiceElevatedAsync(status.ServiceName, operationId);
        if (startOutcome != ServiceStartOutcome.Started)
            return (status, startOutcome);
        status = await WaitForAppleSupportAsync(TimeSpan.FromSeconds(30), operationId);
        return (status, startOutcome);
    }

    private static async Task<ProcessResult?> TryInstallAppleDevicesWithWingetAsync(
        string operationId, bool force)
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
            ("source", DriverConstants.AppleStoreSource),
            ("force", force));
        try
        {
            var result = await RunCapturedProcessAsync(winget,
                BuildWingetInstallArguments(force), TimeSpan.FromMinutes(20));
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

    private static async Task<string> DownloadOfficialMobileDeviceSupportAsync(
        string operationId, IProgress<string>? progress)
    {
        var destination = Path.Combine(DriverConstants.PackagesRoot,
            DriverConstants.AppleSupportMsiFileName);
        if (IsTrustedAppleMsi(destination))
        {
            DriverLogger.WriteEvent("apple-support", "standalone_download_cache_used",
                ("operation", operationId),
                ("package", DriverLogger.DescribePath(destination)),
                ("signature", "trusted"),
                ("sha256", DriverPayload.ComputeHashTag(destination)));
            return destination;
        }
        if (File.Exists(destination)) File.Delete(destination);

        var timer = Stopwatch.StartNew();
        try
        {
            var catalogContent = await DownloadAppleUpdateCatalogAsync();
            var package = AppleSoftwareUpdateCatalog
                .ParseLatestMobileDeviceSupport64(catalogContent);
            DriverLogger.WriteEvent("apple-support", "standalone_package_selected",
                ("operation", operationId), ("product", package.ProductId),
                ("post_date", package.PostDate), ("bytes", package.Size),
                ("origin", DriverLogger.DescribeUri(package.DownloadUri.ToString())));

            var lastReportedPercent = -1;
            long lastReportedBytes = 0;
            long lastLoggedMilliseconds = -5000;
            var downloadProgress = new Progress<SegmentedDownloadProgress>(value =>
            {
                ReportDownloadProgress(progress, value.BytesReceived,
                    value.TotalBytes, value.BytesPerSecond, value.SegmentCount,
                    ref lastReportedPercent, ref lastReportedBytes);
                var elapsedMilliseconds = timer.ElapsedMilliseconds;
                if (elapsedMilliseconds - lastLoggedMilliseconds < 5000 &&
                    value.BytesReceived != value.TotalBytes) return;
                lastLoggedMilliseconds = elapsedMilliseconds;
                DriverLogger.WriteEvent("apple-support",
                    "standalone_download_progress",
                    ("operation", operationId), ("bytes", value.BytesReceived),
                    ("total_bytes", value.TotalBytes),
                    ("bytes_per_second", Math.Round(value.BytesPerSecond)),
                    ("segments", value.SegmentCount),
                    ("elapsed_ms", elapsedMilliseconds));
            });
            var result = await SegmentedHttpDownloader.DownloadAsync(Http,
                package.DownloadUri, destination,
                new SegmentedDownloadOptions(
                    AppleSoftwareUpdateCatalog.MaximumPackageBytes,
                    ExpectedBytes: package.Size,
                    MaximumConcurrency: 8,
                    MinimumSegmentBytes: 1024L * 1024),
                IsTrustedAppleDownloadUri, downloadProgress);
            DriverLogger.WriteEvent("apple-support", "standalone_download_complete",
                ("operation", operationId), ("bytes", result.BytesReceived),
                ("segments", result.SegmentCount),
                ("elapsed_ms", timer.ElapsedMilliseconds));

            if (!IsTrustedAppleMsi(destination))
                throw new InvalidOperationException(
                    "The downloaded Apple support package signature is invalid.");
            DriverLogger.WriteEvent("apple-support",
                "standalone_download_security_verified",
                ("operation", operationId),
                ("package", DriverLogger.DescribePath(destination)),
                ("signature", "trusted"),
                ("sha256", DriverPayload.ComputeHashTag(destination)),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            return destination;
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
    }

    private static async Task<string> DownloadAppleUpdateCatalogAsync()
    {
        using var response = await Http.GetAsync(
            AppleSoftwareUpdateCatalog.CatalogUrl,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ??
            throw new InvalidDataException(
                "The Apple update catalog response has no final URL.");
        if (finalUri.Scheme != Uri.UriSchemeHttps ||
            !finalUri.Host.Equals("swscan.apple.com",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The Apple update catalog redirected to an untrusted host.");
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength > AppleSoftwareUpdateCatalog.MaximumCatalogBytes)
            throw new InvalidDataException(
                "The Apple update catalog is unexpectedly large.");

        await using var input = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer);
            if (count == 0) break;
            if (output.Length + count >
                AppleSoftwareUpdateCatalog.MaximumCatalogBytes)
                throw new InvalidDataException(
                    "The Apple update catalog exceeded its size limit.");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0,
            checked((int)output.Length));
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

        try
        {
            var lastReportedPercent = -1;
            long lastReportedBytes = 0;
            long lastLoggedMilliseconds = -5000;
            var downloadProgress = new Progress<SegmentedDownloadProgress>(value =>
            {
                ReportDownloadProgress(progress, value.BytesReceived,
                    value.TotalBytes, value.BytesPerSecond, value.SegmentCount,
                    ref lastReportedPercent, ref lastReportedBytes);
                var elapsedMilliseconds = timer.ElapsedMilliseconds;
                if (elapsedMilliseconds - lastLoggedMilliseconds < 5000 &&
                    value.BytesReceived != value.TotalBytes) return;
                lastLoggedMilliseconds = elapsedMilliseconds;
                DriverLogger.WriteEvent("apple-support", "download_progress",
                    ("operation", operationId),
                    ("bytes", value.BytesReceived),
                    ("total_bytes", value.TotalBytes),
                    ("bytes_per_second", Math.Round(value.BytesPerSecond)),
                    ("segments", value.SegmentCount),
                    ("elapsed_ms", elapsedMilliseconds));
            });
            var result = await SegmentedHttpDownloader.DownloadAsync(Http,
                new Uri(DriverConstants.OfficialItunesDownloadUrl), destination,
                new SegmentedDownloadOptions(512L * 1024 * 1024,
                    MaximumConcurrency: 12,
                    MinimumSegmentBytes: 2L * 1024 * 1024),
                IsTrustedAppleDownloadUri, downloadProgress);
            DriverLogger.WriteEvent("apple-support", "download_body_complete",
                ("operation", operationId), ("bytes", result.BytesReceived),
                ("segments", result.SegmentCount),
                ("elapsed_ms", timer.ElapsedMilliseconds));

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

    private static bool IsTrustedAppleDownloadUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("apple.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase));

    private static void ReportDownloadProgress(IProgress<string>? progress,
        long downloadedBytes, long? totalBytes, double bytesPerSecond,
        int segmentCount, ref int lastReportedPercent, ref long lastReportedBytes)
    {
        if (progress is null) return;
        const double bytesPerMegabyte = 1024d * 1024d;
        if (totalBytes is > 0)
        {
            var percent = (int)Math.Clamp(downloadedBytes * 100L / totalBytes.Value,
                0, 100);
            if (percent == lastReportedPercent) return;
            lastReportedPercent = percent;
            progress.Report(DriverLocalization.Format(
                "AppleSupportDownloadProgressParallel",
                percent, downloadedBytes / bytesPerMegabyte,
                totalBytes.Value / bytesPerMegabyte,
                bytesPerSecond / bytesPerMegabyte, segmentCount));
            return;
        }

        if (downloadedBytes - lastReportedBytes < 1024 * 1024) return;
        lastReportedBytes = downloadedBytes;
        progress.Report(DriverLocalization.Format(
            "AppleSupportDownloadProgressUnknownParallel",
            downloadedBytes / bytesPerMegabyte,
            bytesPerSecond / bytesPerMegabyte, segmentCount));
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

    internal static bool IsUserCancellation(int exitCode) => exitCode == 1223;

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
                IsUserCancellation(result.ExitCode) ? ServiceStartOutcome.Cancelled :
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
