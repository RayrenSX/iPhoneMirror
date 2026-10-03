using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunUpdaterElevationRegressionTests()
    {
        TestUpdaterElevationRegressionsAsync().GetAwaiter().GetResult();
        Console.WriteLine("Updater elevation regressions passed: CLR overrides, encoded arguments, trusted modules and tamper rejection.");
        return 0;
    }

    private static async Task TestUpdaterElevationRegressionsAsync()
    {
        var type = typeof(App).Assembly.GetType(
            "IPhoneMirror.App.Updater.UpdateInstallerLauncher", true)!;
        const BindingFlags members = BindingFlags.Static | BindingFlags.NonPublic;
        var validate = type.GetMethod("ValidateElevationEnvironment", members)!;
        foreach (var name in new[] { "COMPLUS_InstallRoot", "COR_ENABLE_PROFILING",
                     "CORECLR_PROFILER", "DOTNET_STARTUP_HOOKS", "DEVPATH",
                     "APPDOMAIN_MANAGER_ASM", "APPDOMAIN_MANAGER_TYPE", "CLRConfigFile" })
        {
            var rejected = false;
            try { validate.Invoke(null, [new Hashtable { [name.ToLowerInvariant()] = "untrusted" }]); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
            { rejected = true; }
            InteractionAssert(rejected, "The updater accepted a CLR override: " + name);
        }
        validate.Invoke(null, [new Hashtable { ["DOTNET_ROOT"] = "", ["PSModulePath"] = "user modules" }]);
        foreach (var method in new[] { "BuildElevatedPowerShellStartInfo", "BuildUnelevatedPowerShellStartInfo" })
        {
            var start = (ProcessStartInfo)type.GetMethod(method, members)!.Invoke(null,
                [Path.GetTempPath(), Convert.ToBase64String(Encoding.Unicode.GetBytes("exit 0"))])!;
            InteractionAssert(start.WorkingDirectory == Environment.SystemDirectory &&
                start.FileName == Path.Combine(Environment.SystemDirectory,
                    "WindowsPowerShell", "v1.0", "powershell.exe") && start.UseShellExecute,
                "An update PowerShell host can load from a user working directory.");
            InteractionAssert((start.Verb == "runas") == method.Contains("Elevated", StringComparison.Ordinal),
                "The updater changed the requested privilege level.");
        }
        var installer = (string)type.GetMethod("BuildVerifiedInstallerBootstrap", members)!.Invoke(null,
            [@"C:\updates\安装 '$();\setup.exe", new string('A', 64), "/LOG=\"C:\\logs\\a '$();.log\""] )!;
        var installerScript = Encoding.Unicode.GetString(Convert.FromBase64String(installer));
        InteractionAssert(!installerScript.Contains("ConvertFrom-Json") &&
            installerScript.Contains("$security.SetOwner($administrators)") &&
            installerScript.Contains("$start.EnvironmentVariables.Clear()"),
            "The installer bootstrap still trusts inherited modules or environment paths.");

        var fixture = Path.Combine(Path.GetTempPath(), "iPhoneMirror-UpdaterRegression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var helper = Path.Combine(fixture, "helper.ps1");
            var output = Path.Combine(fixture, "output.txt");
            File.WriteAllText(helper, "param([string]$Output,[string]$Value) " +
                "Write-Output 'TRUSTED_SYSTEM_MODULE'; [IO.File]::WriteAllText($Output,$Value)", new UTF8Encoding(false));
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(helper)));
            var modules = Path.Combine(fixture, "modules");
            var module = Path.Combine(modules, "UntrustedUpdaterModule");
            Directory.CreateDirectory(module);
            File.WriteAllText(Path.Combine(module, "UntrustedUpdaterModule.psd1"),
                "@{ RootModule='UntrustedUpdaterModule.psm1'; ModuleVersion='1.0.0'; " +
                "FunctionsToExport=@('ConvertFrom-Json','Write-Output') }");
            File.WriteAllText(Path.Combine(module, "UntrustedUpdaterModule.psm1"),
                "throw 'UNTRUSTED_UPDATE_MODULE_RAN'; function ConvertFrom-Json {} function Write-Output {}");
            const string value = "更新 '$($env:USERNAME); \"quoted\" \\ newline\nend";
            var build = type.GetMethod("BuildVerifiedScriptBootstrap", members)!;
            var bootstrap = (string)build.Invoke(null,
                [helper, digest, new[] { "-Output", output, "-Value", value }, false])!;
            var accepted = await RunUpdaterSentinelAsync(bootstrap, modules);
            InteractionAssert(accepted.ExitCode == 0 && accepted.Output.Contains("TRUSTED_SYSTEM_MODULE") &&
                File.ReadAllText(output) == value && !accepted.Error.Contains("UNTRUSTED_UPDATE_MODULE_RAN"),
                "The verified updater lost literal arguments or imported an untrusted module: " + accepted.Error);

            File.AppendAllText(helper, " # changed");
            var rejected = await RunUpdaterSentinelAsync(bootstrap, modules);
            InteractionAssert(rejected.ExitCode != 0 && File.ReadAllText(output) == value &&
                !rejected.Error.Contains("UNTRUSTED_UPDATE_MODULE_RAN"),
                "The updater executed a changed helper or discovered an inherited module.");
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunUpdaterSentinelAsync(
        string encodedCommand, string untrustedModules)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCommand })
            start.ArgumentList.Add(argument);
        start.Environment["PSModulePath"] = untrustedModules;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Sentinel process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            throw;
        }
        return (process.ExitCode, await output, await error);
    }
}
