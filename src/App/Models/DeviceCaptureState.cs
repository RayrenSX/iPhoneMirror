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

internal enum ColorOutputPreference : uint
{
    Auto = 0,
    ForceSdrToneMap = 1,
    PreferHdrWhenSupported = 2,
}

internal sealed class DeviceCaptureState
{
    internal required string Udid { get; init; }
    internal ulong Handle { get; set; }
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
    internal ColorOutputPreference ColorOutputPreference { get; set; } = ColorOutputPreference.Auto;
    internal bool HasSession => Handle != 0;
    internal bool ErrorShown { get; set; }
}
