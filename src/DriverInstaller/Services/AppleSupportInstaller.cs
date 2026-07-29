using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed record AppleSupportInstallResult(
    bool Success,
    bool RequiresStoreInteraction,
    string Message);

/// <summary>
/// Installs Apple USB support without requiring the user to open Apple Devices.
/// An offline AppleMobileDeviceSupport MSI is preferred, then Apple Devices is
/// installed from Microsoft Store through winget. The official Apple iTunes
/// installer remains the network fallback. No Apple package is bundled.
/// </summary>
internal sealed class AppleSupportInstaller(DeviceCatalog catalog)
{
    private static readonly HttpClient Http = CreateHttpClient();

    internal async Task<AppleSupportInstallResult> InstallAsync()
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
        if (current.ServiceInstalled && current.ServiceName is not null &&
            await StartServiceElevatedAsync(current.ServiceName, operationId))
        {
            current = await WaitForAppleSupportAsync(TimeSpan.FromSeconds(30), operationId);
            if (current.Ready)
            {
                DriverLogger.WriteEvent("apple-support", "service_recovered",
                    ("operation", operationId), ("elapsed_ms", timer.ElapsedMilliseconds));
                return new AppleSupportInstallResult(true, false, current.Diagnostic);
            }
        }

        DriverPayload.CreateSafeDirectory(DriverConstants.PackagesRoot);
        var packageLog = Path.Combine(DriverConstants.PackagesRoot, "apple-support-install.log");
        var offlineMsi = FindOfflineMsi();
        if (offlineMsi is not null)
        {
            var packageHash = DriverPayload.ComputeHash(offlineMsi);
            DriverLogger.WriteEvent("apple-support", "offline_package_selected",
                ("operation", operationId), ("package", DriverLogger.DescribePath(offlineMsi)),
                ("signature", "trusted"),
                ("sha256", DriverLogger.HashTag(packageHash)));
            var result = await RunMsiAsync(offlineMsi, packageHash, packageLog, operationId);
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
            var wingetResult = await TryInstallAppleDevicesWithWingetAsync(operationId);
            if (wingetResult is not null && wingetResult.ExitCode == 0)
            {
                var storeReady = await WaitAndRecoverAppleSupportAsync(
                    TimeSpan.FromMinutes(3), operationId);
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
            }

            var setup = FindLocalItunesSetup();
            try
            {
                DriverLogger.WriteEvent("apple-support", "itunes_fallback_selected",
                    ("operation", operationId), ("local_package", setup is not null),
                    ("winget_attempted", wingetResult is not null),
                    ("winget_exit_code", wingetResult?.ExitCode));
                setup ??= await DownloadOfficialItunesAsync(operationId);
            }
            catch (Exception error)
            {
                DriverLogger.WriteException("apple-support", "package_acquisition_failed", error,
                    ("operation", operationId), ("elapsed_ms", timer.ElapsedMilliseconds));
                OpenMicrosoftStore();
                return new AppleSupportInstallResult(false, true,
                    DriverLocalization.Get("AppleDownloadUnavailable") + "\n" +
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

            var result = await RunElevatedAsync(setup, ["/quiet", "/norestart"],
                TimeSpan.FromMinutes(20), operationId, "itunes-installer", setup,
                packageHash);
            if (!IsInstallerSuccess(result.ExitCode))
            {
                DriverLogger.WriteError("apple-support", "itunes_install_failed",
                    ("operation", operationId), ("exit_code", result.ExitCode),
                    ("elapsed_ms", timer.ElapsedMilliseconds),
                    ("output", LimitOutput(result.CombinedOutput)));
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Format("AppleInstallerFailed", result.ExitCode,
                        LimitOutput(result.CombinedOutput), DriverLogger.Path));
            }
        }

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
        string? operationId = null)
    {
        var timer = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + timeout;
        var polls = 0;
        AppleSupportStatus status;
        do
        {
            polls++;
            status = catalog.InspectAppleSupport();
            if (status.Ready)
            {
                DriverLogger.WriteEvent("apple-support", "wait_ready",
                    ("operation", operationId), ("polls", polls),
                    ("elapsed_ms", timer.ElapsedMilliseconds));
                return status;
            }
            await Task.Delay(500);
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

    private async Task<AppleSupportStatus> WaitAndRecoverAppleSupportAsync(
        TimeSpan timeout, string operationId)
    {
        var status = await WaitForAppleSupportAsync(timeout, operationId);
        if (status.Ready || !status.ServiceInstalled || status.ServiceRunning ||
            status.ServiceName is null)
            return status;

        if (!await StartServiceElevatedAsync(status.ServiceName, operationId)) return status;
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
        IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
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
            throw new TimeoutException("Apple Devices installation timed out.");
        }
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string? FindOfflineMsi()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "AppleMobileDeviceSupport64.msi"),
            Path.Combine(DriverConstants.PackagesRoot, "AppleMobileDeviceSupport64.msi"),
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

    private static async Task<string> DownloadOfficialItunesAsync(string operationId)
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
                int read;
                while ((read = await source.ReadAsync(buffer)) != 0)
                {
                    total += read;
                    if (total > 512L * 1024 * 1024)
                        throw new InvalidOperationException(
                            "The Apple installer exceeded the size limit.");
                    await target.WriteAsync(buffer.AsMemory(0, read));
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

    private static async Task<ProcessResult> RunMsiAsync(string msi, string packageHash,
        string logPath, string operationId)
    {
        string[] arguments = ["/i", msi, "/quiet", "/norestart", "/l*v", logPath];
        return await RunElevatedAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            arguments, TimeSpan.FromMinutes(15), operationId, "msiexec", msi, packageHash);
    }

    private static bool IsInstallerSuccess(int exitCode) => exitCode is 0 or 1641 or 3010;

    private async Task<bool> StartServiceElevatedAsync(string serviceName, string operationId)
    {
        if (serviceName is not ("Apple Mobile Device Service" or "AppleMobileDeviceService"))
        {
            DriverLogger.WriteWarning("apple-support", "service_start_rejected",
                ("operation", operationId), ("service", serviceName));
            return false;
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
            return success;
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("apple-support", "service_start_exception", error,
                ("operation", operationId), ("service", serviceName),
                ("elapsed_ms", timer.ElapsedMilliseconds));
            return false;
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
