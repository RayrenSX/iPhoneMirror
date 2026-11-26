using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    public bool CanConfigureProjectionAudio => SelectedDevice is { IsMediaCast: false } &&
        CurrentDeviceSession is not ({ IsStarting: true } or { IsStopping: true });

    public bool CanSelectDeviceAudio => CanConfigureProjectionAudio &&
        SelectedDevice is { IsWireless: false } && !IsMediaOutputRunning && !IsMediaOutputTransitioning;

    public bool ShowWindowsAudioControls => !ProjectionAudioOnDevice;

    public string ProjectionAudioRouteHint => LocalizationService.Get(
        SelectedDevice is null ? "StatusWaitingDevice" :
        IsWirelessSelected || IsMediaCastSelected ? "ProjectionAudioAirPlayHint" :
        !CanSelectDeviceAudio && !ProjectionAudioOnDevice ? "ProjectionAudioBusyHint" :
        ProjectionAudioOnDevice ? "ProjectionAudioDeviceHint" : "ProjectionAudioWindowsHint");

    public int ProjectionAudioRouteIndex
    {
        get => ProjectionAudioOnDevice ? 1 : 0;
        set
        {
            if (value is 0 or 1) ProjectionAudioOnDevice = value == 1;
        }
    }

    private bool ProjectionAudioOnDevice
    {
        get => !IsMediaCastSelected && CurrentDeviceSession?.AudioOnDevice == true;
        set
        {
            if (value == ProjectionAudioOnDevice || !CanConfigureProjectionAudio ||
                (value && !CanSelectDeviceAudio)) return;
            if (SelectedDevice is not { } device) return;
            var state = GetOrCreateDeviceState(device);
            if (!ApplyProjectionAudio(state, (uint)state.AudioBuffer, state.SyncAudioPreview, value)) return;
            state.AudioOnDevice = value;
            NotifyProjectionAudio();
            SetSettingsStatus(value ? "ProjectionAudioDeviceRequested" : "ProjectionAudioWindowsRequested");
        }
    }

    public bool ProjectionAudioSync
    {
        get => !IsMediaCastSelected && CurrentDeviceSession?.SyncAudioPreview == true;
        set
        {
            if (!CanConfigureProjectionAudio || value == ProjectionAudioSync || SelectedDevice is not { } device) return;
            var state = GetOrCreateDeviceState(device);
            if (!ApplyProjectionAudio(state, (uint)state.AudioBuffer, value, state.AudioOnDevice)) return;
            state.SyncAudioPreview = value;
            SaveProjectionAudioTiming(state);
            NotifyProjectionAudio();
        }
    }

    public int ProjectionAudioBufferIndex
    {
        get => (int)(CurrentDeviceSession?.AudioBuffer ?? ProjectionAudioBuffer.Default);
        set
        {
            if (value < 0 || value > 3 || !CanConfigureProjectionAudio ||
                value == ProjectionAudioBufferIndex || SelectedDevice is not { } device) return;
            var state = GetOrCreateDeviceState(device);
            if (!ApplyProjectionAudio(state, (uint)value, state.SyncAudioPreview, state.AudioOnDevice)) return;
            state.AudioBuffer = (ProjectionAudioBuffer)value;
            SaveProjectionAudioTiming(state);
            NotifyProjectionAudio();
        }
    }

    private void SaveProjectionAudioTiming(DeviceCaptureState state)
    {
        if (System.Windows.Application.Current is App) PersistDeviceVideoPreferences(state);
    }

    private bool ApplyProjectionAudio(DeviceCaptureState state, uint buffer, bool sync, bool deviceOutput)
    {
        if (!state.HasSession) return true;
        var result = InvokeDeviceSetting(() => _core.SetDeviceAudioPlaybackPreferences(state.Handle!, buffer, sync, deviceOutput));
        if (!result.Success)
        {
            SetRawSettingsStatus(result.Message);
            NotifyProjectionAudio();
        }
        return result.Success;
    }

    private void NotifyProjectionAudio()
    {
        OnPropertyChanged(nameof(CanConfigureProjectionAudio));
        OnPropertyChanged(nameof(CanSelectDeviceAudio));
        OnPropertyChanged(nameof(ShowWindowsAudioControls));
        OnPropertyChanged(nameof(ProjectionAudioRouteHint));
        OnPropertyChanged(nameof(ProjectionAudioRouteIndex));
        OnPropertyChanged(nameof(ProjectionAudioSync));
        OnPropertyChanged(nameof(ProjectionAudioBufferIndex));
    }
}
