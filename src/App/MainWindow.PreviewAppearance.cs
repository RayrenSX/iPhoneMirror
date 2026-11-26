using System.Windows;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private (ulong Session, nint Window, DeviceFrameProfile Artwork, DeviceFrameHitTest Mask)? _mainPreviewFrame;

    private void ApplyMainPreviewAppearance()
    {
        var rounded = _viewModel.MainPreviewRoundedCorners;
        var visible = IsMainNativePreviewVisible;
        var session = _viewModel.CurrentSessionHandle;
        var window = MainPreviewHost.WindowHandle;
        var device = _viewModel.SelectedDevice;
        var model = device?.ProductType;
        var width = _viewModel.SourceVideoWidth;
        var height = _viewModel.SourceVideoHeight;
        DeviceFrameProfile? wanted = null;
        if (_viewModel.MainPreviewPhoneFrame && visible && !_isFullScreen && device is not null)
            wanted = DeviceFrameCatalog.Resolve(model, _secondaryMirrors.FrameAppearance(device), width, height).Profile;

        var applied = true;
        var previous = _mainPreviewFrame;
        var sameTarget = previous is { } old && old.Session == session && old.Window == window;
        if (!sameTarget) _mainPreviewFrame = null;
        if (visible && session != 0 && window != 0)
        {
            if (wanted is not null && !ReferenceEquals(wanted, _mainPreviewFrame?.Artwork))
            {
                try
                {
                    var mask = new DeviceFrameHitTest(wanted);
                    applied = NativeCore.SetDeviceWindowFrameArtwork(session, window, wanted) &&
                        NativeCore.SetDeviceWindowPhoneFrame(session, window, true);
                    if (applied) _mainPreviewFrame = (session, window, wanted, mask);
                }
                catch (Exception error)
                {
                    applied = false;
                    DiagnosticLogger.Exception("window", "main_preview_frame_failed", error);
                }
            }
            if ((wanted is null && sameTarget) || !applied)
            {
                applied = NativeCore.SetDeviceWindowPhoneFrame(session, window, false) && applied;
                _mainPreviewFrame = null;
            }
            var profile = rounded ? DeviceCornerProfileResolver.Resolve(model, width, height) : DeviceCornerProfile.Rectangular;
            _ = NativeCore.SetDeviceWindowCornerProfile(session, window, profile.RadiusRatio, profile.CurveExponent);
        }
        else _mainPreviewFrame = null;

        if (!ReferenceEquals(previous?.Artwork, _mainPreviewFrame?.Artwork) ||
            (previous is not null && !sameTarget))
        {
            MainPreviewHost.ReleasePointerCapture();
            OnControlPointerInput(this, new PreviewPointerEventArgs(PreviewPointerKind.Reset, 0, 0, 0, 0));
            CloseScreenTextSelection();
            RefreshMappingOverlays();
        }
        RefreshCompletePreviewFit();
        CompletePreviewStage.Opacity = _mainPreviewFrame is null ? 0.55 : 0.30;
        OnWorkspaceLayoutChanged(this, new RoutedEventArgs());
        _viewModel.MainPreviewPhoneFrameApplyFailed = !applied;
    }

    private (DeviceFrameProfile? Frame, int Rotation) GetMainPreviewDeviceFrame()
    {
        if (_mainPreviewFrame is not { } frame || frame.Session != _viewModel.CurrentSessionHandle ||
            frame.Window != MainPreviewHost.WindowHandle || _isFullScreen) return (null, 0);
        var expected = _viewModel.SourceVideoWidth > _viewModel.SourceVideoHeight ? 1 : 0;
        var rotation = NativeCore.GetDeviceWindowPhoneFrameOrientation(frame.Session, frame.Window);
        return (frame.Artwork, rotation >= 0 && (rotation & 1) == expected ? rotation : expected);
    }

    private PreviewPointerEventArgs? MapMainPreviewFramePointer(PreviewPointerEventArgs input)
    {
        if (_mainPreviewFrame is not { } state || input.Kind == PreviewPointerKind.Reset ||
            input.SurfaceWidth <= 0 || input.SurfaceHeight <= 0) return input;
        var (frame, rotation) = GetMainPreviewDeviceFrame();
        if (frame is null || !HasLightweightContentDimensions) return input;
        if (input.Kind is PreviewPointerKind.ButtonDown or PreviewPointerKind.TouchDown or PreviewPointerKind.Wheel &&
            !state.Mask.Contains(rotation, input)) return null;
        var sourceInput = new PreviewPointerEventArgs(input.Kind, input.X, input.Y, input.Button, input.Wheel,
            input.SurfaceWidth, input.SurfaceHeight, _viewModel.SourceVideoWidth, _viewModel.SourceVideoHeight,
            input.Rotation, input.TouchId, input.SourceWindow) { WindowsButtons = input.WindowsButtons };
        return PhoneFrameGeometry.MapPointer(frame, rotation, sourceInput);
    }
}
