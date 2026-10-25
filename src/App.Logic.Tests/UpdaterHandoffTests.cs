using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Services;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

internal static class UpdaterHandoffTests
{
    internal static async Task RunAsync(bool verifyFailureDialog = false)
    {
        using (var failed = StartPowerShell("exit 23"))
            await ExpectFailureAsync(UpdateInstallerLauncher.WaitForHelperAsync(failed.SafeHandle));
        using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset))
        using (var noHandoff = StartPowerShell("exit 0"))
            await ExpectFailureAsync(UpdateInstallerLauncher.WaitForHelperAsync(noHandoff.SafeHandle, ready));
        using (var completed = StartPowerShell("exit 0"))
            await UpdateInstallerLauncher.WaitForHelperAsync(completed.SafeHandle).WaitAsync(TimeSpan.FromSeconds(15));
        using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset))
        using (var stalled = StartPowerShell("[Threading.Thread]::Sleep(60000)"))
        {
            try
            {
                try
                {
                    await UpdateInstallerLauncher.WaitForHelperAsync(stalled.SafeHandle, ready,
                        TimeSpan.FromMilliseconds(150));
                    throw new InvalidOperationException("A stalled helper never timed out.");
                }
                catch (TimeoutException) { }
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                try
                {
                    await UpdateInstallerLauncher.WaitForHelperAsync(stalled.SafeHandle, ready,
                        cancellationToken: cancellation.Token);
                    throw new InvalidOperationException("A cancelled handoff kept waiting.");
                }
                catch (OperationCanceledException) { }
            }
            finally { await StopAsync(stalled); }
        }
        var scopeName = "iPhoneMirrorScopeTest-" + Guid.NewGuid().ToString("N") + ".exe";
        var scopePath = Path.Combine(Path.GetTempPath(), scopeName);
        var scopeKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + scopeName;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(scopeKey)) key.SetValue("", scopePath);
            Require(DeploymentLayout.GetRegisteredInstallHive(scopePath) == RegistryHive.CurrentUser,
                "The updater lost the current-user install scope.");
            Require(DeploymentLayout.GetRegisteredInstallHive(Path.Combine(@"C:\other", scopeName)) is null,
                "A different executable inherited another installation's scope.");
        }
        finally { Registry.CurrentUser.DeleteSubKey(scopeKey, throwOnMissingSubKey: false); }
        Require(UpdateInstallerLauncher.BuildInstallerArguments(@"C:\安装 '$(); files", true)
            .Contains(@"/CURRENTUSER /DIR=""C:\安装 '$(); files"""), "User updates lost their scope or path.");
        Require(UpdateInstallerLauncher.BuildInstallerArguments(@"C:\Program Files\iPhoneMirror", false)
            .Contains(@"/ALLUSERS /DIR=""C:\Program Files\iPhoneMirror"""), "Machine updates lost their scope or path.");

        // Exercise the actual installer wait block with a harmless process that
        // fails. No installer, UAC prompt, or real installation is involved.
        var bootstrap = Encoding.Unicode.GetString(Convert.FromBase64String(
            UpdateInstallerLauncher.BuildVerifiedInstallerBootstrap(
                Path.Combine(Path.GetTempPath(), "setup.exe"), new string('A', 64), "")));
        var waitBlock = Regex.Match(bootstrap,
            @"(?s)try \{\s*\$process.WaitForExit\(\).*?finally \{ \$process.Dispose\(\) \}");
        Require(waitBlock.Success, "Installer wait block was not found.");
        using (var installerFailure = StartPowerShell(
                   "$start = [Diagnostics.ProcessStartInfo]::new($env:ComSpec, '/c exit 23'); " +
                   "$start.UseShellExecute = $false; $start.CreateNoWindow = $true; " +
                   "$process = [Diagnostics.Process]::Start($start); " + waitBlock.Value))
            await ExpectFailureAsync(UpdateInstallerLauncher.WaitForHelperAsync(installerFailure.SafeHandle));
        Require(UpdateInstallerLauncher.BuildInstallerArguments().Contains("/FORCECLOSEAPPLICATIONS"),
            "Setup cannot finish closing an older app that minimizes to the tray.");

        var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror-Handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = Path.Combine(root, "payload");
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(payload, "iPhoneMirror.exe"), "new-app");
            File.WriteAllText(Path.Combine(payload, "iPhoneMirror.Driver.exe"), "new-driver");
            File.WriteAllText(Path.Combine(install, "iPhoneMirror.exe"), "old-app");
            var zip = Path.Combine(root, "update.zip");
            ZipFile.CreateFromDirectory(payload, zip, CompressionLevel.NoCompression, false);
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
            var repository = FindRepository();
            var helper = Path.Combine(repository, "src", "App", "tools", "updater", "Apply-ZipUpdate.ps1");

            // The real failure dialog needs an interactive desktop. Keep it in
            // the explicit --updater-handoff run; hosted CI still checks exits,
            // cancellation and payload validation without displaying windows.
            if (verifyFailureDialog)
            {
                var failureScript = Path.Combine(root, "failure.ps1");
                File.WriteAllText(failureScript, "throw 'UPDATER_POST_SHUTDOWN_TEST'", new UTF8Encoding(false));
                var failureDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(failureScript)));
                var failureBootstrap = Encoding.Unicode.GetString(Convert.FromBase64String(
                    UpdateInstallerLauncher.BuildVerifiedScriptBootstrap(failureScript, failureDigest, [])));
                failureBootstrap = failureBootstrap.Replace(
                    "[Diagnostics.Process]::GetProcessById(" + Environment.ProcessId + ")",
                    "[Diagnostics.Process]::GetProcessById(-1)");
                using var failedAfterShutdown = UpdateInstallerLauncher.StartPowerShell(
                    UpdateInstallerLauncher.BuildUnelevatedPowerShellStartInfo(root,
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(failureBootstrap))));
                using var helperProcess = Process.GetProcessById(checked((int)GetProcessId(failedAfterShutdown)));
                try
                {
                    var dialog = await WaitForFailureDialogAsync(failedAfterShutdown);
                    PostMessageW(dialog, 0x0010, 0, 0); // WM_CLOSE for this test helper only.
                    await ExpectFailureAsync(UpdateInstallerLauncher.WaitForHelperAsync(failedAfterShutdown));
                }
                finally { await StopAsync(helperProcess); }
            }

            // A live sentinel proves readiness precedes shutdown and replacement.
            // Only these private test processes are ever terminated.
            using var sentinel = StartPowerShell("[Threading.Thread]::Sleep(60000)");
            try
            {
                var eventName = @"Local\iPhoneMirror.Test.Update." + Guid.NewGuid().ToString("N");
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
                using var updater = StartZipHelper(helper, zip, digest, install, sentinel.Id, eventName);
                var output = updater.StandardOutput.ReadToEndAsync();
                var errors = updater.StandardError.ReadToEndAsync();
                try
                {
                    await UpdateInstallerLauncher.WaitForHelperAsync(updater.SafeHandle, ready)
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    Require(!sentinel.HasExited && !updater.HasExited && ready.WaitOne(0),
                        "ZIP helper did not confirm readiness before waiting for the app.");
                    Require(File.ReadAllText(Path.Combine(install, "iPhoneMirror.exe")) == "old-app",
                        "ZIP helper replaced the app before it exited.");
                    sentinel.Kill();
                    await sentinel.WaitForExitAsync();
                    await UpdateInstallerLauncher.WaitForHelperAsync(updater.SafeHandle)
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    Require(File.ReadAllText(Path.Combine(install, "iPhoneMirror.exe")) == "new-app",
                        "ZIP helper did not install after the old process exited: " + await errors);
                }
                finally { await StopAsync(updater); }
                await output;
                await errors;

                // A valid ZIP with an incomplete payload must fail before handoff.
                File.Delete(zip);
                File.Delete(Path.Combine(payload, "iPhoneMirror.Driver.exe"));
                ZipFile.CreateFromDirectory(payload, zip, CompressionLevel.NoCompression, false);
                digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
                ready.Reset();
                using var rejected = StartZipHelper(helper, zip, digest, install, Environment.ProcessId, eventName);
                var rejectedOutput = rejected.StandardOutput.ReadToEndAsync();
                var rejectedError = rejected.StandardError.ReadToEndAsync();
                try
                {
                    await ExpectFailureAsync(UpdateInstallerLauncher.WaitForHelperAsync(rejected.SafeHandle, ready));
                    Require(!ready.WaitOne(0), "An incomplete ZIP requested application shutdown.");
                    Require((await rejectedError).Contains("does not contain iPhoneMirror.Driver.exe"),
                        "The incomplete payload failed for an unexpected reason.");
                }
                finally { await StopAsync(rejected); }
                await rejectedOutput;
            }
            finally { await StopAsync(sentinel); }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("Updater handoff regressions passed: helper exit, Setup failure, ZIP readiness and payload rejection.");
    }

    internal static async Task RunPackageEndToEndAsync(string fixture, string install, string identity,
        bool elevatedInstaller = false)
    {
        Directory.CreateDirectory(DiagnosticLogger.DirectoryPath);
        var package = Path.Combine(fixture, "iPhoneMirror-Setup-v9.9.0-updater-e2e-x64.exe");
        // Inno's x86 loader must reopen its package in the elevated staging
        // directory. An x64-only fixture would miss WOW64 System32 redirection.
        using (var stream = File.OpenRead(package))
        using (var reader = new BinaryReader(stream))
        {
            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            stream.Position = peOffset + 4;
            Require(reader.ReadUInt16() == 0x014c, "The Setup fixture no longer tests an x86 loader.");
        }
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package)));
        var arguments = UpdateInstallerLauncher.BuildInstallerArguments(install, currentUser: true) +
            " /VERYSILENT /TASKS= /GROUP=" + identity + " /LOG=\"" + Path.Combine(fixture, "setup.log") + "\"";
        var bootstrap = UpdateInstallerLauncher.BuildInstallerBootstrap(package, digest, arguments,
            requiresElevation: elevatedInstaller);
        Require(bootstrap.Length < 30000, "Installer command exceeds the native shell command-line limit.");
        using (var installer = UpdateInstallerLauncher.StartPowerShell(
                   elevatedInstaller
                       ? UpdateInstallerLauncher.BuildElevatedPowerShellStartInfo(fixture, bootstrap)
                       : UpdateInstallerLauncher.BuildUnelevatedPowerShellStartInfo(fixture, bootstrap)))
            await UpdateInstallerLauncher.WaitForHelperAsync(installer).WaitAsync(TimeSpan.FromSeconds(45));
        await WaitForFileAsync(Path.Combine(install, "restarted.txt"));

        var portable = Path.Combine(fixture, "portable 安装 '$(); files");
        Directory.CreateDirectory(portable);
        File.Copy(Path.Combine(fixture, "old", "iPhoneMirror.exe"), Path.Combine(portable, "iPhoneMirror.exe"));
        var zip = Path.Combine(fixture, "portable.zip");
        ZipFile.CreateFromDirectory(Path.Combine(fixture, "payload"), zip, CompressionLevel.NoCompression, false);
        digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        var helper = Path.Combine(FindRepository(), "src", "App", "tools", "updater", "Apply-ZipUpdate.ps1");
        var helperDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(helper)));
        using var old = Process.Start(new ProcessStartInfo(Path.Combine(portable, "iPhoneMirror.exe"))
            { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
        try
        {
            await WaitForFileAsync(Path.Combine(portable, "old-ready.txt"));
            var oldBytes = File.ReadAllBytes(Path.Combine(portable, "iPhoneMirror.exe"));
            var readyName = @"Local\iPhoneMirror.Test.Ready." + Guid.NewGuid().ToString("N");
            var cancelName = @"Local\iPhoneMirror.Test.Cancel." + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
            using var cancel = new EventWaitHandle(false, EventResetMode.ManualReset, cancelName);
            var zipArguments = UpdateInstallerLauncher.BuildZipArguments(zip, portable,
                Path.Combine(portable, "iPhoneMirror.exe"), digest, old.Id)
                .Concat(["-ReadyEventName", readyName, "-CancelEventName", cancelName]).ToArray();
            bootstrap = UpdateInstallerLauncher.BuildVerifiedScriptBootstrap(helper, helperDigest, zipArguments);
            Require(bootstrap.Length < 30000, "ZIP command exceeds the native shell command-line limit.");

            cancel.Set();
            using (var cancelledBeforeReady = UpdateInstallerLauncher.StartPowerShell(
                       UpdateInstallerLauncher.BuildUnelevatedPowerShellStartInfo(fixture, bootstrap)))
            {
                await ExpectFailureAsync(UpdateInstallerLauncher.WaitForHelperAsync(cancelledBeforeReady));
                Require(!ready.WaitOne(0) && !old.HasExited && oldBytes.SequenceEqual(
                        File.ReadAllBytes(Path.Combine(portable, "iPhoneMirror.exe"))),
                    "Cancellation before readiness modified the old installation.");
            }
            cancel.Reset();

            // Cancellation after readiness must not close the app or mutate files.
            using (var cancelled = UpdateInstallerLauncher.StartPowerShell(
                       UpdateInstallerLauncher.BuildUnelevatedPowerShellStartInfo(fixture, bootstrap)))
            {
                await UpdateInstallerLauncher.WaitForHelperAsync(cancelled, ready, TimeSpan.FromSeconds(15));
                cancel.Set();
                await ExpectFailureAsync(UpdateInstallerLauncher.WaitForHelperAsync(cancelled));
                Require(!old.HasExited && oldBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(portable, "iPhoneMirror.exe"))),
                    "Cancelling the prepared helper closed or modified the old version.");
            }
            ready.Reset();
            cancel.Reset();
            using (var updater = UpdateInstallerLauncher.StartPowerShell(
                       UpdateInstallerLauncher.BuildUnelevatedPowerShellStartInfo(fixture, bootstrap)))
            {
                await UpdateInstallerLauncher.WaitForHelperAsync(updater, ready, TimeSpan.FromSeconds(15));
                Require(!old.HasExited && !File.Exists(Path.Combine(portable, "close-requested.txt")),
                    "The handoff sent a close-to-tray request before explicit app shutdown.");
                File.WriteAllText(Path.Combine(portable, "exit-old.txt"), "exit");
                await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await UpdateInstallerLauncher.WaitForHelperAsync(updater).WaitAsync(TimeSpan.FromSeconds(15));
            }
            await WaitForFileAsync(Path.Combine(portable, "restarted.txt"));
            Require(File.ReadAllBytes(Path.Combine(fixture, "payload", "iPhoneMirror.exe"))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(portable, "iPhoneMirror.exe"))),
                "Portable update did not install the expected executable.");
        }
        finally { await StopAsync(old); }
        Console.WriteLine($"PASS: actual ShellExecute bootstrap (Setup elevated={elevatedInstaller}), per-user Setup, ZIP cancellation, replacement and executable restart.");
    }

    private static async Task WaitForFileAsync(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Expected process marker missing: " + path);
            await Task.Delay(100);
        }
    }

    private static async Task<nint> WaitForFailureDialogAsync(SafeProcessHandle process)
    {
        var pid = GetProcessId(process);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            nint dialog = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var windowPid);
                if (windowPid != pid) return true;
                var text = new StringBuilder();
                EnumChildWindows(window, (child, _) =>
                {
                    var buffer = new StringBuilder(2048);
                    GetWindowTextW(child, buffer, buffer.Capacity);
                    text.Append(buffer);
                    return true;
                }, 0);
                if (!text.ToString().Contains("UPDATER_POST_SHUTDOWN_TEST")) return true;
                dialog = window;
                return false;
            }, 0);
            if (dialog != 0) return dialog;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("A failure after app shutdown did not show its error details.");
    }

    private delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetProcessId(SafeProcessHandle process);

    private static Process StartZipHelper(string helper, string zip, string digest,
        string install, int waitPid, string eventName)
    {
        var start = PowerShellStart();
        foreach (var argument in new[] { "-ExecutionPolicy", "Bypass", "-File", helper,
                     "-WaitPids", waitPid.ToString(), "-ZipPath", zip, "-ExpectedSha256", digest,
                     "-InstallDirectory", install, "-RestartExecutable", Path.Combine(install, "iPhoneMirror.exe"),
                     "-ReadyEventName", eventName, "-SkipRestart" })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static Process StartPowerShell(string script)
    {
        var start = PowerShellStart();
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return Process.Start(start)!;
    }

    private static ProcessStartInfo PowerShellStart()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        return start;
    }

    private static async Task ExpectFailureAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("A failed helper was accepted as a successful update.");
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync();
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "App", "iPhoneMirror.App.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
