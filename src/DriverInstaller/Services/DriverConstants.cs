using System.Text.RegularExpressions;

namespace IPhoneMirror.DriverInstaller.Services;

internal static partial class DriverConstants
{
    internal const string ElevatedSwitch = "--elevated-driver-operation";
    internal const string AppleStoreProductId = "9NP83LWLPZ9K";
    internal const string OfficialItunesDownloadUrl =
        "https://www.apple.com/itunes/download/win64";
    internal const string QqGroupNumber = "1050045279";
    internal const string AisiOfficialUrl = "https://www.i4.cn/";
    internal const string DriverVersion = "1.2.6.0";

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
    internal static string PackagesRoot => Path.Combine(DataRoot, "Packages");

    [GeneratedRegex(@"^USB\\VID_05AC&PID_[0-9A-Fa-f]{4}\\[A-Za-z0-9]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AppleParentPattern();

    internal static bool IsAllowedAppleParent(string instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) &&
        !instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
        AppleParentPattern().IsMatch(instanceId);

    internal static bool IsValidOperationId(string value) =>
        Guid.TryParseExact(value, "N", out _);

    internal static bool IsKnownReplaceableParentService(string service) =>
        service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase) ||
        service.Equals("libusb0", StringComparison.OrdinalIgnoreCase) ||
        service.Equals("libusbK", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeSerial(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

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
