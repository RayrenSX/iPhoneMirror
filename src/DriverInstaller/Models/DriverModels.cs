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
    string[] UpperFilters,
    uint? ProblemCode = 0,
    bool IsStarted = true,
    string DriverInf = "",
    string DriverSection = "",
    string DriverVersion = "",
    string[]? LowerFilters = null)
{
    public bool IsHealthy => IsPresent && IsStarted && ProblemCode == 0;
    public bool IsCaptureParent => Service.Equals("usbccgp", StringComparison.OrdinalIgnoreCase);
    public string ParentStatusText => DriverLocalization.Format("ParentStatusFormat",
        string.IsNullOrEmpty(Service) ? DriverLocalization.Get("ParentNoService") : Service,
        string.IsNullOrEmpty(DriverInf) ? DriverLocalization.Get("ParentNoInf") : DriverInf,
        ProblemCode?.ToString() ?? "?",
        DriverLocalization.Get(IsHealthy ? "ParentHealthy" : "ParentNeedsAttention"));
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
    bool UsbDriverInstalled,
    string? UsbDriverInf,
    string Diagnostic)
{
    internal bool Ready => ServiceInstalled && ServiceRunning && UsbDriverInstalled;
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
    string LogPath,
    bool RequiresRestart = false);

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    internal string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
