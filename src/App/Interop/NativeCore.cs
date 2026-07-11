using System.Runtime.InteropServices;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Interop;

internal enum NativeResult : int
{
    Ok = 0,
    BufferTooSmall = -3,
}

internal enum ConnectionState : int
{
    Disconnected,
    UsbPresentNoMux,
    Connected,
    Paired,
    Ready,
    Error,
}

internal enum CaptureState : int
{
    Idle,
    ActivatingUsb,
    WaitingForDevice,
    Handshaking,
    Streaming,
    Stopping,
    Stopped,
    Error,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeDeviceInfo
{
    public uint StructSize;
    public uint ApiVersion;
    public uint DeviceId;
    public uint MuxPort;
    public ConnectionState State;
    public int UsbConnected;
    public int PairRecordPresent;
    public int LockdownAccessible;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Udid;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ProductType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string OsVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ConnectionType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 192)] public string Status;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeEnvironmentInfo
{
    public uint StructSize;
    public uint ApiVersion;
    public int ServiceInstalled;
    public int ServiceRunning;
    public int StandardMuxAvailable;
    public int CaptureMuxAvailable;
    public uint PhysicalAppleUsbDevices;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string Diagnostic;
    public int LibUsbRuntimeAvailable;
    public int UsbDkBackendAvailable;
    public uint LibUsbAppleDevices;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string LibUsbVersion;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeCaptureStatus
{
    public uint StructSize;
    public uint ApiVersion;
    public CaptureState State;
    public uint Width;
    public uint Height;
    public double Fps;
    public double LatencyMs;
    public ulong VideoFrames;
    public ulong AudioPackets;
    public uint AudioSampleRate;
    public uint AudioChannels;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 192)] public string Message;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeVideoFrameInfo
{
    public uint StructSize;
    public uint ApiVersion;
    public uint Width;
    public uint Height;
    public uint Stride;
    public uint PixelFormat;
    public long Timestamp100Ns;
}

public sealed record VideoFrame(uint Width, uint Height, uint Stride, long Timestamp100Ns, byte[] Pixels);

internal sealed class NativeCore : IDisposable
{
    private const string Library = "iPhoneMirror.Core";
    private bool _initialized;
    private byte[]? _frameBuffer;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_initialize();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void im_shutdown();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_refresh_devices([Out] NativeDeviceInfo[]? devices, ref uint count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_get_environment(ref NativeEnvironmentInfo environment);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int im_is_libusb0_device_available(
        [MarshalAs(UnmanagedType.LPWStr)] string udid, out int available);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int im_start_capture([MarshalAs(UnmanagedType.LPWStr)] string udid);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int im_start_capture_ex(
        [MarshalAs(UnmanagedType.LPWStr)] string udid, int playAudio);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_stop_capture();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_set_audio_enabled(int enabled);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_set_audio_volume(float volume);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_set_video_preferences(uint width, uint height, uint maxFps);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_get_capture_status(ref NativeCaptureStatus status);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_get_latest_video_timestamp(out long timestamp100Ns);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_copy_latest_video_frame(
        ref NativeVideoFrameInfo info, [Out] byte[]? buffer, ref uint bufferSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_copy_latest_video_frame_scaled(
        ref NativeVideoFrameInfo info, [Out] byte[]? buffer, ref uint bufferSize,
        uint maxWidth, uint maxHeight);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_attach_preview_window(nint hwnd);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void im_detach_preview_window();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_force_preview_refresh();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint im_last_error();

    internal static bool AttachPreviewWindow(nint hwnd) =>
        hwnd != 0 && im_attach_preview_window(hwnd) == 0;

    internal static void DetachPreviewWindow() => im_detach_preview_window();

    internal static bool ForcePreviewRefresh()
    {
        try
        {
            return im_force_preview_refresh() == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public NativeCore()
    {
        var result = im_initialize();
        if (result != 0) throw new InvalidOperationException(GetLastError(
            LocalizationService.Get("NativeCoreInitFailed")));
        _initialized = true;
    }

    public NativeEnvironmentInfo GetEnvironment()
    {
        var info = new NativeEnvironmentInfo
        {
            StructSize = (uint)Marshal.SizeOf<NativeEnvironmentInfo>(),
            Diagnostic = string.Empty,
            LibUsbVersion = string.Empty,
        };
        var result = im_get_environment(ref info);
        if (result != 0) throw new InvalidOperationException(GetLastError(
            LocalizationService.Get("ReadEnvironmentFailed")));
        return info;
    }

    /// <summary>
    /// Checks whether libusb0 can enumerate and open this exact iPhone serial.
    /// The native probe only opens the descriptor long enough to read its
    /// serial; it never changes the active USB configuration or driver state.
    /// </summary>
    public bool IsLibUsb0DeviceAvailable(string udid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        var result = im_is_libusb0_device_available(udid, out var available);
        if (result != 0)
            throw new InvalidOperationException(GetLastError(
                LocalizationService.Get("ReadEnvironmentFailed")));
        return available != 0;
    }

    public IReadOnlyList<NativeDeviceInfo> GetDevices()
    {
        // usbmux is live state: another iPhone can appear or disappear between
        // the count query and the fill call. Retry a changed-size snapshot and
        // return only the entries actually written by the native core.
        for (var attempt = 0; attempt < 3; ++attempt)
        {
            uint count = 0;
            var result = im_refresh_devices(null, ref count);
            if (result != 0) throw new InvalidOperationException(GetLastError(
                LocalizationService.Get("EnumerateDevicesFailed")));
            if (count == 0) return [];

            var devices = new NativeDeviceInfo[count];
            for (var i = 0; i < devices.Length; i++)
            {
                devices[i].StructSize = (uint)Marshal.SizeOf<NativeDeviceInfo>();
                devices[i].Udid = string.Empty;
                devices[i].Name = string.Empty;
                devices[i].ProductType = string.Empty;
                devices[i].OsVersion = string.Empty;
                devices[i].ConnectionType = string.Empty;
                devices[i].Status = string.Empty;
            }
            var capacity = count;
            result = im_refresh_devices(devices, ref capacity);
            if (result == (int)NativeResult.BufferTooSmall) continue;
            if (result != 0) throw new InvalidOperationException(GetLastError(
                LocalizationService.Get("ReadDeviceInfoFailed")));
            return devices.Take(checked((int)Math.Min(capacity, (uint)devices.Length))).ToArray();
        }

        throw new InvalidOperationException(LocalizationService.Get("EnumerateDevicesFailed"));
    }

    public (bool Success, string Message) StartCapture(string udid, bool playAudio = true)
    {
        var result = im_start_capture_ex(udid, playAudio ? 1 : 0);
        return result == 0
            ? (true, LocalizationService.Get("CaptureStarted"))
            : (false, GetLastError(LocalizationService.Get("CannotStartCapture")));
    }

    public void StopCapture() => im_stop_capture();

    public (bool Success, string Message) SetAudioEnabled(bool enabled)
    {
        try
        {
            var result = im_set_audio_enabled(enabled ? 1 : 0);
            return result == 0
                ? (true, LocalizationService.Get(enabled ? "AudioPlaybackEnabled" : "AudioPlaybackMuted"))
                : (false, GetLastError(LocalizationService.Get("AudioStateUpdateFailed")));
        }
        catch (EntryPointNotFoundException)
        {
            return (false, LocalizationService.Get("AudioToggleUnsupported"));
        }
    }

    public (bool Success, string Message) SetAudioVolume(double volume)
    {
        try
        {
            var normalized = Math.Clamp((float)volume, 0.0f, 1.0f);
            var result = im_set_audio_volume(normalized);
            return result == 0
                ? (true, LocalizationService.Format("AudioVolumeFormat", normalized * 100))
                : (false, GetLastError(LocalizationService.Get("AudioVolumeUpdateFailed")));
        }
        catch (EntryPointNotFoundException)
        {
            return (false, LocalizationService.Get("AudioVolumeUnsupported"));
        }
    }

    public (bool Success, string Message) SetVideoPreferences(uint width, uint height, uint maxFps)
    {
        try
        {
            var result = im_set_video_preferences(width, height, maxFps);
            return result == 0
                ? (true, LocalizationService.Get("VideoPreferencesApplied"))
                : (false, GetLastError(LocalizationService.Get("VideoPreferencesUpdateFailed")));
        }
        catch (EntryPointNotFoundException)
        {
            return (false, LocalizationService.Get("VideoPreferencesUnsupported"));
        }
    }

    public NativeCaptureStatus GetCaptureStatus()
    {
        var status = new NativeCaptureStatus
        {
            StructSize = (uint)Marshal.SizeOf<NativeCaptureStatus>(),
            Message = string.Empty,
        };
        var result = im_get_capture_status(ref status);
        if (result != 0) throw new InvalidOperationException(GetLastError(
            LocalizationService.Get("ReadCaptureStatusFailed")));
        return status;
    }

    public long GetLatestVideoTimestamp()
    {
        var result = im_get_latest_video_timestamp(out var timestamp);
        return result == 0 ? timestamp : 0;
    }

    public VideoFrame? GetLatestVideoFrame()
    {
        var info = new NativeVideoFrameInfo { StructSize = (uint)Marshal.SizeOf<NativeVideoFrameInfo>() };
        uint size = (uint)(_frameBuffer?.Length ?? 0);
        var result = im_copy_latest_video_frame(ref info, _frameBuffer, ref size);
        if (result == (int)NativeResult.BufferTooSmall)
        {
            _frameBuffer = new byte[size];
            info.StructSize = (uint)Marshal.SizeOf<NativeVideoFrameInfo>();
            result = im_copy_latest_video_frame(ref info, _frameBuffer, ref size);
        }
        if (result != 0 || _frameBuffer is null) return null;
        return new VideoFrame(info.Width, info.Height, info.Stride, info.Timestamp100Ns, _frameBuffer);
    }

    public VideoFrame? GetLatestPreviewFrame(uint maxWidth = 960, uint maxHeight = 2200)
    {
        var info = new NativeVideoFrameInfo { StructSize = (uint)Marshal.SizeOf<NativeVideoFrameInfo>() };
        uint size = (uint)(_frameBuffer?.Length ?? 0);
        var result = im_copy_latest_video_frame_scaled(ref info, _frameBuffer, ref size, maxWidth, maxHeight);
        if (result == (int)NativeResult.BufferTooSmall)
        {
            _frameBuffer = new byte[size];
            info.StructSize = (uint)Marshal.SizeOf<NativeVideoFrameInfo>();
            result = im_copy_latest_video_frame_scaled(ref info, _frameBuffer, ref size, maxWidth, maxHeight);
        }
        if (result != 0 || _frameBuffer is null) return null;
        return new VideoFrame(info.Width, info.Height, info.Stride, info.Timestamp100Ns, _frameBuffer);
    }

    private static string GetLastError(string fallback)
    {
        // The native ABI currently exposes human-readable Chinese diagnostics.
        // Keep those details in the native log, but do not leak mixed-language
        // text into the English UI until the ABI provides stable error codes.
        if (!LocalizationService.EffectiveCulture.Name.StartsWith(
                "zh", StringComparison.OrdinalIgnoreCase)) return fallback;
        var pointer = im_last_error();
        return pointer == 0 ? fallback : Marshal.PtrToStringUni(pointer) ?? fallback;
    }

    public void Dispose()
    {
        if (!_initialized) return;
        im_shutdown();
        _initialized = false;
    }
}
