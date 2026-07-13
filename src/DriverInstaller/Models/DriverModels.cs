using IPhoneMirror.DriverInstaller.Services;

namespace IPhoneMirror.DriverInstaller.Models;

internal enum DriverOperationKind
{
    Install,
    Repair,
    Uninstall,
    ParentRepair,
}

public sealed record AppleDeviceRecord(
    string InstanceId,
    string Serial,
    string DisplayName,
    string ProductType,
    string ModelName,
    string DeviceName,
    string OsVersion,
    int DeviceNumber,
    string Service,
    bool IsPresent,
    bool HasLibUsb0Filter,
    string[] UpperFilters)
{
    public string ConnectionText => DriverLocalization.Get(IsPresent ? "Connected" : "HistoricalDevice");
    public string DriverText => DriverLocalization.Get(HasLibUsb0Filter ? "CaptureInstalled" : "CaptureMissing");
    public string SelectionText
    {
        get
        {
            var friendlyName = string.IsNullOrWhiteSpace(DeviceName) ||
                               string.Equals(DeviceName, "iPhone", StringComparison.OrdinalIgnoreCase)
                ? DriverLocalization.Format("DeviceNumberFormat", ModelName, DeviceNumber)
                : DriverLocalization.Format("NamedDeviceFormat", ModelName, DeviceName);
            return string.IsNullOrWhiteSpace(OsVersion)
                ? friendlyName
                : DriverLocalization.Format("DeviceOsFormat", friendlyName, OsVersion);
        }
    }
    public string DetailText => string.IsNullOrWhiteSpace(ProductType)
        ? DriverLocalization.Format("AppleUsbDeviceFormat", DeviceNumber)
        : DriverLocalization.Format("DeviceModelFormat", ProductType);
}

internal sealed record AppleSupportStatus(
    bool ServiceInstalled,
    bool ServiceRunning,
    string? ServiceName,
    string Diagnostic)
{
    internal bool Ready => ServiceInstalled && ServiceRunning;
}

internal sealed record LibUsbStackStatus(
    bool ServiceInstalled,
    bool ServiceRunning,
    bool FilesMatch,
    string? Version,
    string Diagnostic);

internal sealed record DriverOperationResult(
    bool Success,
    bool RequiresReplug,
    string Message,
    string? InstanceId,
    string? BackupPath,
    string LogPath);

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    internal string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
