using System.Runtime.InteropServices;
using System.IO;

namespace IPhoneMirror.App.Interop;

internal sealed partial class NativeCore
{
    [DllImport("iPhoneMirror.Core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int im_external_session_create(string device, ref NativeCaptureOptions options, out ulong handle);
    [DllImport("iPhoneMirror.Core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_external_session_push(ulong handle, uint kind, uint width, uint height, byte[] data, uint length);
    [DllImport("iPhoneMirror.Core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_external_session_state(ulong handle, int active);

    internal NativeSessionCreateResult CreateExternalSession(string device, bool audio, double volume,
        uint width = 0, uint height = 0, uint fps = 60, uint decoderPreference = 0)
    {
        var options = new NativeCaptureOptions { StructSize = (uint)Marshal.SizeOf<NativeCaptureOptions>(),
            ApiVersion = 18, RequestedWidth = width, RequestedHeight = height,
            TargetFps = fps, PlayAudio = audio ? 1 : 0,
            AudioVolume = Math.Clamp((float)volume, 0, 1), Reserved = new uint[5] };
        options.Reserved[3] = Math.Min(decoderPreference, 2U);
        options.Reserved[4] = 1; // The external I420 source is SDR.
        var result = im_external_session_create(device, ref options, out var handle);
        return result == 0 ? new(true, new NativeSessionHandle(handle), 0, "CoreDevice ready") :
            new(false, null, result, GetLastError("CoreDevice capture could not start."));
    }

    internal void PushExternalFrame(NativeSessionHandle handle, uint kind, uint width, uint height, byte[] data)
    {
        if (im_external_session_push(handle.RawHandle, kind, width, height, data, checked((uint)data.Length)) != 0)
            throw new InvalidDataException("The native media consumer rejected the decoded frame.");
    }
    internal void SetExternalActive(NativeSessionHandle handle, bool active) =>
        im_external_session_state(handle.RawHandle, active ? 1 : 0);
}
