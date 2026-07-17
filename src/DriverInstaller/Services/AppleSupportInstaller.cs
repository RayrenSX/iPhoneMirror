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
/// An offline AppleMobileDeviceSupport MSI is preferred; the official Apple
/// iTunes installer is the network fallback. No Apple package is bundled.
/// </summary>
internal sealed class AppleSupportInstaller(DeviceCatalog catalog)
{
    private static readonly HttpClient Http = CreateHttpClient();

    internal async Task<AppleSupportInstallResult> InstallAsync()
    {
        var current = catalog.InspectAppleSupport();
        DriverLogger.Write("Apple support status: " + current.Diagnostic);
        if (current.Ready)
            return new AppleSupportInstallResult(true, false, current.Diagnostic);
        if (current.ServiceInstalled && current.ServiceName is not null &&
            await StartServiceElevatedAsync(current.ServiceName))
        {
            current = await WaitForAppleSupportAsync(TimeSpan.FromSeconds(30));
            if (current.Ready)
                return new AppleSupportInstallResult(true, false, current.Diagnostic);
        }

        DriverPayload.CreateSafeDirectory(DriverConstants.PackagesRoot);
        var packageLog = Path.Combine(DriverConstants.PackagesRoot, "apple-support-install.log");
        var offlineMsi = FindOfflineMsi();
        if (offlineMsi is not null)
        {
            DriverLogger.Write("Using offline AppleMobileDeviceSupport MSI: " + offlineMsi);
            var result = await RunMsiAsync(offlineMsi, packageLog);
            if (!IsInstallerSuccess(result.ExitCode))
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Format("OfflineMsiFailed", result.ExitCode,
                        LimitOutput(result.CombinedOutput), packageLog));
        }
        else
        {
            var setup = FindLocalItunesSetup();
            try
            {
                DriverLogger.Write("Using Apple official iTunes installer fallback.");
                setup ??= await DownloadOfficialItunesAsync();
            }
            catch (Exception error)
            {
                OpenMicrosoftStore();
                return new AppleSupportInstallResult(false, true,
                    DriverLocalization.Get("AppleDownloadUnavailable") + "\n" + error.Message);
            }

            if (!DriverPayload.IsAuthenticodeTrusted(setup))
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Get("AppleSignatureInvalid"));

            var result = await RunElevatedAsync(setup, ["/quiet", "/norestart"],
                TimeSpan.FromMinutes(20));
            if (!IsInstallerSuccess(result.ExitCode))
                return new AppleSupportInstallResult(false, false,
                    DriverLocalization.Format("AppleInstallerFailed", result.ExitCode,
                        LimitOutput(result.CombinedOutput), DriverLogger.Path));
        }

        var ready = await WaitForAppleSupportAsync(TimeSpan.FromSeconds(90));
        if (ready.Ready)
            return new AppleSupportInstallResult(true, false, ready.Diagnostic);

        if (ready.ServiceInstalled && ready.ServiceName is not null)
        {
            var started = await StartServiceElevatedAsync(ready.ServiceName);
            if (started)
            {
                ready = await WaitForAppleSupportAsync(TimeSpan.FromSeconds(30));
                if (ready.Ready)
                    return new AppleSupportInstallResult(true, false, ready.Diagnostic);
            }
        }

        return new AppleSupportInstallResult(false, false,
            DriverLocalization.Format("AppleServiceNotReady", packageLog));
    }

    internal void OpenMicrosoftStore()
    {
        var uri = $"ms-windows-store://pdp/?ProductId={DriverConstants.AppleStoreProductId}";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    internal async Task<AppleSupportStatus> WaitForAppleSupportAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        AppleSupportStatus status;
        do
        {
            status = catalog.InspectAppleSupport();
            if (status.Ready) return status;
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);
        return status;
    }

    internal static string? FindWinget()
    {
        var candidates = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            candidates.Add(Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "winget.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindOfflineMsi()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "AppleMobileDeviceSupport64.msi"),
            Path.Combine(DriverConstants.PackagesRoot, "AppleMobileDeviceSupport64.msi"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "iPhoneMirror.Driver", "AppleMobileDeviceSupport64.msi"),
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
            DriverPayload.IsAuthenticodeTrusted(path));
    }

    private static async Task<string> DownloadOfficialItunesAsync()
    {
        var destination = Path.Combine(DriverConstants.PackagesRoot, "iTunes64Setup.exe");
        if (File.Exists(destination) && DriverPayload.IsAuthenticodeTrusted(destination))
            return destination;
        if (File.Exists(destination)) File.Delete(destination);
        DriverLogger.Write("Downloading official Apple installer from " +
            DriverConstants.OfficialItunesDownloadUrl);

        using var response = await Http.GetAsync(DriverConstants.OfficialItunesDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            finalUri.Scheme != Uri.UriSchemeHttps ||
            !finalUri.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Apple download redirected to an untrusted host.");
        if (response.Content.Headers.ContentLength is > 512L * 1024 * 1024)
            throw new InvalidOperationException("The Apple installer is unexpectedly large.");

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) != 0)
        {
            total += read;
            if (total > 512L * 1024 * 1024)
                throw new InvalidOperationException("The Apple installer exceeded the size limit.");
            await target.WriteAsync(buffer.AsMemory(0, read));
        }
        await target.FlushAsync();
        if (!DriverPayload.IsAuthenticodeTrusted(destination))
        {
            File.Delete(destination);
            throw new InvalidOperationException("The downloaded Apple installer signature is invalid.");
        }
        return destination;
    }

    private static bool IsTrustedAppleMsi(string path) =>
        File.Exists(path) && DriverPayload.IsAuthenticodeTrusted(path);

    private static async Task<ProcessResult> RunMsiAsync(string msi, string logPath)
    {
        string[] arguments = ["/i", msi, "/quiet", "/norestart", "/l*v", logPath];
        return await RunElevatedAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            arguments, TimeSpan.FromMinutes(15));
    }

    private static bool IsInstallerSuccess(int exitCode) => exitCode is 0 or 1641 or 3010;

    private async Task<bool> StartServiceElevatedAsync(string serviceName)
    {
        if (serviceName is not ("Apple Mobile Device Service" or "AppleMobileDeviceService"))
            return false;
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        var start = new ProcessStartInfo
        {
            FileName = sc,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("start");
        start.ArgumentList.Add(serviceName);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode is 0 or 1056;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return false;
        }
    }

    private static async Task<ProcessResult> RunAsync(string executable,
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
            ?? throw new InvalidOperationException("The Apple installer did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Apple USB support installation timed out.");
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<ProcessResult> RunElevatedAsync(string executable,
        IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
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
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch
                {
                    // The elevated process may deny termination. The timeout
                    // still has to return control to the driver manager UI.
                }
                throw new TimeoutException("Apple USB support installation timed out.");
            }
            return new ProcessResult(process.ExitCode, string.Empty, string.Empty);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return new ProcessResult(1223, string.Empty, DriverLocalization.Get("UacCancelled"));
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
