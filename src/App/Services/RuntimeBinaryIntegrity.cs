using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace IPhoneMirror.App.Services;

internal static class RuntimeBinaryIntegrity
{
    private const int MaximumUsbTouchBridgeRuntimeFiles = 4096;
    private const int MaximumBridgeManifestBytes = 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> WirelessHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["airplay2dll.dll"] =
                "4a534dacac5cd36f9aaa6e016db75db899e0678ffb8b0c191cabf230a2002bd9",
            ["avcodec-58.dll"] =
                "4da59c6e58d78bb2b751553d2840421850309d42f1534734543b6854205a65c4",
            ["avutil-56.dll"] =
                "85eef85c41cd5661c0ff1f9d78fed41f0f0cbc2bd094eed0449fbb68e710ff0a",
            ["dnssd.dll"] =
                "003eeb7ea109df21e62d236e24937971bd9738b6648df81f6effb810524d92bd",
            ["swresample-3.dll"] =
                "7284ddec63d4583faf645edfdea5e101182e476ae18f9584da5f60fb637536c1",
            ["swscale-5.dll"] =
                "e34410901819510e2f8c20ca103af4210707badf55ce807a81f2b164dcfa3b15",
        };

    private const string FfmpegHash =
        "1326dde4c84ff1f96fe6b8916c5bed29e163e9b5dccf995f6f3db069d143ec5e";

    private static readonly Lazy<string> PublishedFfmpegHash = new(() =>
    {
        using var stream = typeof(RuntimeBinaryIntegrity).Assembly.GetManifestResourceStream(
            "IPhoneMirror.App.Components.Ffmpeg.sha256");
        if (stream is null) return FfmpegHash;
        using var reader = new StreamReader(stream);
        var hash = reader.ReadToEnd().Trim();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid embedded FFmpeg integrity metadata.");
        return hash;
    });


    internal static bool VerifyWirelessDirectory(string directory,
        out string failure)
    {
        foreach (var (name, expected) in WirelessHashes)
        {
            if (!VerifyFile(Path.Combine(directory, name), expected, out failure))
                return false;
        }
        failure = string.Empty;
        return true;
    }

    internal static bool IsTrustedFfmpeg(string path) =>
        VerifyFile(path, PublishedFfmpegHash.Value, out _);

    /// <summary>
    /// Checks the self-developed bridge's onedir payload before it is started.
    /// The manifest is produced next to the executable during packaging, so this
    /// detects a partial installer or a damaged upgrade before PyInstaller emits
    /// an opaque module-load error on a customer machine.
    /// </summary>
    internal static bool VerifyUsbTouchBridgeRuntime(string bridgePath,
        out string failure)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(bridgePath))
            {
                failure = "bridge executable path is empty";
                return false;
            }

            var executablePath = Path.GetFullPath(bridgePath);
            var directory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(directory) || !File.Exists(executablePath) ||
                !Path.GetFileName(executablePath).Equals("iUsbBridge.exe", StringComparison.OrdinalIgnoreCase))
            {
                failure = "iUsbBridge.exe is missing";
                return false;
            }

            var manifestPath = Path.Combine(directory, "iUsbBridge.runtime.json");
            if (!File.Exists(manifestPath))
            {
                failure = "iUsbBridge.runtime.json is missing";
                return false;
            }

            EnsureRegularRuntimeEntry(executablePath);
            EnsureRegularRuntimeEntry(manifestPath);
            using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (manifestStream.Length > MaximumBridgeManifestBytes)
            {
                failure = "bridge runtime manifest exceeds the size limit";
                return false;
            }
            using var manifestDocument = JsonDocument.Parse(manifestStream);
            var root = manifestDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != 1 ||
                !root.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                failure = "bridge runtime manifest has an unsupported schema";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            var hasExecutable = false;
            var hasInternalRuntime = false;
            foreach (var entry in files.EnumerateArray())
            {
                if (++count > MaximumUsbTouchBridgeRuntimeFiles ||
                    entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("path", out var pathProperty) ||
                    pathProperty.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("sha256", out var hashProperty) ||
                    hashProperty.ValueKind != JsonValueKind.String)
                {
                    failure = "bridge runtime manifest contains an invalid entry";
                    return false;
                }

                var relative = pathProperty.GetString();
                var expectedHash = hashProperty.GetString();
                if (!TryGetSafeRuntimePath(directory, relative, out var normalized,
                        out var filePath) || !IsSha256(expectedHash) ||
                    !seen.Add(normalized))
                {
                    failure = "bridge runtime manifest contains an unsafe file entry";
                    return false;
                }

                if (string.Equals(normalized, "iUsbBridge.exe",
                        StringComparison.OrdinalIgnoreCase))
                    hasExecutable = true;
                if (normalized.StartsWith("_internal\\", StringComparison.OrdinalIgnoreCase))
                    hasInternalRuntime = true;

                if (!VerifyFile(filePath, expectedHash!, out failure))
                    return false;
            }

            if (count == 0 || !hasExecutable || !hasInternalRuntime)
            {
                failure = "bridge runtime manifest does not describe a complete payload";
                return false;
            }

            // Upgrades must not leave an old DLL/plugin that can shadow a listed
            // dependency. Do not follow junctions while inspecting the payload.
            var internalDirectory = Path.Combine(directory, "_internal");
            seen.Remove("iUsbBridge.exe");
            var entries = 0;
            ValidateBridgeRuntimeTree(directory, internalDirectory, seen, ref entries, 0);
            if (seen.Count != 0)
            {
                failure = "bridge runtime files do not match the manifest";
                return false;
            }

            failure = string.Empty;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException)
        {
            failure = $"bridge runtime could not be verified: {error.GetType().Name}";
            return false;
        }
    }

    private static bool TryGetSafeRuntimePath(string rootDirectory, string? relative,
        out string normalized, out string filePath)
    {
        normalized = string.Empty;
        filePath = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains(':') || relative.Contains('\\') ||
            (relative != "iUsbBridge.exe" && !relative.StartsWith("_internal/", StringComparison.Ordinal)))
            return false;

        var segments = relative.Replace('/', '\\').Split('\\');
        if (segments.Length == 0 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return false;

        normalized = string.Join('\\', segments);
        var fullRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        filePath = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        for (var item = new FileInfo(filePath).Directory; item is not null &&
             !item.FullName.Equals(fullRoot, StringComparison.OrdinalIgnoreCase); item = item.Parent)
            if (item.Exists) EnsureRegularRuntimeEntry(item.FullName);
        if (File.Exists(filePath)) EnsureRegularRuntimeEntry(filePath);
        return filePath.StartsWith(fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureRegularRuntimeEntry(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Bridge runtime cannot contain links.");
    }

    private static void ValidateBridgeRuntimeTree(string root, string directory,
        HashSet<string> expected, ref int entries, int depth)
    {
        if (depth > 64) throw new IOException("Bridge runtime directory nesting exceeds the limit.");
        EnsureRegularRuntimeEntry(directory);
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (++entries > MaximumUsbTouchBridgeRuntimeFiles * 2)
                throw new IOException("Bridge runtime contains too many entries.");
            EnsureRegularRuntimeEntry(entry.FullName);
            if (entry is DirectoryInfo)
                ValidateBridgeRuntimeTree(root, entry.FullName, expected, ref entries, depth + 1);
            else if (!expected.Remove(Path.GetRelativePath(root, entry.FullName)))
                throw new IOException("Bridge runtime contains an unlisted file.");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => (character is >= '0' and <= '9') ||
            (character is >= 'a' and <= 'f') || (character is >= 'A' and <= 'F'));

    private static bool VerifyFile(string path, string expected,
        out string failure)
    {
        try
        {
            if (!File.Exists(path))
            {
                failure = $"missing runtime binary: {Path.GetFileName(path)}";
                return false;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                failure = $"runtime hash mismatch: {Path.GetFileName(path)}";
                return false;
            }
            failure = string.Empty;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failure = $"runtime hash could not be read: {Path.GetFileName(path)}";
            return false;
        }
    }
}
