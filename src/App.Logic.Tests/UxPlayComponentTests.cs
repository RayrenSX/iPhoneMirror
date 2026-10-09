using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http;
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
            await RunLatestReleaseAsync(root, archive, payload);
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

    private static async Task RunLatestReleaseAsync(string root, string archive, Dictionary<string, byte[]> payload)
    {
        ComponentDescriptor ForVersion(string path, Dictionary<string, byte[]> content, string version)
        {
            var name = $"iPhoneMirror-UxPlay-v{version}-win-x64.zip";
            return Describe(path, content) with { Version = version, Name = name,
                Url = $"https://github.com/RayrenSX/iPhoneMirror/releases/download/v{version}/{name}" };
        }
        object Release(ComponentDescriptor component, bool draft = false, bool includeComponent = true,
            bool includeDigest = true, string? tag = null)
        {
            tag ??= component.ReleaseTag;
            var assets = new List<object>
            {
                new { name = $"iPhoneMirror-{tag}-win-x64.zip", size = 100,
                    browser_download_url = $"https://github.com/RayrenSX/iPhoneMirror/releases/download/{tag}/iPhoneMirror-{tag}-win-x64.zip" },
                new { name = "SHA256SUMS.txt", size = 0,
                    browser_download_url = $"https://github.com/RayrenSX/iPhoneMirror/releases/download/{tag}/SHA256SUMS.txt" },
            };
            if (includeComponent) assets.Add(new { name = component.Name, size = component.Size,
                browser_download_url = component.Url, digest = includeDigest ? "sha256:" + component.Sha256 : null });
            return new { tag_name = tag, name = tag, draft, prerelease = tag.Contains('-'), assets };
        }
        var older = ForVersion(archive, payload, "1.2.3");
        var latest = ForVersion(archive, payload, "2.0.0-pre");
        var ignoredDraft = ForVersion(archive, payload, "9.0.0");
        var standalone = ForVersion(archive, payload, "10.0.0");
        var json = JsonSerializer.Serialize(new[] { Release(older), Release(latest),
            Release(ignoredDraft, draft: true), Release(standalone, tag: "uxplay-v10.0.0") });
        var bytes = File.ReadAllBytes(archive);
        var packageRequests = new List<string>();
        var listRequests = 0;
        var rawRequests = 0;
        var checksumRequests = 0;
        var failApi = false;
        CancellationTokenSource? cancelFetch = null;
        using var http = new HttpClient(new ReleaseHandler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            HttpResponseMessage response;
            if (uri.Host is "api.github.com" or "raw.githubusercontent.com")
            {
                listRequests++;
                if (cancelFetch is not null)
                {
                    cancelFetch.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                if (uri.Host == "raw.githubusercontent.com") rawRequests++;
                response = new(failApi && uri.Host == "api.github.com" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                    { Content = new StringContent(json) };
            }
            else if (uri.AbsolutePath.EndsWith("/SHA256SUMS.txt"))
            {
                checksumRequests++;
                response = new(HttpStatusCode.OK) { Content = new StringContent($"{latest.Sha256}  {latest.Name}\n") };
            }
            else
            {
                Check(uri.AbsoluteUri == latest.Url, "Only the selected release's UxPlay ZIP may be downloaded");
                packageRequests.Add(uri.AbsoluteUri);
                response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            response.RequestMessage = request;
            return Task.FromResult(response);
        }));
        var cache = Path.Combine(root, "latest-cache");
        using var client = new GitHubReleaseClient(http, Path.Combine(cache, "Downloads"));
        var progress = new Progress<UpdateDownloadProgress>(_ => { });
        var installingCount = 0;
        Task<ComponentDescriptor> Install(CancellationToken token = default) =>
            UxPlayComponent.InstallLatestAsync(cache, client, progress, () => installingCount++, token, allowMirrorFallback: false);
        var installed = await Install();
        Check(installed.Version == "2.0.0-pre" && installed.Files.SequenceEqual(latest.Files),
            "Latest application prerelease is selected and its ZIP supplies the exact file hashes");
        var saved = UxPlayComponent.ReadInstalledDescriptor(cache)!;
        Check(saved.Version == installed.Version && UxPlayComponent.FindInstalledExecutable(saved, cache) is not null,
            "A fresh descriptor read can find and verify the downloaded runtime offline");
        var previousPackageRequests = packageRequests.Count;
        await Install();
        Check(listRequests == 2 && packageRequests.Count == previousPackageRequests && installingCount == 1,
            "Every explicit install refreshes release discovery but reuses an unchanged verified archive");

        var newPayload = new Dictionary<string, byte[]>(payload) { ["uxplay.exe"] = Encoding.UTF8.GetBytes("new receiver") };
        var newArchive = Path.Combine(root, "new-component.zip");
        CreateZip(newArchive, newPayload);
        latest = ForVersion(newArchive, newPayload, "3.0.0");
        bytes = File.ReadAllBytes(newArchive);
        json = JsonSerializer.Serialize(new[] { Release(older), Release(latest, includeDigest: false) });
        installed = await Install();
        Check(installed.Version == "3.0.0" && checksumRequests == 1 && installingCount == 2 &&
            UxPlayComponent.ReadInstalledDescriptor(cache)?.Sha256 == latest.Sha256,
            "A newly published ZIP replaces the selected cache using that release's checksum manifest");

        // Reproduce the review finding: edit the runtime and its plaintext file
        // hashes while retaining the official ZIP hash. Neither offline lookup
        // nor a subsequent online install may accept that substituted runtime.
        json = JsonSerializer.Serialize(new[] { Release(latest) });
        var hostPath = Path.Combine(cache, installed.Sha256, WirelessReceiverConfiguration.UxPlayExecutableName);
        var changedHost = Encoding.UTF8.GetBytes("substituted receiver host");
        File.WriteAllBytes(hostPath, changedHost);
        var forged = installed with { Files = installed.Files.Select(file =>
            file.Path == WirelessReceiverConfiguration.UxPlayExecutableName
                ? file with { Size = changedHost.Length, Sha256 = Hash(changedHost) } : file).ToArray() };
        File.WriteAllText(Path.Combine(cache, "installed-component.json"), JsonSerializer.Serialize(forged));
        Check(UxPlayComponent.FindInstalledExecutable(UxPlayComponent.ReadInstalledDescriptor(cache)!, cache) is null,
            "Editing plaintext metadata cannot approve a modified runtime for offline launch");
        var retainedArchive = Path.Combine(cache, "Archives", installed.Sha256 + ".zip");
        Check(File.Exists(retainedArchive), "The verified archive is retained separately from the runtime");
        var beforeRepairRequests = packageRequests.Count;
        installed = await Install();
        Check(packageRequests.Count == beforeRepairRequests && File.ReadAllBytes(hostPath)
                .SequenceEqual(newPayload[WirelessReceiverConfiguration.UxPlayExecutableName]),
            "Online repair rebuilds trusted hashes from the release-verified archive without redownloading it");

        var protectedPath = Path.Combine(cache, "installed-component.dat");
        var protectedBytes = File.ReadAllBytes(protectedPath);
        var alteredReceipt = protectedBytes.ToArray();
        alteredReceipt[^1] ^= 1;
        File.WriteAllBytes(protectedPath, alteredReceipt);
        Check(UxPlayComponent.ReadInstalledDescriptor(cache) is null,
            "A changed protected receipt is rejected without falling back to forged plaintext metadata");
        installed = await Install();
        Check(UxPlayComponent.FindInstalledExecutable(UxPlayComponent.ReadInstalledDescriptor(cache)!, cache) is not null,
            "The official archive recovers a damaged offline receipt");

        File.WriteAllBytes(retainedArchive, [0, 1, 2, 3]);
        beforeRepairRequests = packageRequests.Count;
        installed = await Install();
        Check(packageRequests.Count > beforeRepairRequests && Hash(File.ReadAllBytes(retainedArchive)) == latest.Sha256,
            "A damaged retained ZIP is replaced with a fresh, verified release asset");
        File.Delete(protectedPath);
        File.Delete(retainedArchive);
        File.WriteAllBytes(hostPath, changedHost);
        Check(UxPlayComponent.ReadInstalledDescriptor(cache) is null, "Legacy plaintext metadata is never a trust source");
        beforeRepairRequests = packageRequests.Count;
        installed = await Install();
        Check(packageRequests.Count > beforeRepairRequests && UxPlayComponent.FindInstalledExecutable(installed, cache) is not null,
            "Legacy forged metadata with no verified archive forces download and repair");

        failApi = true;
        var fallback = await client.GetLatestUxPlayAsync(true, CancellationToken.None);
        Check(fallback.TagName == "v3.0.0" && rawRequests == 1, "API failure uses the published release-list fallback");
        failApi = false;
        var missing = ForVersion(archive, payload, "4.0.0");
        foreach (var invalidRelease in new[]
        {
            Release(missing, includeComponent: false),
            Release(missing with { Url = older.Url }),
        })
        {
            json = JsonSerializer.Serialize(new[] { Release(latest), invalidRelease });
            try { await client.GetLatestUxPlayAsync(true, CancellationToken.None); throw new Exception("Missing latest ZIP silently used an older component"); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        }
        Check(rawRequests == 1, "An authoritative latest release with no matching ZIP must not use stale fallback data");

        latest = ForVersion(newArchive, newPayload, "4.0.0") with { Sha256 = new string('0', 64) };
        json = JsonSerializer.Serialize(new[] { Release(latest) });
        await Reject(async () => { await Install(); });
        Check(UxPlayComponent.ReadInstalledDescriptor(cache)?.Version == "3.0.0" &&
            UxPlayComponent.FindInstalledExecutable(installed, cache) is not null,
            "A bad new archive never replaces the previous verified installation");
        using (var cancellation = new CancellationTokenSource())
        {
            cancelFetch = cancellation;
            try { await Install(cancellation.Token); throw new Exception("Release discovery ignored cancellation"); }
            catch (OperationCanceledException) { }
            cancelFetch = null;
        }
        Check(UxPlayComponent.ReadInstalledDescriptor(cache)?.Version == "3.0.0",
            "Cancelled discovery leaves the installed component unchanged");

        File.WriteAllText(Path.Combine(cache, "installed-component.dat"), "{}");
        Check(UxPlayComponent.ReadInstalledDescriptor(cache) is null, "Invalid persisted metadata cannot make a component ready");
        var unsafeArchive = Path.Combine(root, "latest-unsafe.zip");
        CreateZip(unsafeArchive, new Dictionary<string, byte[]>(payload) { ["../escape.dll"] = [1] });
        var unsafeAsset = new ReleaseAsset(latest.Name, new Uri(latest.Url), new FileInfo(unsafeArchive).Length,
            Hash(File.ReadAllBytes(unsafeArchive)));
        var unsafeDownload = new DownloadedUpdate(fallback with { TagName = "v4.0.0" }, unsafeAsset, unsafeArchive, true, unsafeAsset.Sha256);
        await Reject(async () => { await UxPlayComponent.DescribeDownloadedAsync(unsafeDownload, CancellationToken.None); });
        await Reject(async () => { await UxPlayComponent.DescribeDownloadedAsync(unsafeDownload with { HashVerified = false }, CancellationToken.None); });

        // A new application carries a newer component descriptor. Its normal
        // availability path must stop selecting the old, otherwise valid cache.
        latest = ForVersion(newArchive, newPayload, "3.0.0");
        json = JsonSerializer.Serialize(new[] { Release(latest) });
        installed = await Install();
        var upgradePayload = new Dictionary<string, byte[]>(newPayload)
            { ["uxplay.exe"] = Encoding.UTF8.GetBytes("application upgrade receiver") };
        var upgradeArchive = Path.Combine(root, "upgrade-component.zip");
        CreateZip(upgradeArchive, upgradePayload);
        var required = ForVersion(upgradeArchive, upgradePayload, "4.0.0");
        var selected = UxPlayComponent.SelectDescriptor(UxPlayComponent.ReadInstalledDescriptor(cache), required)!;
        Check(selected.Version == "4.0.0" && UxPlayComponent.FindInstalledExecutable(selected, cache) is null,
            "An application upgrade exposes the download path instead of accepting its old cached host");
        Check(UxPlayComponent.SelectDescriptor(installed, older) == installed &&
            UxPlayComponent.SelectDescriptor(installed, latest) == installed &&
            UxPlayComponent.SelectDescriptor(null, latest) == latest &&
            UxPlayComponent.SelectDescriptor(installed, null) == installed,
            "Current/newer protected components and legacy embedded components remain selectable offline");
        beforeRepairRequests = packageRequests.Count;
        try
        {
            await UxPlayComponent.InstallLatestAsync(cache, client, progress, () => installingCount++,
                CancellationToken.None, false, required);
            throw new Exception("A stale release satisfied a newer application's component requirement");
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        Check(packageRequests.Count == beforeRepairRequests && UxPlayComponent.ReadInstalledDescriptor(cache)?.Version == "3.0.0",
            "An unpublished required component leaves the old installation intact without downloading an older host");
        latest = required;
        bytes = File.ReadAllBytes(upgradeArchive);
        json = JsonSerializer.Serialize(new[] { Release(latest) });
        installed = await UxPlayComponent.InstallLatestAsync(cache, client, progress, () => installingCount++,
            CancellationToken.None, false, required);
        selected = UxPlayComponent.SelectDescriptor(UxPlayComponent.ReadInstalledDescriptor(cache), required)!;
        Check(selected.Version == "4.0.0" && UxPlayComponent.FindInstalledExecutable(selected, cache) is not null,
            "The newer published component installs and remains available offline after a fresh metadata read");
        Console.WriteLine("UxPlay release discovery, archive/receipt tampering, legacy migration, offline cache, application upgrades and cancellation passed.");
    }

    private sealed class ReleaseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
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
