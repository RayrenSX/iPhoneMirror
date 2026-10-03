using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

internal static class UxPlayComponentTests
{
    internal static async Task RunPackageAsync(string archive, string descriptorPath, string cache)
    {
        var descriptor = System.Text.Json.JsonSerializer.Deserialize<ComponentDescriptor>(
            File.ReadAllText(descriptorPath), new System.Text.Json.JsonSerializerOptions
            { PropertyNameCaseInsensitive = true }) ?? throw new Exception("Missing package descriptor");
        await UxPlayComponent.ExtractVerifiedAsync(archive, descriptor, cache, CancellationToken.None);
        var executable = UxPlayComponent.FindInstalledExecutable(descriptor, cache)
            ?? throw new Exception("Actual UxPlay package did not verify after extraction");
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add("--check-runtime");
        // Do not let an installed MSYS2/GStreamer conceal missing package DLLs.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment["PATH"] = Path.Combine(windows, "System32") + ";" + windows;
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new Exception("Could not start extracted UxPlay runtime preflight");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        Check(process.ExitCode == 0, "Actual UxPlay package runtime preflight failed");
        Console.WriteLine("Actual UxPlay component ZIP passed production extraction, hashes and runtime loading.");
    }

    internal static async Task RunAsync()
    {
        var entry = DiagnosticLogger.FormatEntry("INFO", "updater", "download_http_response",
            ("final_url", DiagnosticLogger.DownloadUrl(new Uri("https://user:password@release-assets.githubusercontent.com/path/file.zip?token=secret#fragment"))));
        Check(entry.Contains("https://release-assets.githubusercontent.com/path/file.zip") &&
            !entry.Contains("password") && !entry.Contains("secret") && !entry.Contains("fragment") && !entry.Contains("user:"),
            "Public download diagnostics preserve the path without redirect credentials");
        var root = Path.Combine(Path.GetTempPath(), "uxplay-component-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "component.zip");
            var payload = new Dictionary<string, byte[]>
            {
                ["uxplay.exe"] = Encoding.UTF8.GetBytes("receiver fixture"),
                [WirelessReceiverConfiguration.UxPlayExecutableName] = Encoding.UTF8.GetBytes("host fixture"),
                ["bin/runtime.dll"] = Encoding.UTF8.GetBytes("nested runtime fixture"),
            };
            CreateZip(archive, payload);
            var descriptor = Describe(archive, payload);
            var cache = Path.Combine(root, "cache");
            await UxPlayComponent.ExtractVerifiedAsync(archive, descriptor, cache, CancellationToken.None);
            Check(UxPlayComponent.FindInstalledExecutable(descriptor, cache) is not null, "verified component is available offline");
            await UxPlayComponent.ExtractVerifiedAsync(archive, descriptor, cache, CancellationToken.None);
            Check(!Directory.EnumerateDirectories(cache, ".install-*").Any(), "repeat install cleans staging");

            var missingFile = Path.Combine(cache, descriptor.Sha256, "uxplay.exe");
            File.Delete(missingFile);
            Check(UxPlayComponent.FindInstalledExecutable(descriptor, cache) is null, "partial cache is unavailable");
            await UxPlayComponent.ExtractVerifiedAsync(archive, descriptor, cache, CancellationToken.None);
            Check(File.Exists(missingFile), "partial component can be repaired");

            var nestedFile = Path.Combine(cache, descriptor.Sha256, "bin", "runtime.dll");
            var written = File.GetLastWriteTimeUtc(nestedFile);
            File.WriteAllBytes(nestedFile, new byte[payload["bin/runtime.dll"].Length]);
            File.SetLastWriteTimeUtc(nestedFile, written);
            Check(UxPlayComponent.FindInstalledExecutable(descriptor, cache) is null,
                "launch verification rejects same-size corruption even with preserved timestamp");
            Check(UxPlayComponent.FindInstalledExecutable(descriptor, cache, verifyHashes: false) is null,
                "failed verification invalidates availability cache");
            await UxPlayComponent.ExtractVerifiedAsync(archive, descriptor, cache, CancellationToken.None);
            var injectedFile = Path.Combine(cache, descriptor.Sha256, "bin", "unexpected.dll");
            File.WriteAllText(injectedFile, "unlisted plugin");
            Check(UxPlayComponent.FindInstalledExecutable(descriptor, cache) is null, "unlisted DLLs are rejected");
            File.Delete(injectedFile);

            using (var cacheLock = await UxPlayComponent.AcquireCacheLockAsync(cache, CancellationToken.None))
            using (var waitingCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
            {
                try
                {
                    await UxPlayComponent.ExtractVerifiedAsync(archive, descriptor, cache, waitingCancellation.Token);
                    throw new Exception("Concurrent installation bypassed cache lock");
                }
                catch (OperationCanceledException) { }
            }
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                UxPlayComponent.ExtractVerifiedAsync(archive, descriptor, cache, CancellationToken.None)));
            Check(!Directory.EnumerateDirectories(cache, ".install-*").Any(), "concurrent installers leave no staging directories");

            var outsideDirectory = Path.Combine(root, "outside");
            Directory.CreateDirectory(outsideDirectory);
            File.WriteAllBytes(Path.Combine(outsideDirectory, "runtime.dll"), payload["bin/runtime.dll"]);
            var binDirectory = Path.GetDirectoryName(nestedFile)!;
            var savedDirectory = Path.Combine(root, "saved-bin");
            Directory.Move(binDirectory, savedDirectory);
            // A directory junction does not require Windows Developer Mode.
            using (var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{binDirectory}\" \"{outsideDirectory}\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            })!)
            {
                await junction.WaitForExitAsync();
                Check(junction.ExitCode == 0, "cache-link test fixture created");
            }
            try { Check(UxPlayComponent.FindInstalledExecutable(descriptor, cache) is null, "nested cache junction is rejected"); }
            finally { Directory.Delete(binDirectory); Directory.Move(savedDirectory, binDirectory); }

            var tampered = descriptor with { Sha256 = new string('0', 64) };
            foreach (var invalid in new[]
            {
                descriptor with { Files = [descriptor.Files[0], null!] },
                descriptor with { Files = [.. descriptor.Files, new ComponentFile("uxplay.exe/child.dll", 0, new string('0', 64))] },
            })
            {
                try { UxPlayComponent.ValidateDescriptor(invalid); throw new Exception("Invalid metadata accepted"); }
                catch (InvalidDataException) { }
            }
            await Reject(() => UxPlayComponent.ExtractVerifiedAsync(archive, tampered, cache, CancellationToken.None));
            var wrongFileHash = descriptor with { Files = descriptor.Files.Select(file => file with { Sha256 = new string('0', 64) }).ToArray() };
            await Reject(() => UxPlayComponent.ExtractVerifiedAsync(archive, wrongFileHash, Path.Combine(root, "bad-file"), CancellationToken.None));
            Check(!Directory.EnumerateFiles(Path.Combine(root, "bad-file"), ".verified-package", SearchOption.AllDirectories).Any(), "failed extraction never becomes ready");

            foreach (var path in new[] { "../outside.exe", "/absolute", "bin/x:ads", "bin\\x.dll", "bin/NUL.txt", "bin/.. /x", "bin/file." })
            {
                try { UxPlayComponent.ValidateRelativePath(path); throw new Exception("Unsafe path accepted: " + path); }
                catch (InvalidDataException) { }
            }
            // The ZIP has an unsafe path while the trusted descriptor still expects safe names.
            var unsafeZip = Path.Combine(root, "unsafe.zip");
            CreateZip(unsafeZip, new Dictionary<string, byte[]> { ["../outside.exe"] = payload["uxplay.exe"], [WirelessReceiverConfiguration.UxPlayExecutableName] = payload[WirelessReceiverConfiguration.UxPlayExecutableName] });
            var unsafeDescriptor = descriptor with { Size = new FileInfo(unsafeZip).Length, Sha256 = Hash(File.ReadAllBytes(unsafeZip)) };
            await Reject(() => UxPlayComponent.ExtractVerifiedAsync(unsafeZip, unsafeDescriptor, cache, CancellationToken.None));
            Check(!File.Exists(Path.Combine(root, "outside.exe")), "archive cannot escape staging");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await UxPlayComponent.ExtractVerifiedAsync(archive, descriptor, cache, cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }

            var asset = new ReleaseAsset(descriptor.Name, new Uri(descriptor.Url), descriptor.Size, descriptor.Sha256);
            var mirrors = GitHubReleaseClient.BuildDownloadCandidates(asset, true);
            Check(mirrors.Count > 1 && mirrors[0].Host != "github.com" && mirrors[^1] == asset.DownloadUri, "mirror acceleration with upstream fallback");
            var release = ReleaseParser.ParseLatest("""
                [{"tag_name":"v1.2.3","name":"test","draft":false,"prerelease":false,"assets":[
                {"name":"iPhoneMirror-UxPlay-v1.2.3-win-x64.zip","browser_download_url":"https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.2.3/iPhoneMirror-UxPlay-v1.2.3-win-x64.zip","size":20},
                {"name":"iPhoneMirror-v1.2.3-win-x64.zip","browser_download_url":"https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.2.3/iPhoneMirror-v1.2.3-win-x64.zip","size":30}]}]
                """, true, false);
            Check(release?.ZipAsset?.Name == "iPhoneMirror-v1.2.3-win-x64.zip", "updater never installs a component as the application");
            var standalone = descriptor with { Release = "uxplay-v1.2.3",
                Url = descriptor.Url.Replace("/v1.2.3/", "/uxplay-v1.2.3/") };
            UxPlayComponent.ValidateDescriptor(standalone);
            Check(UxPlayComponent.FindInstalledExecutable(standalone, cache) is not null,
                "Changing release location preserves content-addressed cache");
            await Reject(() => { UxPlayComponent.ValidateDescriptor(standalone with { Release = "uxplay-v9.9.9" }); return Task.CompletedTask; });
            var componentOnly = ReleaseParser.ParseLatest("""
                [{"tag_name":"uxplay-v1.2.3","name":"UxPlay","draft":false,"prerelease":true,"assets":[
                {"name":"iPhoneMirror-UxPlay-v1.2.3-win-x64.zip","browser_download_url":"https://github.com/RayrenSX/iPhoneMirror/releases/download/uxplay-v1.2.3/iPhoneMirror-UxPlay-v1.2.3-win-x64.zip","size":20}]}]
                """, true, true);
            Check(componentOnly is null, "Standalone component releases never appear as application updates");
            Console.WriteLine("UxPlay extraction, cache integrity/links, concurrent installation, cancellation, repair, mirrors and release selection passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); throw new Exception("Invalid component accepted"); }
        catch (InvalidDataException) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void CreateZip(string path, Dictionary<string, byte[]> payload)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in payload) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
    }
    private static ComponentDescriptor Describe(string path, Dictionary<string, byte[]> payload) => new(1, "1.2.3",
        "iPhoneMirror-UxPlay-v1.2.3-win-x64.zip",
        "https://github.com/RayrenSX/iPhoneMirror/releases/download/v1.2.3/iPhoneMirror-UxPlay-v1.2.3-win-x64.zip",
        new FileInfo(path).Length, Hash(File.ReadAllBytes(path)),
        payload.Select(pair => new ComponentFile(pair.Key, pair.Value.Length, Hash(pair.Value))).ToArray());
}
