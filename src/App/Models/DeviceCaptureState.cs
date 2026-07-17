namespace IPhoneMirror.App.Models;

internal enum UsbProjectionMode : uint
{
    Demo = 0,
    AirPlay = 1,
    Aisi = 2,
}

internal sealed class DeviceCaptureState
{
    internal required string Udid { get; init; }
    internal ulong Handle { get; set; }
    internal uint RenderWidth { get; set; }
    internal uint RenderHeight { get; set; }
    internal int FrameRate { get; set; } = 60;
    internal bool PlayAudio { get; set; } = true;
    internal double Volume { get; set; } = 100;
    internal uint AdvancedUsbWidth { get; set; }
    internal uint AdvancedUsbHeight { get; set; }
    internal UsbProjectionMode UsbProjectionMode { get; set; } = UsbProjectionMode.Demo;
    internal bool HasSession => Handle != 0;
    internal bool ErrorShown { get; set; }
}
