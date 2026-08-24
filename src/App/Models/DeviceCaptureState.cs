namespace IPhoneMirror.App.Models;

internal enum UsbProjectionMode : uint
{
    Demo = 0,
    AirPlay = 1,
    Aisi = 2,
}

internal enum DecoderPreference : uint
{
    Auto = 0,
    HardwarePreferred = 1,
    SoftwareCompatible = 2,
}

internal sealed class DeviceCaptureState
{
    internal required string Udid { get; init; }
    internal ulong Handle { get; set; }
    internal bool IsStarting { get; set; }
    internal bool IsStopping { get; set; }
    internal uint RenderWidth { get; set; }
    internal uint RenderHeight { get; set; }
    internal int FrameRate { get; set; } = 60;
    internal bool PlayAudio { get; set; } = true;
    internal double Volume { get; set; } = 100;
    internal uint AdvancedUsbWidth { get; set; }
    internal uint AdvancedUsbHeight { get; set; }
    internal UsbProjectionMode UsbProjectionMode { get; set; } = UsbProjectionMode.Demo;
    internal DecoderPreference DecoderPreference { get; set; } = DecoderPreference.Auto;
    internal double Brightness { get; set; }
    internal double Contrast { get; set; } = 100;
    internal double Saturation { get; set; } = 100;
    internal double Gamma { get; set; } = 100;
    internal uint AppliedRenderWidth { get; private set; }
    internal uint AppliedRenderHeight { get; private set; }
    internal int AppliedFrameRate { get; private set; } = 60;
    internal DecoderPreference AppliedDecoderPreference { get; private set; } =
        DecoderPreference.Auto;
    internal double AppliedBrightness { get; private set; }
    internal double AppliedContrast { get; private set; } = 100;
    internal double AppliedSaturation { get; private set; } = 100;
    internal double AppliedGamma { get; private set; } = 100;
    internal bool HasAppliedVideoSettings { get; private set; }
    internal bool HasSession => Handle != 0;
    internal bool ErrorShown { get; set; }
    internal bool VideoProtected { get; private set; }
    internal bool ProtectedAudioActive { get; private set; }
    internal uint ProtectedAudioSampleRate { get; private set; }
    internal uint ProtectedAudioChannels { get; private set; }

    internal bool UpdateProtectionState(bool videoProtected,
        bool audioActive, uint audioSampleRate, uint audioChannels)
    {
        audioActive = videoProtected && audioActive;
        audioSampleRate = videoProtected ? audioSampleRate : 0;
        audioChannels = videoProtected ? audioChannels : 0;
        var changed = VideoProtected != videoProtected ||
            ProtectedAudioActive != audioActive ||
            ProtectedAudioSampleRate != audioSampleRate ||
            ProtectedAudioChannels != audioChannels;
        VideoProtected = videoProtected;
        ProtectedAudioActive = audioActive;
        ProtectedAudioSampleRate = audioSampleRate;
        ProtectedAudioChannels = audioChannels;
        return changed;
    }

    internal void ResetRuntimeObservations()
    {
        VideoProtected = false;
        ProtectedAudioActive = false;
        ProtectedAudioSampleRate = 0;
        ProtectedAudioChannels = 0;
    }

    // A settings window is tied to the native session that existed when it
    // opened. The state object intentionally survives reconnects, so object
    // identity alone cannot distinguish the old session from its replacement.
    internal bool MatchesSessionHandle(ulong expectedHandle) =>
        !IsStopping && Handle == expectedHandle;

    internal bool HasPendingVideoSettings => !HasAppliedVideoSettings ||
        RenderWidth != AppliedRenderWidth || RenderHeight != AppliedRenderHeight ||
        FrameRate != AppliedFrameRate ||
        DecoderPreference != AppliedDecoderPreference ||
        Math.Abs(Brightness - AppliedBrightness) > 0.001 ||
        Math.Abs(Contrast - AppliedContrast) > 0.001 ||
        Math.Abs(Saturation - AppliedSaturation) > 0.001 ||
        Math.Abs(Gamma - AppliedGamma) > 0.001;

    internal void MarkVideoSettingsApplied(uint renderWidth, uint renderHeight,
        int frameRate, DecoderPreference decoderPreference, double brightness,
        double contrast, double saturation, double gamma)
    {
        MarkRenderSettingsApplied(renderWidth, renderHeight, frameRate);
        AppliedDecoderPreference = decoderPreference;
        MarkImageAdjustmentsApplied(brightness, contrast, saturation, gamma);
    }

    internal void MarkRenderSettingsApplied(uint renderWidth,
        uint renderHeight, int frameRate)
    {
        AppliedRenderWidth = renderWidth;
        AppliedRenderHeight = renderHeight;
        AppliedFrameRate = frameRate;
        HasAppliedVideoSettings = true;
    }

    internal void MarkImageAdjustmentsApplied(double brightness,
        double contrast, double saturation, double gamma)
    {
        AppliedBrightness = brightness;
        AppliedContrast = contrast;
        AppliedSaturation = saturation;
        AppliedGamma = gamma;
        HasAppliedVideoSettings = true;
    }

    internal void SynchronizeAppliedDecoderPreference(
        DecoderPreference decoderPreference)
    {
        AppliedDecoderPreference = decoderPreference;
    }
}
