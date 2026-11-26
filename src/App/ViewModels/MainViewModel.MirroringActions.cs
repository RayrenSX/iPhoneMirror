using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    public bool HasMirroringVideo => IsMirroringVideoAvailable(CurrentDeviceSession);
    public string ProjectionPictureActionText => LocalizationService.Get(
        HasMirroringVideo ? "IndependentWindowImageSettings" : "MirroringActionStartFirst");

    private static bool IsMirroringVideoAvailable(DeviceCaptureState? state) =>
        IsSessionPresentable(state) && state is { HasMirroringVideo: true, VideoProtected: false };

    internal bool CanToggleMirroringControl(string? udid, bool wireless)
    {
        if (udid is null) return false;
        if (FindControl(udid) is { } control)
        {
            if (control.Starting || control.Stopping) return false;
            if (wireless ? control.WirelessEnabled : control.WiredEnabled) return true;
        }

        if (!IsMirroringVideoAvailable(_sessions.Get(udid))) return false;
        var device = Devices.FirstOrDefault(candidate => DeviceViewModel.UdidEquals(candidate.Udid, udid));
        return wireless ? CanEnableWirelessControlFor(device) : CanEnableUsbControlFor(device);
    }

    private void ObserveMirroringVideo(DeviceCaptureState state, NativeCaptureStatus status)
    {
        var isMirroring = status.State == CaptureState.Streaming && status.Width > 0 && status.Height > 0;
        if (state.IsMirroring != isMirroring)
        {
            state.IsMirroring = isMirroring;
            RefreshSourceCardMirroring(state.Udid);
        }
        var hasVideo = isMirroring && !state.VideoProtected;
        if (state.HasMirroringVideo == hasVideo) return;
        state.HasMirroringVideo = hasVideo;
        if (ReferenceEquals(state, CurrentDeviceSession)) NotifyMirroringActionAvailability();
    }

    private void NotifyMirroringActionAvailability()
    {
        OnPropertyChanged(nameof(HasMirroringVideo));
        OnPropertyChanged(nameof(ProjectionPictureActionText));
        OnPropertyChanged(nameof(CanStartUsbControl));
        OnPropertyChanged(nameof(CanStartWirelessControl));
        OnPropertyChanged(nameof(CanToggleWiredControl));
        OnPropertyChanged(nameof(CanToggleWirelessControl));
        OnPropertyChanged(nameof(WiredControlActionText));
        OnPropertyChanged(nameof(WirelessControlActionText));
        OnPropertyChanged(nameof(CanToggleUsbControl));
        ToggleUsbControlCommand?.NotifyCanExecuteChanged();
    }
}
