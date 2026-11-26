using System.Runtime.InteropServices;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Interop;

internal sealed partial class NativeCore
{
    [DllImport("iPhoneMirror.Core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_set_audio_playback_preferences(ulong handle,
        uint buffer, int sync, int deviceOutput);

    internal void SetDeviceAudioPlaybackPreferences(NativeSessionHandle handle, uint buffer,
        bool sync, bool deviceOutput)
    {
        if (im_session_set_audio_playback_preferences(handle.RawHandle, buffer,
                sync ? 1 : 0, deviceOutput ? 1 : 0) != 0)
            throw new InvalidOperationException(GetLastError(LocalizationService.Get("AudioStateUpdateFailed")));
    }
}
