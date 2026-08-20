using System.IO;
using System.Security.Cryptography;

namespace IPhoneMirror.App.Services;

internal static class RuntimeBinaryIntegrity
{
    private static readonly IReadOnlyDictionary<string, string> WirelessHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["airplay2dll.dll"] =
                "30bf113be3ee48d37da57daa0658c30c0e48142c69f84be7bc4eab009633d8bb",
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
