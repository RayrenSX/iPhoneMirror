using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.DriverInstaller.Services;
using System.Security.AccessControl;
using System.Security.Principal;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

var failures = new List<string>();

Run("serial normalization", () =>
{
    Equal("0000810100044D600A22001E",
        DriverConstants.NormalizeSerial("00008101-00044d600a22001e"));
    True(DriverConstants.IsValidSerial("0000810100044D600A22001E"));
    True(DriverConstants.IsValidSerial("0000810100044d600a22001e"));
    False(DriverConstants.IsValidSerial("00008101-00044D600A22001E"));
    False(DriverConstants.IsValidSerial("short"));
});

Run("Apple parent allowlist", () =>
{
    True(DriverConstants.IsAllowedAppleParent(
        @"USB\VID_05AC&PID_12A8\0000810100044D600A22001E"));
    False(DriverConstants.IsAllowedAppleParent(
        @"USB\VID_05AC&PID_12A8&MI_00\0000810100044D600A22001E"));
    False(DriverConstants.IsAllowedAppleParent(
        @"USB\VID_1234&PID_12A8\0000810100044D600A22001E"));
    False(DriverConstants.IsAllowedAppleParent(
        @"USB\VID_05AC&PID_12A8\..\Services\libusb0"));
});

Run("operation IDs", () =>
{
    True(DriverConstants.IsValidOperationId(Guid.NewGuid().ToString("N")));
    False(DriverConstants.IsValidOperationId("not-an-operation"));
});

Run("Apple package cache is per-user", () =>
{
    var localRoot = Path.GetFullPath(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData));
    var packagesRoot = Path.GetFullPath(DriverConstants.PackagesRoot);
    True(packagesRoot.StartsWith(localRoot + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase));
    False(packagesRoot.StartsWith(Path.GetFullPath(DriverConstants.DataRoot) +
        Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
});

Run("Apple signer subject allowlist is exact", () =>
{
    True(DriverPayload.IsAllowedAppleSignerSubject(
        "CN=Apple Inc., O=Apple Inc., L=Cupertino, S=California, C=US"));
    False(DriverPayload.IsAllowedAppleSignerSubject(
        "CN=Apple Inc., OU=Software Engineering, O=Apple Inc., L=Cupertino, S=California, C=US"));
    False(DriverPayload.IsAllowedAppleSignerSubject(
        "CN=APPLE INC., O=Apple Inc., L=Cupertino, S=California, C=US"));
    False(DriverPayload.IsAllowedAppleSignerSubject(
        "CN=Apple Inc., O=Apple Inc., L=Cupertino, S=California, C=US, OU=Injected"));
    False(DriverPayload.IsAllowedAppleSignerSubject(null));
});

Run("unsigned Apple package is rejected", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror.Driver.Tests",
        Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "iTunes64Setup.exe");
        File.WriteAllText(package, "not an Authenticode package");
        False(DriverPayload.IsTrustedAppleSignature(package));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("verified file lock blocks replacement", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror.Driver.Tests",
        Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "package.bin");
        var moved = Path.Combine(root, "replacement.bin");
        File.WriteAllText(package, "trusted payload");
        var expectedHash = DriverPayload.ComputeHash(package);
        using (DriverPayload.LockAndValidateHash(package, expectedHash))
        {
            Throws<IOException>(() => File.OpenWrite(package).Dispose());
            Throws<IOException>(() => File.Delete(package));
            Throws<IOException>(() => File.Move(package, moved, overwrite: true));
        }
        File.AppendAllText(package, " updated");
        True(File.ReadAllText(package).EndsWith(" updated", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("reparse path components are rejected", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror.Driver.Tests",
        Guid.NewGuid().ToString("N"));
    var link = Path.Combine(root, "link");
    try
    {
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception error) when (error is UnauthorizedAccessException ||
                                      (error is IOException &&
                                       (error.HResult & 0xffff) == 1314))
        {
            var fallback = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), "Application Data");
            if (File.Exists(fallback) || Directory.Exists(fallback) ||
                (File.GetAttributes(fallback) & FileAttributes.ReparsePoint) != 0)
            {
                Throws<IOException>(() => DriverPayload.EnsureNoReparsePoints(fallback));
                Throws<IOException>(() => DriverPayload.EnsureNoReparsePoints(
                    Path.Combine(fallback, "operation", "payload")));
                Console.WriteLine(
                    "Used the Windows profile junction for the reparse test.");
                return;
            }
            Console.WriteLine("Skipped reparse test: no reparse point is available.");
            return;
        }
        Throws<IOException>(() => DriverPayload.EnsureNoReparsePoints(link));
        Throws<IOException>(() => DriverPayload.EnsureNoReparsePoints(
            Path.Combine(link, "operation", "payload")));
    }
    finally
    {
        if (Directory.Exists(link)) Directory.Delete(link);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("protected operation directory ACL", () =>
{
    var security = DriverPayload.CreateProtectedSystemDirectorySecurity();
    DriverPayload.ValidateProtectedSystemDirectorySecurity(security);

    var worldWritable = DriverPayload.CreateProtectedSystemDirectorySecurity();
    worldWritable.AddAccessRule(new FileSystemAccessRule(
        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        FileSystemRights.Modify, InheritanceFlags.ContainerInherit |
        InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    Throws<IOException>(() =>
        DriverPayload.ValidateProtectedSystemDirectorySecurity(worldWritable));

    var untrustedOwner = DriverPayload.CreateProtectedSystemDirectorySecurity();
    untrustedOwner.SetOwner(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
    Throws<IOException>(() =>
        DriverPayload.ValidateProtectedSystemDirectorySecurity(untrustedOwner));

    Throws<IOException>(() => DriverPayload.ValidateProtectedSystemDirectorySecurity(
        new DirectorySecurity()));
});

Run("elevated result matches process exit code", () =>
{
    var success = new DriverOperationResult(true, false, "ok", null, null, string.Empty);
    var failure = new DriverOperationResult(false, false, "failed", null, null, string.Empty);
    True(DriverOperationClient.IsResultConsistentWithExitCode(0, success));
    False(DriverOperationClient.IsResultConsistentWithExitCode(1, success));
    True(DriverOperationClient.IsResultConsistentWithExitCode(1, failure));
    False(DriverOperationClient.IsResultConsistentWithExitCode(0, failure));
});

Run("log sanitization", () =>
{
    var raw = "password=plain-secret token:token-secret \"secret\":\"quoted secret\" " +
              "Authorization: Bearer header-secret\r\nx-api-key: api-secret " +
              "https://example.test/?signature=query-secret&ok=1";
    var sanitized = DriverLogger.Sanitize(raw);
    foreach (var secret in new[]
             {
                 "plain-secret", "token-secret", "quoted secret", "header-secret",
                 "api-secret", "query-secret",
             })
        False(sanitized.Contains(secret, StringComparison.Ordinal));
    True(sanitized.Contains("<redacted>", StringComparison.Ordinal));
    False(sanitized.Contains('\r'));
    False(sanitized.Contains('\n'));
});

Run("operation correlation IDs survive sanitization", () =>
{
    var operation = Guid.NewGuid().ToString("N");
    var deviceSerial = "0000810100044D600A22001E";
    var entry = DriverLogger.FormatEntry("INFO", "test", "correlation",
        ("operation", operation), ("serial", deviceSerial));
    True(entry.Contains($"operation={operation}", StringComparison.Ordinal));
    False(entry.Contains(deviceSerial, StringComparison.Ordinal));
});

Run("UI log rotation restarts the session", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror.Driver.Tests",
        Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "driver-ui.log");
        File.WriteAllText(path, new string('x', 1024));
        var sessionStarted = true;

        True(DriverLogger.TryRotateIfNeeded(path, 512, 2, ref sessionStarted));
        False(sessionStarted);
        True(File.Exists(path + ".1"));

        DriverLogger.EnsureSessionStarted(path, ref sessionStarted);
        True(sessionStarted);
        var active = File.ReadAllText(path);
        True(active.Contains("category=logger", StringComparison.Ordinal));
        True(active.Contains("event=session_start", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("operation log rotation is bounded", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror.Driver.Tests",
        Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operation.log");
        var logType = typeof(ElevatedDriverHost).GetNestedType("OperationLog",
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OperationLog type was not found.");
        var constructor = logType.GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, [typeof(string), typeof(string), typeof(DriverOperationKind)],
            modifiers: null)
            ?? throw new InvalidOperationException("OperationLog constructor was not found.");
        var write = logType.GetMethod("Write",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OperationLog.Write was not found.");
        var log = constructor.Invoke([path, Guid.NewGuid().ToString("N"),
            DriverOperationKind.Install]);
        var message = new string('x', 4096);
        for (var index = 0; index < 600; index++)
            write.Invoke(log, [message]);

        True(File.Exists(path));
        True(File.Exists(path + ".1"));
        var totalBytes = new FileInfo(path).Length + new FileInfo(path + ".1").Length;
        True(totalBytes <= 2L * 1024 * 1024 + 8 * 1024);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("replaceable parent driver allowlist", () =>
{
    True(DriverConstants.IsKnownReplaceableParentService("WinUSB"));
    True(DriverConstants.IsKnownReplaceableParentService("libusb0"));
    True(DriverConstants.IsKnownReplaceableParentService("libusbK"));
    False(DriverConstants.IsKnownReplaceableParentService("usbccgp"));
    False(DriverConstants.IsKnownReplaceableParentService("usbaapl64"));
    False(DriverConstants.IsKnownReplaceableParentService("unknown"));
});

Run("friendly product names", () =>
{
    Equal("iPhone 12 mini", AppleProductNames.Resolve("iPhone13,1"));
    Equal("iPhone 17", AppleProductNames.Resolve("iPhone18,3"));
    Equal("iPhone (iPhone99,9)", AppleProductNames.Resolve("iPhone99,9"));
});

Run("payload path containment", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror.Driver.Tests", "payload-root");
    var child = DriverPayload.GetSafeChildPath(root, @"amd64\libusb0.sys");
    True(child.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    Throws<InvalidOperationException>(() =>
        DriverPayload.GetSafeChildPath(root, @"..\outside.sys"));
    Throws<InvalidOperationException>(() =>
        DriverPayload.GetSafeChildPath(root, @"C:\Windows\System32\outside.sys"));
});

Run("embedded payload hashes and signature", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror.Driver.Tests",
        Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var payload = DriverPayload.ExtractRuntimeFiles(root);
        DriverPayload.ValidateHash(Path.Combine(payload, @"amd64\install-filter.exe"),
            DriverConstants.InstallerHash);
        DriverPayload.ValidateHash(Path.Combine(payload, @"amd64\libusb0.sys"),
            DriverConstants.DriverHash);
        True(DriverPayload.IsAuthenticodeTrusted(
            Path.Combine(payload, @"amd64\libusb0.sys")));
        True(DriverPayload.TryGetAuthenticodeSignerSubject(
            Path.Combine(payload, @"amd64\libusb0.sys"), out var signerSubject));
        True(!string.IsNullOrWhiteSpace(signerSubject));
        False(DriverPayload.IsAllowedAppleSignerSubject(signerSubject));
        False(DriverPayload.IsTrustedAppleSignature(
            Path.Combine(payload, @"amd64\libusb0.sys")));
        DriverPayload.ValidateHash(Path.Combine(payload, @"amd64\libusb0.dll"),
            DriverConstants.Dll64Hash);
        DriverPayload.ValidateHash(Path.Combine(payload, @"x86\libusb0_x86.dll"),
            DriverConstants.Dll32Hash);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("read-only device catalog", () =>
{
    var catalog = new DeviceCatalog();
    foreach (var device in catalog.GetAppleDevices())
    {
        True(DriverConstants.IsAllowedAppleParent(device.InstanceId));
        Equal(device.Serial, DriverConstants.NormalizeSerial(device.Serial));
        True(device.UpperFilters.All(filter => !string.IsNullOrWhiteSpace(filter)));
        True(!string.IsNullOrWhiteSpace(device.ModelName));
        Console.WriteLine($"Detected: {device.SelectionText} [{device.DetailText}]");
    }
    _ = catalog.InspectAppleSupport();
    _ = catalog.InspectLibUsbStack();
});

Run("winget discovery is side-effect free", () => _ = AppleSupportInstaller.FindWinget());

Run("Apple support requires both service and USB driver", () =>
{
    False(new AppleSupportStatus(true, true, "Apple Mobile Device Service",
        false, null, string.Empty).Ready);
    False(new AppleSupportStatus(false, false, null,
        true, "appleusb.inf", string.Empty).Ready);
    True(new AppleSupportStatus(true, true, "Apple Mobile Device Service",
        true, "appleusb.inf", string.Empty).Ready);
    Equal("AppleUsbDriverMissing",
        DeviceCatalog.ResolveAppleSupportDiagnosticKey(true, true, false));
    Equal("AppleServiceMissing",
        DeviceCatalog.ResolveAppleSupportDiagnosticKey(false, false, true));
});

Run("Apple software update catalog selects the newest standalone support MSI", () =>
{
    const string catalog = """
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><dict><key>Products</key><dict>
          <key>older</key><dict>
            <key>Packages</key><array><dict>
              <key>Size</key><integer>39915520</integer>
              <key>URL</key><string>http://swcdn.apple.com/old/AppleMobileDeviceSupport64.msi</string>
            </dict></array>
            <key>PostDate</key><date>2021-06-30T17:39:06Z</date>
          </dict>
          <key>047-76422</key><dict>
            <key>Packages</key><array>
              <dict><key>Size</key><integer>175001600</integer><key>URL</key><string>http://swcdn.apple.com/current/iTunes64.msi</string></dict>
              <dict><key>Size</key><integer>40308736</integer><key>URL</key><string>http://swcdn.apple.com/current/AppleMobileDeviceSupport64.msi</string></dict>
            </array>
            <key>PostDate</key><date>2026-03-04T18:01:11Z</date>
          </dict>
        </dict></dict></plist>
        """;
    var package = AppleSoftwareUpdateCatalog
        .ParseLatestMobileDeviceSupport64(catalog);
    Equal("047-76422", package.ProductId);
    Equal(40308736L, package.Size);
    Equal("https", package.DownloadUri.Scheme);
    Equal("swcdn.apple.com", package.DownloadUri.Host);
    Equal(new DateTimeOffset(2026, 3, 4, 18, 1, 11, TimeSpan.Zero),
        package.PostDate);
});

Run("Apple software update catalog rejects an untrusted package host", () =>
{
    const string catalog = """
        <plist version="1.0"><dict><key>Products</key><dict>
          <key>malicious</key><dict>
            <key>Packages</key><array><dict>
              <key>Size</key><integer>40308736</integer>
              <key>URL</key><string>https://example.test/AppleMobileDeviceSupport64.msi</string>
            </dict></array>
            <key>PostDate</key><date>2026-03-04T18:01:11Z</date>
          </dict>
        </dict></dict></plist>
        """;
    Throws<InvalidDataException>(() => AppleSoftwareUpdateCatalog
        .ParseLatestMobileDeviceSupport64(catalog));
});

Run("Apple support install path distinguishes a missing service from a missing INF", () =>
{
    True(AppleSupportInstaller.ShouldInstallAppleDevicesFromStore(
        new AppleSupportStatus(false, false, null,
            false, null, string.Empty)));
    True(AppleSupportInstaller.ShouldInstallAppleDevicesFromStore(
        new AppleSupportStatus(true, true, "Apple Mobile Device Service",
            false, null, string.Empty)));
    True(AppleSupportInstaller.ShouldInstallAppleDevicesFromStore(
        new AppleSupportStatus(false, false, null,
            true, "appleusb.inf", string.Empty)));
    False(AppleSupportInstaller.ShouldInstallAppleDevicesFromStore(
        new AppleSupportStatus(true, true, "Apple Mobile Device Service",
            true, "usbaapl64.inf", string.Empty)));

    False(AppleSupportInstaller.ShouldRecoverExistingService(
        new AppleSupportStatus(true, false, "Apple Mobile Device Service",
            false, null, string.Empty)));
    False(AppleSupportInstaller.ShouldRecoverExistingService(
        new AppleSupportStatus(false, false, null,
            true, "appleusb.inf", string.Empty)));
    True(AppleSupportInstaller.ShouldRecoverExistingService(
        new AppleSupportStatus(true, false, "Apple Mobile Device Service",
            true, "appleusb.inf", string.Empty)));
    False(AppleSupportInstaller.ShouldRecoverExistingService(
        new AppleSupportStatus(true, true, "Apple Mobile Device Service",
            true, "appleusb.inf", string.Empty)));
});

Run("Apple USB driver package detection", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror.Driver.Tests",
        Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var recoveryOnly = Path.Combine(root, "applekis.inf_amd64_test");
        Directory.CreateDirectory(recoveryOnly);
        File.WriteAllText(Path.Combine(recoveryOnly, "applekis.inf"), string.Empty);
        Equal<string?>(null, DeviceCatalog.FindAppleUsbDriverPackage(root));

        var modern = Path.Combine(root, "appleusb.inf_amd64_test");
        Directory.CreateDirectory(modern);
        File.WriteAllText(Path.Combine(modern, "appleusb.inf"), string.Empty);
        Equal("appleusb.inf", DeviceCatalog.FindAppleUsbDriverPackage(root));

        Directory.Delete(modern, recursive: true);
        var desktop = Path.Combine(root, "usbaapl64.inf_amd64_test");
        Directory.CreateDirectory(desktop);
        File.WriteAllText(Path.Combine(desktop, "usbaapl64.inf"), string.Empty);
        Equal("usbaapl64.inf", DeviceCatalog.FindAppleUsbDriverPackage(root));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("winget Apple Devices command is pinned to Microsoft Store", () =>
{
    var arguments = AppleSupportInstaller.BuildWingetInstallArguments();
    True(arguments.Contains(DriverConstants.AppleStoreProductId));
    True(arguments.Contains(DriverConstants.AppleStoreSource));
    True(arguments.Contains("--exact"));
    True(arguments.Contains("--accept-source-agreements"));
    True(arguments.Contains("--accept-package-agreements"));
    True(arguments.Contains("--disable-interactivity"));
    False(arguments.Contains("--force"));
    True(AppleSupportInstaller.BuildWingetInstallArguments(force: true)
        .Contains("--force"));
});

Run("Apple installer terminal exit codes are recognized", () =>
{
    False(AppleSupportInstaller.IsRestartRequired(0));
    True(AppleSupportInstaller.IsRestartRequired(1641));
    True(AppleSupportInstaller.IsRestartRequired(3010));
    False(AppleSupportInstaller.IsRestartRequired(1223));

    False(AppleSupportInstaller.IsUserCancellation(0));
    False(AppleSupportInstaller.IsUserCancellation(1641));
    False(AppleSupportInstaller.IsUserCancellation(3010));
    True(AppleSupportInstaller.IsUserCancellation(1223));
});

if (failures.Count != 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Driver installer tests passed.");
return 0;

void Run(string name, Action test)
{
    try { test(); }
    catch (Exception error) { failures.Add($"{name}: {error.Message}"); }
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value) => True(!value);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
