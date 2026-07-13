using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace IPhoneMirror.DriverInstaller.Services;

internal static class DriverPayload
{
    private sealed record PayloadFile(string ResourceName, string RelativePath, string Hash);

    private static readonly PayloadFile[] RuntimeFiles =
    [
        new("DriverPayload.amd64.install-filter.exe", @"amd64\install-filter.exe",
            DriverConstants.InstallerHash),
        new("DriverPayload.amd64.libusb0.sys", @"amd64\libusb0.sys",
            DriverConstants.DriverHash),
        new("DriverPayload.amd64.libusb0.dll", @"amd64\libusb0.dll",
            DriverConstants.Dll64Hash),
        new("DriverPayload.x86.libusb0_x86.dll", @"x86\libusb0_x86.dll",
            DriverConstants.Dll32Hash),
    ];

    internal static string ExtractRuntimeFiles(string operationDirectory)
    {
        var payloadRoot = Path.Combine(operationDirectory, "payload");
        CreateSafeDirectory(payloadRoot);
        foreach (var item in RuntimeFiles)
        {
            var destination = GetSafeChildPath(payloadRoot, item.RelativePath);
            CreateSafeDirectory(Path.GetDirectoryName(destination)!);
            using var source = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(item.ResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded driver resource is missing: {item.ResourceName}.");
            using (var target = new FileStream(destination, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                source.CopyTo(target);
            ValidateHash(destination, item.Hash);
        }

        var driver = Path.Combine(payloadRoot, @"amd64\libusb0.sys");
        if (!IsAuthenticodeTrusted(driver))
            throw new InvalidOperationException(
                "The embedded libusb0 kernel driver signature is not trusted by Windows.");
        return payloadRoot;
    }

    internal static void ValidateHash(string path, string expectedHash)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required driver file is missing.", path);
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Driver payload hash mismatch: {path}.");
    }

    internal static string GetSafeChildPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Driver payload path must be relative.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Driver payload path escaped its operation directory.");
        return candidate;
    }

    internal static void CreateSafeDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"A driver operation directory is a reparse point: {path}.");
    }

    internal static bool IsAuthenticodeTrusted(string path)
    {
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
        };
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00000010,
                UiContext = 0,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(0, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint StructSize;
        internal nint FilePath;
        internal nint FileHandle;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal uint StructSize;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint FileInfo;
        internal uint StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal nint SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action,
        ref WinTrustData data);
}
