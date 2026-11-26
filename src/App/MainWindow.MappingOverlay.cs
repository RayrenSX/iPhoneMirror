using System.Windows;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly Dictionary<nint, KeyboardMappingOverlayWindow> _mappingOverlays = [];
    private MappingPositionPick? _mappingPick;

    private List<MappingPreviewSurface> MappingSurfaces()
    {
        var result = new List<MappingPreviewSurface>();
        var device = _viewModel.SelectedDevice;
        if (_mappingClosing || _bossKeyHidden || device is null || !_viewModel.IsCapturing ||
            _viewModel.IsAudioOnlyAirPlay || _viewModel.IsVideoProtected ||
            _viewModel.CurrentSessionHandle == 0 || _viewModel.SourceVideoWidth == 0 || _viewModel.SourceVideoHeight == 0)
            return result;
        MappingPreviewSurface Surface(nint window, nint owner, uint width, uint height, int rotation) =>
            new(window, owner, device.Udid, _viewModel.CurrentSessionHandle, width, height, rotation,
                _viewModel.AppliedBluetoothPortraitMouseDirection, _viewModel.AppliedBluetoothLandscapeMouseDirection,
                _viewModel.AppliedBluetoothMouseReverseHorizontal, _viewModel.AppliedBluetoothMouseReverseVertical);
        if (MainPreviewHost.IsVisible && MainPreviewHost.WindowHandle != 0 && _windowSource is not null)
        {
            var frame = GetMainPreviewDeviceFrame();
            result.Add(Surface(MainPreviewHost.WindowHandle, _windowSource.Handle,
                _viewModel.SourceVideoWidth, _viewModel.SourceVideoHeight, 0) with
                { Frame = frame.Frame, FrameRotation = frame.Rotation });
        }
        var independent = _secondaryMirrors.GetWindowHandle(device.Udid);
        if (independent != 0 && _secondaryMirrors.TryGetControlGeometry(device.Udid, out var width, out var height, out var rotation))
        {
            var frame = _secondaryMirrors.GetDeviceFrame(device.Udid);
            result.Add(Surface(independent, independent, width, height, rotation) with
                { Frame = frame.Frame, FrameRotation = frame.Rotation });
        }
        return result;
    }

    private string? BeginMappingPositionPick(KeyboardMappingEntry entry, Action<KeyboardMappingEntry?> completed)
    {
        var surfaces = MappingSurfaces();
        var surface = surfaces.LastOrDefault();
        if (surface is null) return Localization.LocalizationService.Get("MappingPickNoPreview");
        EndMappingKeyCapture();
        CancelMappedGesture();
        _mappingPick = new(entry, surface, result =>
        {
            _mappingPick = null;
            if (!_mappingSettings.HasActiveMappings) _mappingTimer?.Stop();
            if (!_mappingClosing) completed(result);
            RefreshMappingStatus();
            RefreshMappingOverlays();
        });
        _mappingTimer?.Start();
        RefreshMappingOverlays();
        if (_mappingPick is { } pick)
        {
            if (!_mappingOverlays.TryGetValue(surface.Window, out var overlay) || !overlay.IsVisible)
            {
                pick.Completed(null);
                return Localization.LocalizationService.Get("MappingPickNoPreview");
            }
            overlay.BeginPick(pick);
        }
        RefreshMappingStatus();
        return null;
    }

    private void CancelMappingPositionPick()
    {
        if (_mappingPick is not { } pick) return;
        if (_mappingOverlays.TryGetValue(pick.Surface.Window, out var overlay) && overlay.IsPicking)
            overlay.CancelPick();
        else pick.Completed(null);
    }

    private void RefreshMappingOverlays()
    {
        var surfaces = _mappingSettings.HasActiveMappings || _mappingPick is not null ? MappingSurfaces() : [];
        if (_mappingPick is { } pick && !surfaces.Contains(pick.Surface))
        {
            CancelMappingPositionPick();
            return;
        }
        foreach (var handle in _mappingOverlays.Keys.Except(surfaces.Select(s => s.Window)).ToArray())
        {
            var overlay = _mappingOverlays[handle];
            _mappingOverlays.Remove(handle);
            overlay.Close();
        }
        foreach (var surface in surfaces)
        {
            if (!_mappingOverlays.TryGetValue(surface.Window, out var overlay) || overlay.IsClosed)
            {
                overlay = new KeyboardMappingOverlayWindow(surface);
                _mappingOverlays[surface.Window] = overlay;
            }
            overlay.Update(surface, _mappingSettings.Enabled ? _mappingSettings.Mappings : []);
        }
    }
}
