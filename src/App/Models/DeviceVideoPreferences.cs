namespace IPhoneMirror.App.Models;

// Persisted subset of the existing DeviceCaptureState. Both setup and normal
// projection settings continue to edit the same runtime state.
internal sealed record DeviceVideoPreferences(uint Width, uint Height, int FrameRate, DecoderPreference Decoder)
{
    // Numeric values identify the protocol, independently of the A/B labels.
    // Keep 0=Demo and 1=AirPlay so existing users retain their chosen protocol.
    private UsbProjectionMode _usbProjectionMode = UsbProjectionMode.AirPlay;
    public UsbProjectionMode UsbProjectionMode
    {
        get => _usbProjectionMode;
        init => _usbProjectionMode = Enum.IsDefined(value) ? value : UsbProjectionMode.AirPlay;
    }
    public bool SyncAudioPreview { get; init; }
    public ProjectionAudioBuffer AudioBuffer { get; init; }
    internal bool IsValid => Width <= 8192 && Height <= 8192 && (Width == 0) == (Height == 0) &&
        FrameRate is 24 or 30 or 60 or 120 && Enum.IsDefined(Decoder) && Enum.IsDefined(UsbProjectionMode);
    internal static DeviceVideoPreferences From(DeviceCaptureState state) =>
        new(state.RenderWidth, state.RenderHeight, state.FrameRate, state.DecoderPreference)
        { UsbProjectionMode = state.UsbProjectionMode, SyncAudioPreview = state.SyncAudioPreview, AudioBuffer = state.AudioBuffer };
    internal void Apply(DeviceCaptureState state)
    {
        if (!IsValid) return;
        state.RenderWidth = Width; state.RenderHeight = Height; state.FrameRate = FrameRate; state.DecoderPreference = Decoder;
        state.UsbProjectionMode = UsbProjectionMode;
        state.SyncAudioPreview = SyncAudioPreview;
        state.AudioBuffer = Enum.IsDefined(AudioBuffer) ? AudioBuffer : default;
    }
}
