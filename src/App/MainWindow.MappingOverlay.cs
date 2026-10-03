using System.Windows;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly Dictionary<nint, KeyboardMappingOverlayWindow> _mappingOverlays = [];
    private MappingPositionPick? _mappingPick;
    private bool _mappingRestoreSettings;

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
            result.Add(Surface(MainPreviewHost.WindowHandle, _windowSource.Handle,
                _viewModel.SourceVideoWidth, _viewModel.SourceVideoHeight, 0));
        var independent = _secondaryMirrors.GetWindowHandle(device.Udid);
        if (independent != 0 && _secondaryMirrors.TryGetControlGeometry(device.Udid, out var width, out var height, out var rotation))
            result.Add(Surface(independent, independent, width, height, rotation));
        return result;
    }

    private string? BeginMappingPositionPick(KeyboardMappingEntry entry, Action<KeyboardMappingEntry?> completed)
    {
        var surfaces = MappingSurfaces();
        var surface = surfaces.LastOrDefault();
        if (surface is null) return Localization.LocalizationService.Get("MappingPickNoPreview");
        EndMappingKeyCapture();
        CancelMappedGesture();
        _mappingRestoreSettings = _isSettingsPanelVisible;
        SetSettingsPanelVisible(false);
        _mappingPick = new(entry, surface, result =>
        {
            _mappingPick = null;
            if (_mappingRestoreSettings && !_mappingClosing) SetSettingsPanelVisible(true);
            _mappingRestoreSettings = false;
            if (!_mappingSettings.Enabled) _mappingTimer?.Stop();
            if (!_mappingClosing) completed(result);
            RefreshMappingStatus();
            RefreshMappingOverlays();
        });
        _mappingTimer?.Start();
        RefreshMappingOverlays();
        if (_mappingPick is not null && _mappingOverlays.TryGetValue(surface.Window, out var overlay))
            overlay.BeginPick(_mappingPick);
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
        var surfaces = _mappingSettings.Enabled || _mappingPick is not null ? MappingSurfaces() : [];
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
            if (!_mappingOverlays.TryGetValue(surface.Window, out var overlay))
            {
                overlay = new KeyboardMappingOverlayWindow(surface);
                _mappingOverlays.Add(surface.Window, overlay);
            }
            overlay.Update(surface, _mappingSettings.Enabled ? _mappingSettings.Mappings : []);
        }
    }
}
