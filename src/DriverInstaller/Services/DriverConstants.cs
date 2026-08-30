using System.Text.RegularExpressions;

namespace IPhoneMirror.DriverInstaller.Services;

internal static partial class DriverConstants
{
    private static readonly int[] AppleMobileCaptureProductIds =
        [
        0x1290, 0x1291, 0x1292, 0x1293, 0x1294, 0x1297, 0x1299, 0x129A, 0x129C, 0x129D, 0x129E, 0x129F, 0x12A0, 0x12A1, 0x12A2, 0x12A3, 0x12A4, 0x12A5, 0x12A6, 0x12A8, 0x12A9, 0x12AA, 0x12AB, 0x12AC
    ];

    internal const string ElevatedSwitch = "--elevated-driver-operation";
    internal const string AppleStoreProductId = "9NP83LWLPZ9K";
    internal const string AppleStoreSource = "msstore";
    internal const string AppleSupportMsiFileName = "AppleMobileDeviceSupport64.msi";
    internal const string OfficialItunesDownloadUrl =
        "https://www.apple.com/itunes/download/win64";
    internal const string QqGroupNumber = "1050045279";
    internal const string AisiOfficialUrl = "https://www.i4.cn/";
    internal const string DriverVersion = "1.2.6.0";
    internal const string AppleSignerSubject =
        "CN=Apple Inc., O=Apple Inc., L=Cupertino, S=California, C=US";

    internal const string InstallerHash =
        "DF2ABF387893332F28C4DF68B10A6B176DC9706142055DCCCCF447F5A9CEDE2D";
    internal const string DriverHash =
        "8058F2AFE6EF96A7D2DED432997FD8655970C9EA75A938EE4557D6A2CB4CC989";
    internal const string Dll64Hash =
        "4F18B5D2C28AA66B648C8683C6D09B52B92CBBEE85984BBEFAD5F38A64BC2A14";
    internal const string Dll32Hash =
        "00CACA07869B19D10B370552AC7CC2F6F2EE246FC15DB11650F6CD3F4EF9B666";

    internal static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "iPhoneMirror.Driver");
    internal static string OperationsRoot => Path.Combine(DataRoot, "Operations");
    internal static string BackupsRoot => Path.Combine(DataRoot, "Backups");
    internal static string PackagesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror.Driver", "Packages");

    [GeneratedRegex(@"^USB\\VID_05AC&PID_[0-9A-Fa-f]{4}\\[A-Za-z0-9][A-Za-z0-9-]{6,38}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AppleParentPattern();

    internal static bool IsAllowedAppleParent(string instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) &&
        !instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
        AppleParentPattern().IsMatch(instanceId);

    // This explicit table follows config/apple-mobile-capture-pids.txt and Apple's iPhone USB driver entries,
    // including older iPhone and iPad product IDs while excluding Apple TV,
    // Watch, HomePod, and other Apple USB devices.
    internal static bool IsAppleMobileCaptureParent(string instanceId)
    {
        if (!IsAllowedAppleParent(instanceId)) return false;
        var marker = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 || marker + 8 > instanceId.Length) return false;
        return int.TryParse(instanceId.AsSpan(marker + 4, 4),
            System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture, out var productId) &&
            Array.IndexOf(AppleMobileCaptureProductIds, productId) >= 0;
    }

    internal static bool IsValidOperationId(string value) =>
        Guid.TryParseExact(value, "N", out _);

    internal static bool IsKnownReplaceableParentService(string service) =>
        service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase) ||
        service.Equals("libusb0", StringComparison.OrdinalIgnoreCase) ||
        service.Equals("libusbK", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeSerial(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    internal static bool IsValidSerial(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = NormalizeSerial(value);
        return normalized.Length is >= 8 and <= 40 &&
               normalized.All(char.IsAsciiLetterOrDigit) &&
               string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase);
    }

    internal static (string Directory, string ResultPath, string LogPath) GetOperationPaths(
        string operationId)
    {
        if (!IsValidOperationId(operationId))
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        var directory = Path.Combine(OperationsRoot, operationId);
        return (directory, Path.Combine(directory, "result.json"),
            Path.Combine(directory, "operation.log"));
    }
}
