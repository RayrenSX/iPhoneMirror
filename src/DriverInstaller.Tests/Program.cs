using IPhoneMirror.DriverInstaller.Services;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

var failures = new List<string>();

Run("serial normalization", () =>
{
    Equal("0000810100044D600A22001E",
        DriverConstants.NormalizeSerial("00008101-00044d600a22001e"));
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
