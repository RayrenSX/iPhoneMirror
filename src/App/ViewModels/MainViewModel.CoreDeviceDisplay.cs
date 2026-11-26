using System.IO;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    internal Func<DeviceViewModel, CancellationToken, Task>? CoreDeviceMirrorStarter { get; set; }

    private async Task<bool> StartPairedNetworkSessionAsync(DeviceViewModel device, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_disposed || !Devices.Contains(device) || !CanQueueSessionLifecycleOperation(device) ||
            !TryBeginSessionLifecycleOperation(device.Udid)) return false;
        var ownsBusy = !IsBusy;
        if (ownsBusy) IsBusy = true;
        if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
            CaptureStatus = LocalizationService.Get("StartRequested");
        try
        {
            AddDiagnosticLog(AppLog.Event("capture_start_begin",
                ("device", AppLog.Device(device.Udid)), ("kind", "coredevice")));
            // Pairing and connection preparation acquire their own gates.
            // Never hold the native core gate while they are starting.
            await (CoreDeviceMirrorStarter ?? StartCoreDeviceMirrorAsync)(device, cancellation);
            return true;
        }
        finally
        {
            if (ownsBusy) IsBusy = false;
            EndSessionLifecycleOperation(device.Udid);
        }
    }

    private static string CoreDeviceMirrorFailureMessage(Exception error)
    {
        var key = (error as AutomationException)?.Code switch
        {
            "COREDEVICE_MIRROR_UNSUPPORTED" => "CoreDeviceMirrorUnsupported",
            "COREDEVICE_MIRROR_PHONE_BUSY" => "CoreDeviceMirrorPhoneBusy",
            "COREDEVICE_MIRROR_NO_FRAME" => "CoreDeviceMirrorNoFrame",
            "COREDEVICE_MIRROR_DISCONNECTED" or "SESSION_CHANGED" => "CoreDeviceMirrorDisconnected",
            "COREDEVICE_MIRROR_BUSY" => "CoreDeviceMirrorStopFirst",
            "DECODER_MISSING" => "CoreDeviceMirrorDecoder",
            _ => null,
        };
        return key is not null ? LocalizationService.Get(key) :
            error is InvalidOperationException ? error.Message : LocalizationService.Get("CoreDeviceMirrorFailed");
    }

    private async Task StartPairedNetworkCaptureAsync(DeviceViewModel device)
    {
        try { await StartPairedNetworkSessionAsync(device, _shutdownCancellation.Token); }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("capture", "coredevice_mirror_start_failed", error,
                ("device", AppLog.Device(device.Udid)));
            if (_disposed) return;
            var message = CoreDeviceMirrorFailureMessage(error);
            AddUiLog(LocalizationService.Format("StartFailedFormat", message));
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
                CaptureStatus = message;
            ShowWiredCaptureFailure(LocalizationService.Format("DeviceCaptureErrorTitleFormat", device.DisplayName),
                new CaptureFailureNotice(message, CaptureFailureStage.None, (int)NativeResult.CaptureBackendUnavailable), device.Udid);
        }
    }

    internal string? CoreDeviceMirrorUnavailable(DeviceViewModel device)
    {
        if (Version.TryParse(device.OsVersion, out var version) && version.Major < 27)
            return "CoreDeviceMirrorUnsupported";
        if (device.IsMediaCast || device.IsDisconnected || !_identityResolver.Resolve(device).IsResolved)
            return "CoreDeviceMirrorPair";
        if (_sessions.AnySession && Devices.Any(source =>
                DeviceViewModel.UdidEquals(AutomationPhysicalId(source.Udid), AutomationPhysicalId(device.Udid)) &&
                _sessions.Get(source.Udid) is { HasSession: true }))
            return "CoreDeviceMirrorStopFirst";
        return null;
    }

    internal async Task StartCoreDeviceMirrorAsync(DeviceViewModel device, CancellationToken cancellation)
    {
        if (CoreDeviceMirrorUnavailable(device) is { } unavailable)
            throw new InvalidOperationException(LocalizationService.Get(unavailable));
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "CoreDevice", "iPhoneMirror.MediaDecoder.exe")))
            throw new InvalidOperationException(LocalizationService.Get("CoreDeviceMirrorDecoder"));

        await PrepareDeviceHubConnectionAsync(device, cancellation);
        var bridge = GetReadyDeviceHubBridge(device) ??
            throw new InvalidOperationException(LocalizationService.Get("CoreDeviceMirrorPair"));
        var sharedMedia = bridge.AuthMode == "mediastream";
        await _coreGate.WaitAsync(cancellation);
        var ownsBusy = false;
        try
        {
            if (_disposed || !Devices.Contains(device))
                return;
            if (CoreDeviceMirrorUnavailable(device) is { } currentUnavailable)
                throw new InvalidOperationException(LocalizationService.Get(currentUnavailable));
            var state = GetOrCreateIndependentDeviceState(device);
            if (state.IsStarting || state.IsStopping)
                throw new InvalidOperationException(LocalizationService.Get("CoreDeviceMirrorStopFirst"));
            state.IsStarting = true;
            if (!IsBusy)
            {
                IsBusy = true;
                ownsBusy = true;
            }
            try
            {
                var settings = CaptureSessionStartSettings(state);
                var created = await _sessions.CreateAsync(() => _core.CreateExternalSession(device.Udid, false,
                    0, settings.RenderWidth, settings.RenderHeight, (uint)settings.FrameRate,
                    (uint)settings.DecoderPreference), cancellation);
                if (!created.Success || created.Handle is not { } handle)
                    throw new InvalidOperationException(created.Message);
                _sessions.SetHandle(state, handle);
                var adjustments = _core.SetDeviceImageAdjustments(handle,
                    settings.Brightness, settings.Contrast, settings.Saturation, settings.Gamma);
                if (!adjustments.Success) throw new InvalidOperationException(adjustments.Message);

                var feed = new CoreDeviceCaptureFeed(_core, handle,
                    pipe => StopCoreDeviceProducerAsync(device.Udid, bridge, sharedMedia, pipe));
                state.ExternalCapture = feed;
                var display = AppleDisplayCatalog.Find(device.ProductType);
                var width = display?.Width ?? 0;
                var height = display?.Height ?? 0;
                // Keep the panel's native dimensions across encoder tiers.
                // The preview renderer fits that canvas to the window.
                await bridge.DeviceHubAsync("mirror_start", new { pipe = feed.PipeName,
                    width, height, panelWidth = width, panelHeight = height, directVideo = true },
                    () => !_disposed && ReferenceEquals(GetReadyDeviceHubBridge(device), bridge), cancellation);
                await feed.FirstFrame.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
                state.PlayAudio = false;
                state.MarkVideoSettingsApplied(settings.RenderWidth, settings.RenderHeight, settings.FrameRate,
                    settings.DecoderPreference, settings.Brightness, settings.Contrast, settings.Saturation, settings.Gamma);
                state.IsStarting = false;
                if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, device.Udid))
                {
                    _activeCaptureUdid = device.Udid;
                    IsCapturing = true;
                    NativeCore.SelectPreviewSession(handle);
                    CaptureStatus = LocalizationService.Get("CaptureStreaming");
                }
                NotifyCaptureSessionChanged();
                OnPropertyChanged(nameof(CurrentSessionHandle));
                _ = ObserveCoreDeviceMirrorAsync(device.Udid, handle.RawHandle, feed);
            }
            catch
            {
                if (state.ExternalCapture is null && sharedMedia)
                    await RetireCoreDeviceMirrorBridgeAsync(device.Udid, bridge, sharedMedia);
                await _sessions.StopAndDestroyAsync(state);
                throw;
            }
            finally { state.IsStarting = false; }
        }
        finally
        {
            if (ownsBusy) IsBusy = false;
            _coreGate.Release();
        }
    }

    private async Task StopCoreDeviceProducerAsync(string udid, UsbTouchBridgeHost bridge,
        bool sharedMedia, string pipe)
    {
        // A media-authenticated bridge shares its one phone-side stream with
        // the mirror. Stopping the decoder alone leaves that stream running.
        if (!sharedMedia && bridge.AuthMode != "mediastream" && bridge.IsReady)
        {
            try
            {
                var result = await bridge.DeviceHubAsync("mirror_stop", new { pipe }, () => true, CancellationToken.None);
                if (!result.TryGetProperty("sharesControlMedia", out var shared) ||
                    shared.ValueKind != System.Text.Json.JsonValueKind.True)
                    return;
            }
            catch (Exception error) when (error is AutomationException or OperationCanceledException or TimeoutException or IOException)
            {
                DiagnosticLogger.Exception("capture", "coredevice_mirror_stop_failed", error);
            }
        }
        await RetireCoreDeviceMirrorBridgeAsync(udid, bridge, sharedMedia);
    }

    private async Task RetireCoreDeviceMirrorBridgeAsync(string udid, UsbTouchBridgeHost bridge, bool sharedMedia)
    {
        if (FindControl(udid) is not { } control) return;
        // Recovery can replace the process while the preview is still tearing
        // down. Cancel recovery for this phone's shared stream as well.
        var sameSharedDevice = sharedMedia && bridge.Udid is { } appleUdid &&
            DeviceViewModel.UdidEquals(control.AppleUdid, appleUdid);
        if (ReferenceEquals(control.WiredBridge, bridge) ||
            sameSharedDevice && control.WiredEnabled && control.WiredBridge?.AuthMode != "direct")
            await DisableUsbControlAsync(control);
        if (ReferenceEquals(control.WirelessBridge, bridge) ||
            sameSharedDevice && control.WirelessEnabled && control.WirelessBridge?.AuthMode != "direct")
            await DisableWirelessControlAsync(control);
    }

    private async Task ObserveCoreDeviceMirrorAsync(string udid, ulong handle, CoreDeviceCaptureFeed feed)
    {
        try
        {
            await feed.Completion;
            if (_disposed || _sessions.Get(udid) is not { IsStopping: false, ExternalCapture: { } external } ||
                !ReferenceEquals(external, feed)) return;
            await StopDeviceSessionAsync(udid, handle);
            if (DeviceViewModel.UdidEquals(SelectedDevice?.Udid, udid) && !HasCaptureSession)
                CaptureStatus = LocalizationService.Get("CoreDeviceMirrorDisconnected");
        }
        catch (Exception error) { DiagnosticLogger.Exception("capture", "coredevice_mirror_disconnected", error); }
    }

    private bool IsIPhoneDuo(DeviceViewModel device) =>
        IsDuoProduct(device) || (_identityResolver.Resolve(device).AppleUdid is { Length: > 0 } appleUdid &&
            Devices.Any(candidate => DeviceViewModel.UdidEquals(candidate.Udid, appleUdid) && IsDuoProduct(candidate)));

    private static bool IsDuoProduct(DeviceViewModel device) =>
        string.Equals(device.ProductType, "iPhone19,4", StringComparison.OrdinalIgnoreCase);

    private (uint Width, uint Height) CoreDeviceControlPreviewSize(DeviceViewModel device)
    {
        // Older Duo DDIs omit the active-panel flag. Use the dimensions of
        // this device's live capture to disambiguate the built-in panels.
        if (_sessions.Get(device.Udid) is not { HasSession: true, IsStopping: false, Handle: { } handle } capture)
            return default;
        if (capture.SourceGeometry is { Width: > 0, Height: > 0 } geometry) return geometry;
        var status = _captureStatusReader(handle);
        return status.State == CaptureState.Streaming ? (status.Width, status.Height) : default;
    }

    private async Task UpdateCoreDeviceControlPreviewAsync(string udid, uint width, uint height)
    {
        if (FindControl(udid) is not { } control) return;
        var bridge = control.WiredEnabled ? control.WiredBridge : control.WirelessBridge;
        if (bridge is not { SupportsTouchDisplayPreview: true, IsReady: true }) return;
        try { await bridge.UpdateTouchDisplayPreviewAsync(width, height, _shutdownCancellation.Token); }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLogger.ReverseControlWarning("touch_display", "preview_size_send_failed",
                ("device", AppLog.Device(udid)), ("error", AppLog.Error(error)));
        }
    }
}
