using System.Windows;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel : IAutomationBackend
{
    internal DeviceInputService DeviceInput { get; } = new();
    internal AutomationOwnership AutomationOwnership { get; } = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (uint Width, uint Height)> _automationGeometry = new(StringComparer.OrdinalIgnoreCase);
    internal void ObserveAutomationGeometry(string source, uint width, uint height) => _automationGeometry[source] = (width, height);
    internal Func<string, bool>? AutomationHumanInputBusy { get; set; }
    internal void ConfigureAutomationInputAdmission() => _bluetoothControl.InputAdmission = () =>
        AutomationOwnership.EnterInput(AutomationPhysicalId(_bluetoothControlDeviceUdid ?? ""));
    internal bool AutomationBluetoothBusy(string physical) =>
        _bluetoothControlDeviceUdid is { } source && AutomationPhysicalId(source) == physical && _bluetoothControl.HasPendingInput;

    private Task<T> AutomationUiAsync<T>(Func<T> read, CancellationToken cancellation) =>
        Application.Current.Dispatcher.InvokeAsync(read,
            System.Windows.Threading.DispatcherPriority.Normal, cancellation).Task;

    internal string AutomationPhysicalId(string source)
    {
        var profile = _reverseBindings.FindByIdentity(DeviceViewModel.IsWirelessUdid(source)
            ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired, source);
        return profile is null ? source : profile.Id.ToString("N");
    }

    internal bool AutomationHumanAllowed(string? source) => source is not null &&
        AutomationOwnership.HumanAllowed(AutomationPhysicalId(source));
    internal Func<bool> AutomationHumanGuard(string source) =>
        AutomationOwnership.CaptureHumanGuard(AutomationPhysicalId(source));

    async Task<IReadOnlyList<AutomationDevice>> IAutomationBackend.GetDevicesAsync(CancellationToken cancellation)
    {
        var ids = await AutomationUiAsync(() => Devices.Where(d => !d.IsMediaCast || d.IsWireless)
            .Select(d => AutomationDeviceId.Encode(d.Udid)).ToArray(), cancellation);
        var devices = new List<AutomationDevice>();
        foreach (var id in ids)
        {
            try { devices.Add((await ((IAutomationBackend)this).ResolveAsync(id, cancellation)).Device); }
            catch (AutomationException e) when (e.Code == "DEVICE_NOT_FOUND") { }
        }
        return devices;
    }

    async Task<AutomationTarget> IAutomationBackend.ResolveAsync(string id, CancellationToken cancellation)
    {
        var capture = await AutomationUiAsync(() =>
        {
            var device = Devices.FirstOrDefault(d => AutomationDeviceId.Encode(d.Udid) == id &&
                (!d.IsMediaCast || d.IsWireless)) ?? throw new AutomationException("DEVICE_NOT_FOUND", "Device was not found.", 404);
            return (Device: device, Handle: _sessions.Get(device.Udid)?.Handle);
        }, cancellation);
        NativeCaptureStatus? status = null;
        if (capture.Handle is { IsClosed: false, IsInvalid: false } handle)
        {
            try { status = await Task.Run(() => _core.GetDeviceSessionStatus(handle), cancellation); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
        }
        return await AutomationUiAsync(() =>
        {
            var device = capture.Device;
            if (!Devices.Contains(device)) throw new AutomationException("DEVICE_NOT_FOUND", "Device was not found.", 404);
            var profile = _identityResolver.ResolveProfile(device).Profile;
            var physical = profile?.Id.ToString("N") ?? device.Udid;
            var keyboard = CaptureDirectKeyboardRoute(device.Udid, automation: true);
            var control = FindControl(device.Udid);
            var bridge = GetReadyUsbControlBridge(device.Udid);
            var native = capture.Handle;
            var sameCapture = ReferenceEquals(_sessions.Get(device.Udid)?.Handle, native);
            var width = sameCapture ? status?.Width ?? 0 : 0;
            var height = sameCapture ? status?.Height ?? 0 : 0;
            ObserveAutomationGeometry(device.Udid, width, height);
            var portrait = AppliedBluetoothPortraitMouseDirection;
            var landscape = AppliedBluetoothLandscapeMouseDirection;
            var reverseX = AppliedBluetoothMouseReverseHorizontal;
            var reverseY = AppliedBluetoothMouseReverseVertical;
            var generation = control?.Router.Generation ?? keyboard?.Generation ?? 0;
            var geometry = new AutomationGeometry(width, height,
                $"{native?.RawHandle ?? 0:x}:{width}:{height}:{(int)portrait}:{(int)landscape}:{reverseX}:{reverseY}");
            var binding = _identityResolver.ResolveControlBinding(device,
                bridge is not null ? (control?.WiredEnabled == true ? ReverseControlMode.Usb : ReverseControlMode.Wireless) : ReverseControlMode.Bluetooth);
            bool Current() => !_disposed && keyboard?.IsCurrent() == true &&
                (native is null || (ReferenceEquals(_sessions.Get(device.Udid)?.Handle, native) &&
                    _automationGeometry.TryGetValue(device.Udid, out var size) && size == (width, height))) &&
                portrait == AppliedBluetoothPortraitMouseDirection && landscape == AppliedBluetoothLandscapeMouseDirection &&
                reverseX == AppliedBluetoothMouseReverseHorizontal && reverseY == AppliedBluetoothMouseReverseVertical &&
                (control is null || control.Router.Generation == generation) &&
                binding?.Matches(_identityResolver.ResolveControlBinding(device,
                    bridge is not null ? (control?.WiredEnabled == true ? ReverseControlMode.Usb : ReverseControlMode.Wireless) : ReverseControlMode.Bluetooth)) == true;
            var touching = bridge is not null && width > 0 && height > 0 && binding is not null;
            var clipboard = bridge?.SupportsAutomationClipboard == true && binding is not null && keyboard is not null;
            var online = device.State != ConnectionState.Disconnected;
            var capturing = sameCapture && native is { IsClosed: false, IsInvalid: false } &&
                status?.State is CaptureState.Streaming;
            var dto = new AutomationDevice(id, device.Name, device.ProductType, device.OsVersion,
                device.IsWireless ? "airplay" : "usb", online, keyboard is not null && binding is not null,
                profile?.Id, keyboard?.Transport, capturing, geometry,
                new(touching, touching, touching, touching, touching, touching, keyboard is not null,
                    clipboard, clipboard, clipboard, capturing && width > 0 && height > 0));
            // Capture the route on its owner thread. Workers use only the guarded
            // delegates, never enumerate WPF collections to choose a target.
            Func<bool>? admitted = null;
            var route = CaptureTouchRoute(device.Udid, () => Current() && admitted?.Invoke() != false,
                (x, y) => BluetoothMouseOrientationMapper.MapNormalized(x, y, width, height, 0,
                    portrait, landscape, reverseX, reverseY), automation: true);
            return new AutomationTarget(dto, device.Udid, physical, Current,
                p => BluetoothMouseOrientationMapper.MapNormalized(p.X, p.Y, width, height, 0,
                    portrait, landscape, reverseX, reverseY),
                guard => { admitted = guard; return route; }, keyboard,
                bridge is null || !clipboard ? null : (kind, text, token) =>
                    bridge.AutomationClipboardAsync(kind, text, Current, token),
                token => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (native is null || !ReferenceEquals(_sessions.Get(device.Udid)?.Handle, native)) return null;
                    var frame = _core.GetDeviceOutputFrame(native, 0, 0, owned: true);
                    return ReferenceEquals(_sessions.Get(device.Udid)?.Handle, native) ? frame : null;
                }, token)) { SessionCurrent = () => keyboard?.IsCurrent() == true,
                    HardwareBusy = () => bridge?.InputWritePending == true ||
                    (keyboard is not null && DeviceInput.KeyboardGate(keyboard).CurrentCount == 0) ||
                    (bridge is null && _bluetoothControl.HasPendingInput) };
        }, cancellation);
    }

    Task<bool> IAutomationBackend.IsHumanInputBusyAsync(string physicalId, CancellationToken cancellation) =>
        AutomationUiAsync(() => AutomationHumanInputBusy?.Invoke(physicalId) != false, cancellation);

    async Task IAutomationBackend.ReserveAsync(string physicalId, Action reserve, CancellationToken cancellation) =>
        await AutomationUiAsync(() =>
        {
            if (AutomationHumanInputBusy?.Invoke(physicalId) != false)
                throw new AutomationException("CONTROL_LOCKED", "Wait until manual input has finished.");
            reserve();
            return true;
        }, cancellation);
}
