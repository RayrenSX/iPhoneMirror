using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace IPhoneMirror.App.Services;

internal static class RuntimeBinaryIntegrity
{
    private const int MaximumUsbTouchBridgeRuntimeFiles = 4096;
    private static readonly IReadOnlyDictionary<string, string> WirelessHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["airplay2dll.dll"] =
                "4a534dacac5cd36f9aaa6e016db75db899e0678ffb8b0c191cabf230a2002bd9",
            ["avcodec-58.dll"] =
                "e785b030667c0f4709ba299cb494d405e58bce6e3035d441832d35715fbadc3c",
            ["avutil-56.dll"] =
                "33e0a0a733302bbb09456a6e6c4b5036278f0a6d95a0233ff7edeb33b59ee05d",
            ["dnssd.dll"] =
                "003eeb7ea109df21e62d236e24937971bd9738b6648df81f6effb810524d92bd",
            ["swresample-3.dll"] =
                "bb8defd0517fd12379eddb0b20b0998356bfd36862b6180a9d2315f3e6128117",
            ["swscale-5.dll"] =
                "db405507989aed9915287c79a34fd6421fc7683a98edebcf4ba370a917eef311",
        };

    private const string FfmpegHash =
        "1326dde4c84ff1f96fe6b8916c5bed29e163e9b5dccf995f6f3db069d143ec5e";


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
        VerifyFile(path, FfmpegHash, out _);

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
            if (string.IsNullOrWhiteSpace(directory) || !File.Exists(executablePath))
            {
                failure = "UsbTouchBridge.exe is missing";
                return false;
            }

            var manifestPath = Path.Combine(directory, "UsbTouchBridge.runtime.json");
            if (!File.Exists(manifestPath))
            {
                failure = "UsbTouchBridge.runtime.json is missing";
                return false;
            }

            using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
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

                if (string.Equals(normalized, "UsbTouchBridge.exe",
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
            relative.Contains(':'))
            return false;

        var segments = relative.Replace('/', '\\').Split('\\');
        if (segments.Length == 0 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            return false;

        normalized = string.Join('\\', segments);
        var fullRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        filePath = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        return filePath.StartsWith(fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
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
