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

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCaptureOptions
{
    public uint StructSize;
    public uint ApiVersion;
    public uint RequestedWidth;
    public uint RequestedHeight;
    public uint TargetFps;
    public int PlayAudio;
    public float AudioVolume;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public uint[] Reserved;
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

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int im_wireless_receiver_start(
        [MarshalAs(UnmanagedType.LPWStr)] string receiverName,
        [MarshalAs(UnmanagedType.LPWStr)] string hostPath);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void im_wireless_receiver_stop();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_wireless_receiver_get_status(out int running, out int ready);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_refresh_wireless_devices(
        [Out] NativeDeviceInfo[]? devices, ref uint count);

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
    private static extern int im_set_preview_corner_profile(float normalizedRadius, float curveExponent);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int im_session_create([MarshalAs(UnmanagedType.LPWStr)] string udid,
        ref NativeCaptureOptions options, out ulong handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int im_wireless_session_create(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        ref NativeCaptureOptions options, out ulong handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_stop(ulong handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void im_session_destroy(ulong handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_get_status(ulong handle, ref NativeCaptureStatus status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_attach_preview(ulong handle, nint hwnd);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void im_session_detach_preview(ulong handle, nint hwnd);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_set_video_preferences(ulong handle, uint width, uint height, uint fps);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_set_audio_enabled(ulong handle, int enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_set_audio_volume(ulong handle, float volume);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_set_corner_profile(ulong handle, float radius, float exponent);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_get_latest_video_timestamp(ulong handle, out long timestamp);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_copy_latest_video_frame(ulong handle,
        ref NativeVideoFrameInfo info, [Out] byte[]? buffer, ref uint bufferSize,
        uint maxWidth, uint maxHeight);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_force_preview_refresh(ulong handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_set_window_corner_profile(ulong handle, nint hwnd,
        float radius, float exponent);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int im_session_set_window_rotation(ulong handle, nint hwnd,
        int quarterTurns);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint im_last_error();

    private static long _selectedPreviewSession;
    private static nint _selectedPreviewWindow;

    internal static void SelectPreviewSession(ulong handle)
    {
        var previous = unchecked((ulong)Interlocked.Exchange(ref _selectedPreviewSession,
            unchecked((long)handle)));
        if (previous != 0 && previous != handle && _selectedPreviewWindow != 0)
            im_session_detach_preview(previous, _selectedPreviewWindow);
        else if (previous == 0 && handle != 0) im_detach_preview_window();
    }

    internal static bool AttachPreviewWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        var handle = unchecked((ulong)Interlocked.Read(ref _selectedPreviewSession));
        var attached = handle != 0 ? im_session_attach_preview(handle, hwnd) == 0
            : im_attach_preview_window(hwnd) == 0;
        if (attached) _selectedPreviewWindow = hwnd;
        return attached;
    }

    internal static void DetachPreviewWindow()
    {
        var handle = unchecked((ulong)Interlocked.Read(ref _selectedPreviewSession));
        if (handle != 0 && _selectedPreviewWindow != 0)
            im_session_detach_preview(handle, _selectedPreviewWindow);
        else im_detach_preview_window();
        _selectedPreviewWindow = 0;
    }

    internal static bool AttachDevicePreview(ulong handle, nint hwnd) =>
        handle != 0 && hwnd != 0 && im_session_attach_preview(handle, hwnd) == 0;

    internal static void DetachDevicePreview(ulong handle, nint hwnd)
    {
        if (handle != 0 && hwnd != 0) im_session_detach_preview(handle, hwnd);
    }

    internal static bool SetDeviceWindowCornerProfile(ulong handle, nint hwnd,
        double radius, double exponent) => handle != 0 && hwnd != 0 &&
        im_session_set_window_corner_profile(handle, hwnd,
            Math.Clamp((float)radius, 0, 0.5f), Math.Clamp((float)exponent, 1.5f, 8)) == 0;

    internal static bool SetDeviceWindowRotation(ulong handle, nint hwnd, int turns) =>
        handle != 0 && hwnd != 0 && im_session_set_window_rotation(handle, hwnd, turns) == 0;

    internal static bool ForcePreviewRefresh()
    {
        try
        {
            var handle = unchecked((ulong)Interlocked.Read(ref _selectedPreviewSession));
            return (handle != 0 ? im_session_force_preview_refresh(handle)
                : im_force_preview_refresh()) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool SetPreviewCornerProfile(double normalizedRadius, double curveExponent)
    {
        try
        {
            var handle = unchecked((ulong)Interlocked.Read(ref _selectedPreviewSession));
            if (handle != 0) return SetDeviceCornerProfile(handle, normalizedRadius, curveExponent);
            return im_set_preview_corner_profile(
                Math.Clamp((float)normalizedRadius, 0.0f, 0.5f),
                Math.Clamp((float)curveExponent, 1.5f, 8.0f)) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            // A mismatched older native DLL should keep rendering with its
            // historical iPhone curve instead of crashing the GUI.
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

    public (bool Success, string Message) StartWirelessReceiver(
        string receiverName, string hostPath)
    {
        var result = im_wireless_receiver_start(receiverName, hostPath);
        return result == 0
            ? (true, LocalizationService.Get("WirelessReady"))
            : (false, GetLastError(LocalizationService.Get("WirelessReceiverMissing")));
    }

    public (bool Running, bool Ready) GetWirelessReceiverStatus()
    {
        var result = im_wireless_receiver_get_status(out var running, out var ready);
        return result == 0 ? (running != 0, ready != 0) : (false, false);
    }

    public void StopWirelessReceiver() => im_wireless_receiver_stop();

    public IReadOnlyList<NativeDeviceInfo> GetWirelessDevices()
    {
        for (var attempt = 0; attempt < 3; ++attempt)
        {
            uint count = 0;
            var result = im_refresh_wireless_devices(null, ref count);
            if (result != 0) throw new InvalidOperationException(GetLastError(
                LocalizationService.Get("EnumerateDevicesFailed")));
            if (count == 0) return [];

            var devices = new NativeDeviceInfo[count];
            for (var i = 0; i < devices.Length; ++i)
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
            result = im_refresh_wireless_devices(devices, ref capacity);
            if (result == (int)NativeResult.BufferTooSmall) continue;
            if (result != 0) throw new InvalidOperationException(GetLastError(
                LocalizationService.Get("EnumerateDevicesFailed")));
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

    public (bool Success, ulong Handle, string Message) CreateDeviceSession(string udid,
        uint width, uint height, uint fps, bool playAudio, double volume,
        uint usbWidth = 0, uint usbHeight = 0, uint usbProjectionMode = 0)
    {
        var options = new NativeCaptureOptions
        {
            StructSize = (uint)Marshal.SizeOf<NativeCaptureOptions>(),
            ApiVersion = 12,
            RequestedWidth = width,
            RequestedHeight = height,
            TargetFps = fps,
            PlayAudio = playAudio ? 1 : 0,
            AudioVolume = Math.Clamp((float)volume, 0, 1),
            Reserved = new uint[5],
        };
        options.Reserved[0] = usbWidth;
        options.Reserved[1] = usbHeight;
        options.Reserved[2] = Math.Min(usbProjectionMode, 2U);
        var result = im_session_create(udid, ref options, out var handle);
        return result == 0
            ? (true, handle, LocalizationService.Get("CaptureStarted"))
            : (false, 0, GetLastError(LocalizationService.Get("CannotStartCapture")));
    }

    public (bool Success, ulong Handle, string Message) CreateWirelessSession(
        string deviceId, uint width, uint height, uint fps,
        bool playAudio, double volume)
    {
        var options = new NativeCaptureOptions
        {
            StructSize = (uint)Marshal.SizeOf<NativeCaptureOptions>(),
            ApiVersion = 12,
            RequestedWidth = width,
            RequestedHeight = height,
            TargetFps = fps,
            PlayAudio = playAudio ? 1 : 0,
            AudioVolume = Math.Clamp((float)volume, 0, 1),
            Reserved = new uint[5],
        };
        var result = im_wireless_session_create(deviceId, ref options, out var handle);
        return result == 0
            ? (true, handle, LocalizationService.Get("CaptureStarted"))
            : (false, 0, GetLastError(LocalizationService.Get("CannotStartCapture")));
    }

    public void StopDeviceSession(ulong handle)
    {
        if (handle == 0) return;
        var result = im_session_stop(handle);
        if (result != 0) throw new InvalidOperationException(GetLastError(
            LocalizationService.Get("StopFailedFormat")));
    }

    public void DestroyDeviceSession(ulong handle)
    {
        if (handle != 0) im_session_destroy(handle);
    }

    public NativeCaptureStatus GetDeviceSessionStatus(ulong handle)
    {
        var status = new NativeCaptureStatus
        {
            StructSize = (uint)Marshal.SizeOf<NativeCaptureStatus>(),
            Message = string.Empty,
        };
        var result = im_session_get_status(handle, ref status);
        if (result != 0) throw new InvalidOperationException(GetLastError(
            LocalizationService.Get("ReadCaptureStatusFailed")));
        return status;
    }

    public (bool Success, string Message) SetDeviceVideoPreferences(ulong handle,
        uint width, uint height, uint fps)
    {
        var result = im_session_set_video_preferences(handle, width, height, fps);
        return result == 0 ? (true, LocalizationService.Get("VideoPreferencesApplied"))
            : (false, GetLastError(LocalizationService.Get("VideoPreferencesUpdateFailed")));
    }

    public void SetDeviceAudioEnabled(ulong handle, bool enabled)
    {
        if (im_session_set_audio_enabled(handle, enabled ? 1 : 0) != 0)
            throw new InvalidOperationException(GetLastError(LocalizationService.Get("AudioStateUpdateFailed")));
    }

    public void SetDeviceAudioVolume(ulong handle, double volume)
    {
        if (im_session_set_audio_volume(handle, Math.Clamp((float)volume, 0, 1)) != 0)
            throw new InvalidOperationException(GetLastError(LocalizationService.Get("AudioVolumeUpdateFailed")));
    }

    internal static bool SetDeviceCornerProfile(ulong handle, double radius, double exponent) =>
        handle != 0 && im_session_set_corner_profile(handle,
            Math.Clamp((float)radius, 0, 0.5f), Math.Clamp((float)exponent, 1.5f, 8)) == 0;

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
        var handle = unchecked((ulong)Interlocked.Read(ref _selectedPreviewSession));
        long timestamp;
        var result = handle != 0
            ? im_session_get_latest_video_timestamp(handle, out timestamp)
            : im_get_latest_video_timestamp(out timestamp);
        return result == 0 ? timestamp : 0;
    }

    public VideoFrame? GetLatestVideoFrame()
    {
        var info = new NativeVideoFrameInfo { StructSize = (uint)Marshal.SizeOf<NativeVideoFrameInfo>() };
        uint size = (uint)(_frameBuffer?.Length ?? 0);
        var handle = unchecked((ulong)Interlocked.Read(ref _selectedPreviewSession));
        var result = handle != 0
            ? im_session_copy_latest_video_frame(handle, ref info, _frameBuffer, ref size, 0, 0)
            : im_copy_latest_video_frame(ref info, _frameBuffer, ref size);
        if (result == (int)NativeResult.BufferTooSmall)
        {
            _frameBuffer = new byte[size];
            info.StructSize = (uint)Marshal.SizeOf<NativeVideoFrameInfo>();
            result = handle != 0
                ? im_session_copy_latest_video_frame(handle, ref info, _frameBuffer, ref size, 0, 0)
                : im_copy_latest_video_frame(ref info, _frameBuffer, ref size);
        }
        if (result != 0 || _frameBuffer is null) return null;
        return new VideoFrame(info.Width, info.Height, info.Stride, info.Timestamp100Ns, _frameBuffer);
    }

    public VideoFrame? GetLatestPreviewFrame(uint maxWidth = 960, uint maxHeight = 2200)
    {
        var info = new NativeVideoFrameInfo { StructSize = (uint)Marshal.SizeOf<NativeVideoFrameInfo>() };
        uint size = (uint)(_frameBuffer?.Length ?? 0);
        var handle = unchecked((ulong)Interlocked.Read(ref _selectedPreviewSession));
        var result = handle != 0
            ? im_session_copy_latest_video_frame(handle, ref info, _frameBuffer, ref size, maxWidth, maxHeight)
            : im_copy_latest_video_frame_scaled(ref info, _frameBuffer, ref size, maxWidth, maxHeight);
        if (result == (int)NativeResult.BufferTooSmall)
        {
            _frameBuffer = new byte[size];
            info.StructSize = (uint)Marshal.SizeOf<NativeVideoFrameInfo>();
            result = handle != 0
                ? im_session_copy_latest_video_frame(handle, ref info, _frameBuffer, ref size, maxWidth, maxHeight)
                : im_copy_latest_video_frame_scaled(ref info, _frameBuffer, ref size, maxWidth, maxHeight);
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
