using System.IO;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Services;

internal sealed record ComponentFile(string Path, long Size, string Sha256);
internal sealed record ComponentDescriptor(int Schema, string Version, string Name,
    string Url, long Size, string Sha256, ComponentFile[] Files, string? Release = null)
{
    internal string ReleaseTag => Release ?? "v" + Version;
}

internal static class UxPlayComponent
{
    private const string ReceiptName = ".verified-package";
    private const string InstalledDescriptorName = "installed-component.dat";
    private static readonly byte[] DescriptorProtectionPurpose =
        System.Text.Encoding.UTF8.GetBytes("iPhoneMirror.UxPlay.ComponentDescriptor.v1");
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly Lazy<ComponentDescriptor?> BundledDescriptor = new(ReadDescriptor);
    private static readonly Lazy<ComponentDescriptor?> SavedDescriptor = new(() => ReadInstalledDescriptor(CacheRoot));
    private static ComponentDescriptor? _installedDescriptor;
    private sealed record VerifiedFile(long Length, DateTime Written, DateTime Created, string Hash);
    private static readonly ConcurrentDictionary<string, VerifiedFile> VerifiedFiles = new(StringComparer.OrdinalIgnoreCase);
    internal static ComponentDescriptor? Descriptor => SelectDescriptor(
        Volatile.Read(ref _installedDescriptor) ?? SavedDescriptor.Value, BundledDescriptor.Value);
    internal static ReleaseInfo? DownloadRelease { get; private set; }
    internal static string CacheRoot => Path.Combine(UpdateSettingsStore.UserDataDirectory,
        "Components", "UxPlay");

    // An application upgrade may require a newer host/IPC version. Keep offline
    // use for current components, but let the normal unavailable-component flow
    // fetch an update when the saved component predates this application's bundle.
    internal static ComponentDescriptor? SelectDescriptor(ComponentDescriptor? installed,
        ComponentDescriptor? bundled) => installed is null ? bundled : bundled is null ? installed :
        SemanticVersion.Parse(installed.Version) < SemanticVersion.Parse(bundled.Version) ? bundled : installed;

    private static ComponentDescriptor? ReadDescriptor()
    {
        using var stream = typeof(UxPlayComponent).Assembly.GetManifestResourceStream(
            "IPhoneMirror.App.Components.UxPlay.json");
        if (stream is null) return null;
        var value = JsonSerializer.Deserialize<ComponentDescriptor>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (value is null) throw new InvalidDataException("Missing UxPlay component metadata.");
        ValidateDescriptor(value);
        return value;
    }

    internal static void ValidateDescriptor(ComponentDescriptor descriptor)
    {
        if (descriptor.Schema != 1 || !SemanticVersion.TryParse(descriptor.Version, out _) ||
            descriptor.Size is <= 0 or > 200_000_000 || !IsHash(descriptor.Sha256) ||
            descriptor.Name != $"iPhoneMirror-UxPlay-v{descriptor.Version}-win-x64.zip" ||
            (descriptor.ReleaseTag != $"v{descriptor.Version}" && descriptor.ReleaseTag != $"uxplay-v{descriptor.Version}") ||
            descriptor.Url != $"https://github.com/RayrenSX/iPhoneMirror/releases/download/{descriptor.ReleaseTag}/{descriptor.Name}" ||
            descriptor.Files is null || descriptor.Files.Length is < 2 or > 1024)
            throw new InvalidDataException("Invalid UxPlay component metadata.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in descriptor.Files)
        {
            if (file is null) throw new InvalidDataException("Invalid UxPlay component file.");
            ValidateRelativePath(file.Path);
            if (!paths.Add(file.Path) || file.Size < 0 || file.Size > 200_000_000 || !IsHash(file.Sha256))
                throw new InvalidDataException("Invalid UxPlay component file.");
            total = checked(total + file.Size);
        }
        if (paths.Any(path => paths.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Conflicting UxPlay component paths.");
        if (total > 600_000_000 || !paths.Contains("uxplay.exe") ||
            !paths.Contains(WirelessReceiverConfiguration.UxPlayExecutableName))
            throw new InvalidDataException("Incomplete UxPlay component metadata.");
    }

    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    internal static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') ||
            path.StartsWith('/') || path.Split('/').Any(part =>
                part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                IsDeviceName(part)) || path.Equals(ReceiptName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe UxPlay component path.");
    }

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                stem[3] is >= '0' and <= '9');
    }

    internal static string? FindInstalledExecutable()
    {
        var descriptor = Descriptor;
        return descriptor is null ? null : FindInstalledExecutable(descriptor, CacheRoot, verifyHashes: false);
    }

    // Availability polling may reuse hashes for unchanged files. Every launch/probe
    // forces a fresh hash check, including after an earlier successful probe.
    internal static bool VerifyForLaunch(string executable)
    {
        var descriptor = Descriptor;
        if (descriptor is null) return true;
        var directory = Path.Combine(CacheRoot, descriptor.Sha256.ToLowerInvariant());
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(executable)),
                Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return true;
        return FindInstalledExecutable(descriptor, CacheRoot) is not null;
    }

    internal static string? FindInstalledExecutable(ComponentDescriptor descriptor, string cacheRoot,
        bool verifyHashes = true)
    {
        ValidateDescriptor(descriptor);
        var directory = Path.Combine(cacheRoot, descriptor.Sha256.ToLowerInvariant());
        try
        {
            EnsureNoLinks(directory);
            var expected = descriptor.Files.Select(file => file.Path).Append(ReceiptName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ValidateCacheTree(directory, directory, expected);
            var receipt = Path.Combine(directory, ReceiptName);
            if (expected.Count != 0 || new FileInfo(receipt).Length != 64 ||
                File.ReadAllText(receipt) != descriptor.Sha256)
                return null;
            foreach (var file in descriptor.Files)
            {
                var path = Path.Combine(directory, file.Path);
                var item = new FileInfo(path);
                var stamp = new VerifiedFile(item.Length, item.LastWriteTimeUtc, item.CreationTimeUtc, file.Sha256);
                if (item.Length != file.Size) return null;
                if (!verifyHashes && VerifiedFiles.TryGetValue(path, out var previous) && previous == stamp) continue;
                // Remove old approval before checking so failures cannot reuse it.
                VerifiedFiles.TryRemove(path, out _);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return null;
                VerifiedFiles[path] = stamp;
            }
            return Path.Combine(directory, WirelessReceiverConfiguration.UxPlayExecutableName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void ValidateCacheTree(string root, string directory, HashSet<string> expected)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("UxPlay component cannot contain links.");
            if (entry is DirectoryInfo)
            {
                var prefix = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/') + "/";
                if (!expected.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Unexpected UxPlay component directory.");
                ValidateCacheTree(root, entry.FullName, expected);
            }
            else if (!expected.Remove(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/')))
                throw new IOException("Unexpected UxPlay component file.");
        }
    }

    internal static async Task<FileStream> AcquireCacheLockAsync(string cacheRoot, CancellationToken cancellationToken)
    {
        EnsureNoLinks(cacheRoot);
        Directory.CreateDirectory(cacheRoot);
        var path = Path.Combine(cacheRoot, ".install.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("UxPlay component lock cannot be a link.");
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            { await Task.Delay(150, cancellationToken); }
        }
    }

    internal static async Task InstallAsync(IProgress<UpdateDownloadProgress> progress,
        Action installing, CancellationToken cancellationToken)
    {
        using var client = new GitHubReleaseClient(downloadRoot: Path.Combine(CacheRoot, "Downloads"));
        var descriptor = await InstallLatestAsync(CacheRoot, client, progress, installing, cancellationToken,
            minimumDescriptor: BundledDescriptor.Value);
        Volatile.Write(ref _installedDescriptor, descriptor);
    }

    internal static async Task<ComponentDescriptor> InstallLatestAsync(string cacheRoot,
        GitHubReleaseClient client, IProgress<UpdateDownloadProgress> progress,
        Action installing, CancellationToken cancellationToken, bool allowMirrorFallback = true,
        ComponentDescriptor? minimumDescriptor = null)
    {
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            DownloadRelease = null;
            var release = await client.GetLatestUxPlayAsync(allowMirrorFallback, cancellationToken);
            DownloadRelease = release;
            var asset = release.ZipAsset!;
            if (minimumDescriptor is not null &&
                release.Version < SemanticVersion.Parse(minimumDescriptor.Version))
                throw new System.Net.Http.HttpRequestException(
                    "The component required by this application has not been published.",
                    null, System.Net.HttpStatusCode.NotFound);
            using var cacheLock = await AcquireCacheLockAsync(cacheRoot, cancellationToken);
            var archivePath = Path.Combine(cacheRoot, "Archives", asset.Sha256!.ToLowerInvariant() + ".zip");
            var cached = await ReadVerifiedArchiveDescriptorAsync(archivePath, release, cancellationToken);
            // The embedded manifest remains a trusted migration path for existing
            // caches. Never reuse a file list supplied by local, editable metadata.
            if (cached is null && BundledDescriptor.Value is { } bundled &&
                bundled.Size == asset.Size && string.Equals(bundled.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var current = bundled with { Version = release.TagName[1..], Release = release.TagName,
                    Name = asset.Name, Url = asset.DownloadUri.AbsoluteUri };
                if (await Task.Run(() => FindInstalledExecutable(current, cacheRoot), cancellationToken) is not null)
                    cached = current;
            }
            if (cached is not null)
            {
                if (await Task.Run(() => FindInstalledExecutable(cached, cacheRoot), cancellationToken) is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    installing();
                    await Task.Run(() => ExtractVerifiedCoreAsync(archivePath, cached, cacheRoot,
                        cancellationToken), cancellationToken);
                }
                await SaveInstalledDescriptorAsync(cached, cacheRoot, cancellationToken);
                LogState("uxplay_runtime_ready", cached, ("source", "verified_cache"));
                return cached;
            }
            EnsureNoLinks(Path.Combine(cacheRoot, "Downloads", release.TagName));
            await client.CheckComponentAvailabilityAsync(asset, cancellationToken);
            var downloaded = await client.DownloadAsync(release, progress, cancellationToken,
                allowMirrorFallback, preferInstaller: false, stopOnOfficialNotFound: true);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                installing();
                // The release digest authenticates the complete ZIP. Build the
                // per-file receipt from those verified bytes, not an old build's manifest.
                var descriptor = await Task.Run(() => DescribeDownloadedAsync(downloaded, cancellationToken), cancellationToken);
                await Task.Run(() => ExtractVerifiedCoreAsync(downloaded.Path, descriptor, cacheRoot,
                    cancellationToken), cancellationToken);
                EnsureNoLinks(Path.GetDirectoryName(archivePath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
                EnsureRegularFile(archivePath);
                File.Move(downloaded.Path, archivePath, overwrite: true);
                await SaveInstalledDescriptorAsync(descriptor, cacheRoot, cancellationToken);
                LogState("uxplay_runtime_ready", descriptor, ("source", "latest_release"));
                return descriptor;
            }
            finally
            {
                try { File.Delete(downloaded.Path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { DiagnosticLogger.Exception("components", "uxplay_archive_cleanup_failed", error); }
            }
        }
        finally { InstallGate.Release(); }
    }

    private static async Task<ComponentDescriptor?> ReadVerifiedArchiveDescriptorAsync(string archivePath,
        ReleaseInfo release, CancellationToken cancellationToken)
    {
        try
        {
            EnsureNoLinks(Path.GetDirectoryName(archivePath)!);
            EnsureRegularFile(archivePath);
            if (!File.Exists(archivePath)) return null;
            var asset = release.ZipAsset!;
            return await Task.Run(() => DescribeDownloadedAsync(new DownloadedUpdate(
                release, asset, archivePath, true, asset.Sha256), cancellationToken), cancellationToken);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Exception("components", "uxplay_cached_archive_invalid", error);
            return null;
        }
    }

    internal static async Task<ComponentDescriptor> DescribeDownloadedAsync(DownloadedUpdate downloaded,
        CancellationToken cancellationToken)
    {
        if (!downloaded.HashVerified || !IsHash(downloaded.VerifiedSha256))
            throw new InvalidDataException("UxPlay package requires a verified release checksum.");
        await using var input = new FileStream(downloaded.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != downloaded.Asset.Size || input.Length > 200_000_000 ||
            !Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken))
                .Equals(downloaded.VerifiedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UxPlay package checksum failed.");
        input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var files = zip.Entries.Select(entry => new ComponentFile(entry.FullName, entry.Length,
            new string('0', 64))).ToArray();
        var descriptor = new ComponentDescriptor(1, downloaded.Release.TagName[1..], downloaded.Asset.Name,
            downloaded.Asset.DownloadUri.AbsoluteUri, input.Length, downloaded.VerifiedSha256!, files,
            downloaded.Release.TagName);
        // Validate all paths and expansion limits before decompressing any entry.
        ValidateDescriptor(descriptor);
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip.Entries[index];
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unexpected UxPlay package entry.");
            await using var source = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long length = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                length += count;
                if (length > entry.Length) throw new InvalidDataException("UxPlay entry exceeds its declared size.");
                hash.AppendData(buffer, 0, count);
            }
            if (length != entry.Length) throw new InvalidDataException("Incomplete UxPlay entry.");
            files[index] = files[index] with { Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() };
        }
        return descriptor;
    }

    internal static ComponentDescriptor? ReadInstalledDescriptor(string cacheRoot)
    {
        try
        {
            EnsureNoLinks(cacheRoot);
            var path = Path.Combine(cacheRoot, InstalledDescriptorName);
            EnsureRegularFile(path);
            if (!File.Exists(path)) return null;
            using var input = File.OpenRead(path);
            if (input.Length is <= 0 or > 1024 * 1024)
                throw new InvalidDataException("Invalid installed UxPlay metadata.");
            var protectedBytes = new byte[(int)input.Length];
            input.ReadExactly(protectedBytes);
            var bytes = ProtectedData.Unprotect(protectedBytes, DescriptorProtectionPurpose, DataProtectionScope.CurrentUser);
            var descriptor = JsonSerializer.Deserialize<ComponentDescriptor>(bytes)
                ?? throw new InvalidDataException("Missing installed UxPlay metadata.");
            ValidateDescriptor(descriptor);
            return descriptor;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            DiagnosticLogger.Exception("components", "uxplay_installed_metadata_invalid", error);
            return null;
        }
    }

    private static async Task SaveInstalledDescriptorAsync(ComponentDescriptor descriptor, string cacheRoot,
        CancellationToken cancellationToken)
    {
        EnsureNoLinks(cacheRoot);
        var temporary = Path.Combine(cacheRoot, ".metadata-" + Guid.NewGuid().ToString("N"));
        try
        {
            // DPAPI detects modifications to the offline receipt. It is local
            // storage protection, not a publisher signature; online cache reuse
            // independently rebuilds the file list from the release-verified ZIP.
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(descriptor),
                DescriptorProtectionPurpose, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(cacheRoot, InstalledDescriptorName);
            EnsureRegularFile(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void EnsureRegularFile(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("UxPlay metadata and archives must be regular files.");
    }

    internal static async Task InstallAsync(ComponentDescriptor descriptor, string cacheRoot,
        GitHubReleaseClient client, IProgress<UpdateDownloadProgress> progress,
        Action installing, CancellationToken cancellationToken, bool allowMirrorFallback = true)
    {
        ValidateDescriptor(descriptor);
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            using var cacheLock = await AcquireCacheLockAsync(cacheRoot, cancellationToken);
            // Explicit installation/repair must not reuse polling's metadata-only cache.
            // Hashing a complete runtime must not block the dialog's UI thread.
            if (await Task.Run(() => FindInstalledExecutable(descriptor, cacheRoot), cancellationToken) is not null)
            {
                LogState("uxplay_runtime_ready", descriptor, ("source", "verified_cache"));
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(cacheRoot);
            Directory.CreateDirectory(cacheRoot);
            var downloadRoot = Path.Combine(cacheRoot, "Downloads");
            EnsureNoLinks(Path.Combine(downloadRoot, descriptor.ReleaseTag));
            SemanticVersion.TryParse(descriptor.Version, out var version);
            var asset = new ReleaseAsset(descriptor.Name, new Uri(descriptor.Url), descriptor.Size, descriptor.Sha256);
            var release = new ReleaseInfo(descriptor.ReleaseTag, "UxPlay", string.Empty,
                DateTimeOffset.MinValue, version, version.IsPrerelease, null, asset, null);
            LogState("uxplay_component_download_started", descriptor, ("retry_count", 0));
            await client.CheckComponentAvailabilityAsync(asset, cancellationToken);
            var downloaded = await client.DownloadAsync(release, progress, cancellationToken,
                allowMirrorFallback, preferInstaller: false, stopOnOfficialNotFound: true);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                installing();
                LogState("uxplay_extraction_started", descriptor);
                await Task.Run(() => ExtractVerifiedCoreAsync(downloaded.Path, descriptor, cacheRoot,
                    cancellationToken), cancellationToken);
                LogState("uxplay_runtime_ready", descriptor, ("source", "download"));
            }
            finally
            {
                try { File.Delete(downloaded.Path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { DiagnosticLogger.Exception("components", "uxplay_archive_cleanup_failed", error); }
            }
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("components", "uxplay_component_failed", error,
                ("version", descriptor.Version), ("architecture", "x64"),
                ("release", descriptor.ReleaseTag), ("asset", descriptor.Name),
                ("http_status", (error as System.Net.Http.HttpRequestException)?.StatusCode),
                ("request_url", DiagnosticLogger.DownloadUrl(new Uri(descriptor.Url))));
            throw;
        }
        finally { InstallGate.Release(); }
    }

    private static void LogState(string name, ComponentDescriptor descriptor,
        params (string Key, object? Value)[] fields) => DiagnosticLogger.Info("components", name,
            [("version", descriptor.Version), ("architecture", "x64"), ("platform", "Windows"),
             ("release", descriptor.ReleaseTag), ("asset", descriptor.Name),
             ("download_url", DiagnosticLogger.DownloadUrl(new Uri(descriptor.Url))), .. fields]);

    internal static async Task ExtractVerifiedAsync(string archivePath, ComponentDescriptor descriptor,
        string cacheRoot, CancellationToken cancellationToken)
    {
        using var cacheLock = await AcquireCacheLockAsync(cacheRoot, cancellationToken);
        await ExtractVerifiedCoreAsync(archivePath, descriptor, cacheRoot, cancellationToken);
    }

    private static async Task ExtractVerifiedCoreAsync(string archivePath, ComponentDescriptor descriptor,
        string cacheRoot, CancellationToken cancellationToken)
    {
        ValidateDescriptor(descriptor);
        EnsureNoLinks(cacheRoot);
        Directory.CreateDirectory(cacheRoot);
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != descriptor.Size || !Convert.ToHexString(await SHA256.HashDataAsync(input,
                cancellationToken)).Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UxPlay package checksum failed.");
        input.Position = 0;
        LogState("uxplay_hash_verified", descriptor, ("sha256", descriptor.Sha256), ("downloaded_bytes", input.Length));
        var target = Path.Combine(cacheRoot, descriptor.Sha256.ToLowerInvariant());
        var staging = Path.Combine(cacheRoot, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var expected = descriptor.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            if (zip.Entries.Count != expected.Count) throw new InvalidDataException("Unexpected UxPlay package files.");
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRelativePath(entry.FullName);
                if (!expected.Remove(entry.FullName, out var file) || entry.Length != file.Size ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Unexpected UxPlay package entry.");
                var path = Path.Combine(staging, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var source = entry.Open();
                await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    written += count;
                    if (written > file.Size) throw new InvalidDataException("UxPlay entry exceeds its declared size.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                destination.Position = 0;
                if (written != file.Size || !Convert.ToHexString(await SHA256.HashDataAsync(destination,
                        cancellationToken)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("UxPlay component file checksum failed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(staging, ReceiptName), descriptor.Sha256, cancellationToken);
            EnsureNoLinks(target);
            if (Directory.Exists(target))
            {
                if (FindInstalledExecutable(descriptor, cacheRoot) is not null) return;
                // Preserve a damaged cache for diagnostics without overwriting loaded DLLs.
                await MoveDirectoryWithRetryAsync(target, target + ".invalid-" + Guid.NewGuid().ToString("N"), cancellationToken);
            }
            await MoveDirectoryWithRetryAsync(staging, target, cancellationToken);
            LogState("uxplay_runtime_installed", descriptor, ("verified_files", descriptor.Files.Length));
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                EnsureNoLinks(staging);
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task MoveDirectoryWithRetryAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        // Windows scanners may briefly hold freshly extracted executables/DLLs.
        // Retry only sharing/access errors, with a strict 1.4-second backoff budget.
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(source);
            EnsureNoLinks(destination);
            try { Directory.Move(source, destination); return; }
            catch (IOException error) when (attempt < 3 && (error.HResult & 0xFFFF) is 5 or 32 or 33)
            { await Task.Delay(200 << attempt, cancellationToken); }
        }
    }

    private static void EnsureNoLinks(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("UxPlay component directory cannot be a link.");
    }
}
