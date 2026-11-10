namespace IPhoneMirror.App.Models;

// Persisted subset of the existing DeviceCaptureState. Both setup and normal
// projection settings continue to edit the same runtime state.
internal sealed record DeviceVideoPreferences(uint Width, uint Height, int FrameRate, DecoderPreference Decoder)
{
    public UsbProjectionMode UsbProjectionMode { get; init; } = UsbProjectionMode.Demo;
    internal bool IsValid => Width <= 8192 && Height <= 8192 && (Width == 0) == (Height == 0) &&
        FrameRate is 24 or 30 or 60 or 120 && Enum.IsDefined(Decoder) && Enum.IsDefined(UsbProjectionMode);
    internal static DeviceVideoPreferences From(DeviceCaptureState state) =>
        new(state.RenderWidth, state.RenderHeight, state.FrameRate, state.DecoderPreference)
        { UsbProjectionMode = state.UsbProjectionMode };
    internal void Apply(DeviceCaptureState state)
    {
        if (!IsValid) return;
        state.RenderWidth = Width; state.RenderHeight = Height; state.FrameRate = FrameRate; state.DecoderPreference = Decoder;
        state.UsbProjectionMode = UsbProjectionMode;
    }
}
