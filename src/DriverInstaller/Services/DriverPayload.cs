using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

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
            throw new FileNotFoundException(
                $"Required driver file is missing: {Path.GetFileName(path)}.");
        var actual = ComputeHash(path);
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Driver payload hash mismatch for {Path.GetFileName(path)}; " +
                $"expected={DriverLogger.HashTag(expectedHash)} actual={DriverLogger.HashTag(actual)}.");
    }

    internal static string ComputeHashTag(string path) =>
        DriverLogger.HashTag(ComputeHash(path));

    internal static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return ComputeHash(stream);
    }

    internal static FileStream LockAndValidateHash(string path, string expectedHash)
    {
        EnsureNoReparsePoints(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        try
        {
            EnsureNoReparsePoints(path);
            ValidateHash(stream, path, expectedHash);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static FileStream LockAndValidateApplePackage(string path, string expectedHash)
    {
        var stream = LockAndValidateHash(path, expectedHash);
        try
        {
            if (!IsTrustedAppleSignature(path))
                throw new InvalidOperationException(
                    $"The Apple package signer is not allowed: {Path.GetFileName(path)}.");
            ValidateHash(stream, path, expectedHash);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string ComputeHash(Stream stream)
    {
        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static void ValidateHash(Stream stream, string path, string expectedHash)
    {
        var actual = ComputeHash(stream);
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Driver payload hash mismatch for {Path.GetFileName(path)}; " +
                $"expected={DriverLogger.HashTag(expectedHash)} actual={DriverLogger.HashTag(actual)}.");
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
        EnsureNoReparsePoints(path);
        Directory.CreateDirectory(path);
        EnsureNoReparsePoints(path);
    }

    internal static void CreateProtectedSystemDirectory(string path)
    {
        EnsureNoReparsePoints(path);
        var expectedSecurity = CreateProtectedSystemDirectorySecurity();
        new DirectoryInfo(path).Create(expectedSecurity);
        EnsureNoReparsePoints(path);

        var info = new DirectoryInfo(path);
        info.SetAccessControl(expectedSecurity);
        info.Refresh();
        EnsureNoReparsePoints(path);
        ValidateProtectedSystemDirectorySecurity(info.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner));
    }

    internal static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException($"The path has no filesystem root: {path}.");
        var current = fullPath.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (current.Length < root.Length) current = root;

        while (true)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(
                        $"A driver operation path contains a reparse point: {current}.");
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                break;
            current = Directory.GetParent(current)?.FullName ?? root;
        }
    }

    internal static DirectorySecurity CreateProtectedSystemDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid, null));

        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    internal static void ValidateProtectedSystemDirectorySecurity(DirectorySecurity security)
    {
        if (!security.AreAccessRulesProtected)
            throw new IOException("The driver operation directory inherits permissions.");

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var owner = (SecurityIdentifier?)security.GetOwner(typeof(SecurityIdentifier));
        if (owner is null || (!owner.Equals(system) && !owner.Equals(administrators)))
            throw new IOException("The driver operation directory owner is not trusted.");

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false,
            typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray();
        const InheritanceFlags requiredInheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        if (!HasAllowRule(rules, system, FileSystemRights.FullControl, requiredInheritance) ||
            !HasAllowRule(rules, administrators, FileSystemRights.FullControl,
                requiredInheritance) ||
            !HasAllowRule(rules, users, FileSystemRights.ReadAndExecute,
                requiredInheritance))
            throw new IOException("The driver operation directory ACL is incomplete.");

        const FileSystemRights dangerousRights = FileSystemRights.WriteData |
            FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & dangerousRights) == 0)
                continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!sid.Equals(system) && !sid.Equals(administrators))
                throw new IOException(
                    "The driver operation directory grants write access to an untrusted principal.");
        }
    }

    private static bool HasAllowRule(IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity, FileSystemRights rights, InheritanceFlags inheritance) =>
        rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
            rule.IdentityReference.Equals(identity) &&
            (rule.FileSystemRights & rights) == rights &&
            (rule.InheritanceFlags & inheritance) == inheritance &&
            rule.PropagationFlags == PropagationFlags.None);

    internal static bool IsAllowedAppleSignerSubject(string? subject) =>
        string.Equals(subject, DriverConstants.AppleSignerSubject, StringComparison.Ordinal);

    internal static bool IsTrustedAppleSignature(string path) =>
        TryGetAuthenticodeSignerSubject(path, out var subject) &&
        IsAllowedAppleSignerSubject(subject);

    internal static bool TryGetAuthenticodeSignerSubject(string path, out string? subject) =>
        TryVerifyAuthenticode(path, readSignerSubject: true, out subject);

    internal static bool IsAuthenticodeTrusted(string path) =>
        TryVerifyAuthenticode(path, readSignerSubject: false, out _);

    private static bool TryVerifyAuthenticode(string path, bool readSignerSubject,
        out string? signerSubject)
    {
        signerSubject = null;
        if (!File.Exists(path)) return false;

        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
        };
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        var data = new WinTrustData
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = 2,
            RevocationChecks = 0,
            UnionChoice = 1,
            FileInfo = fileInfoPointer,
            StateAction = 1,
            ProviderFlags = 0x00000010,
            UiContext = 0,
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            if (WinVerifyTrust(0, ref action, ref data) != 0) return false;
            if (!readSignerSubject) return true;

            var providerData = WTHelperProvDataFromStateData(data.StateData);
            if (providerData == 0) return false;
            var signerPointer = WTHelperGetProvSignerFromChain(providerData, 0,
                counterSigner: false, counterSignerIndex: 0);
            if (signerPointer == 0) return false;
            var signer = Marshal.PtrToStructure<CryptProviderSigner>(signerPointer);
            if (signer.CertChain == 0 || signer.CertChainCount == 0) return false;
            var providerCertificate = Marshal.PtrToStructure<CryptProviderCertificate>(
                signer.CertChain);
            if (providerCertificate.CertContext == 0) return false;
            var certificateContext = Marshal.PtrToStructure<CertificateContext>(
                providerCertificate.CertContext);
            if (certificateContext.EncodedCertificate == 0 ||
                certificateContext.EncodedCertificateSize == 0 ||
                certificateContext.EncodedCertificateSize > 16 * 1024 * 1024)
                return false;

            var raw = new byte[(int)certificateContext.EncodedCertificateSize];
            Marshal.Copy(certificateContext.EncodedCertificate, raw, 0, raw.Length);
            using var certificate = X509CertificateLoader.LoadCertificate(raw);
            signerSubject = certificate.Subject;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            if (data.StateData != 0)
            {
                data.StateAction = 2;
                _ = WinVerifyTrust(0, ref action, ref data);
            }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        internal uint StructSize;
        internal NativeFileTime VerifyAsOf;
        internal uint CertChainCount;
        internal nint CertChain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        internal uint StructSize;
        internal nint CertContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint EncodingType;
        internal nint EncodedCertificate;
        internal uint EncodedCertificateSize;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action,
        ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint providerData,
        uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);
}
