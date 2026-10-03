using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using IPhoneMirror.UI.Animations;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private RevealTransition? _workspaceTransition;
    private bool _applyingWorkspaceWindowBounds;
    private bool _settlingWorkspace;
    private bool _workspaceWindowFitOnResume;
    private nint _workspaceAnimationWindow;
    private double _devicePageStartOpacity, _mirroringPageStartOpacity;

    private void PrepareWorkspacePages(bool showDevices, bool showMirroring)
    {
        // When the host opens, the clip is the reveal. Only a switch between
        // two pages in the already open host needs a small crossfade.
        var switching = LeftPanelHost.Width > 0 &&
            ((showDevices && MirroringPanel.Visibility == Visibility.Visible) ||
             (showMirroring && DevicePanel.Visibility == Visibility.Visible));
        _devicePageStartOpacity = DevicePanel.Visibility == Visibility.Visible
            ? DevicePanel.Opacity : switching ? 0 : 1;
        _mirroringPageStartOpacity = MirroringPanel.Visibility == Visibility.Visible
            ? MirroringPanel.Opacity : switching ? 0 : 1;
        if (showDevices) DevicePanel.Visibility = Visibility.Visible;
        if (showMirroring) MirroringPanel.Visibility = Visibility.Visible;
        DevicePanel.Opacity = _devicePageStartOpacity;
        MirroringPanel.Opacity = _mirroringPageStartOpacity;
        DevicePanel.IsHitTestVisible = showDevices;
        MirroringPanel.IsHitTestVisible = showMirroring;
        ControlPanel.IsHitTestVisible = _isSettingsPanelVisible;
    }

    private void StartWorkspaceTransition()
    {
        _workspaceTransition ??= new RevealTransition(ApplyWorkspaceTransitionFrame,
            CompleteWorkspaceTransition);
        _workspaceTransition.Start(this);
    }

    private void AnimateCompleteWorkspace(bool showLeft, bool showSettings)
    {
        _workspaceLeftSurfaceStartWidth = GetLightweightElementWidth(LeftPanelHost);
        _workspaceRightSurfaceStartWidth = GetLightweightElementWidth(ControlPanel);
        _workspaceLeftSurfaceTargetWidth = showLeft ? 300 : 0;
        _workspaceRightSurfaceTargetWidth = showSettings ? 336 : 0;
        _workspaceLeftGapStartWidth = GetLightweightColumnWidth(LeftGapColumn);
        _workspaceRightGapStartWidth = GetLightweightColumnWidth(RightGapColumn);
        _workspaceLeftGapTargetWidth = showLeft ? 18 : 0;
        _workspaceRightGapTargetWidth = showSettings ? 18 : 0;
        _workspaceSurfaceAnimationActive = true;
        if (showLeft) LeftPanelHost.Visibility = Visibility.Visible;
        if (showSettings) ControlPanel.Visibility = Visibility.Visible;
        StartWorkspaceTransition();
    }

    private void ApplyWorkspaceTransitionFrame(double progress)
    {
        // Widths and gaps use the SAME eased value as the native window. No
        // business work, forced UpdateLayout, per-frame subscription or timer.
        ApplyWorkspaceSurfaceWidths(progress);
        if (_leftWorkspacePanel != LeftWorkspacePanel.None)
        {
            var deviceOpacity = _leftWorkspacePanel == LeftWorkspacePanel.Devices ? 1d : 0;
            var mirroringOpacity = _leftWorkspacePanel == LeftWorkspacePanel.Mirroring ? 1d : 0;
            if (DevicePanel.IsVisible && _devicePageStartOpacity != deviceOpacity)
                DevicePanel.Opacity = Interpolate(_devicePageStartOpacity, deviceOpacity, progress);
            if (MirroringPanel.IsVisible && _mirroringPageStartOpacity != mirroringOpacity)
                MirroringPanel.Opacity = Interpolate(_mirroringPageStartOpacity, mirroringOpacity, progress);
        }
        if (_lightweightWindowAnimationActive)
            ApplyLightweightWindowAnimationFrame(_workspaceAnimationWindow, progress);
    }

    private void CompleteWorkspaceTransition()
    {
        if (_settlingWorkspace) return;
        _settlingWorkspace = true;
        try
        {
            ApplyWorkspaceSurfaceWidths(1);
            if (_lightweightWindowAnimationActive)
            {
                _workspaceWindowFitOnResume = false;
                ApplyLightweightWindowAnimationFrame(_workspaceAnimationWindow, 1, force: true);
                SynchronizeLightweightWindowPosition();
                _applyingWorkspaceWindowBounds = true;
                try { Width = _lightweightWindowWidthTargetDips; }
                finally { _applyingWorkspaceWindowBounds = false; }
            }
            _workspaceTransition?.Stop();
            _lightweightWindowAnimationActive = false;
            _workspaceAnimationWindow = 0;
            CompleteWorkspaceSurfaceAnimation();
            SetWorkspacePageImmediate(DevicePanel, _leftWorkspacePanel == LeftWorkspacePanel.Devices);
            SetWorkspacePageImmediate(MirroringPanel, _leftWorkspacePanel == LeftWorkspacePanel.Mirroring);
            ReleaseLightweightCenterWidth();
            _retainWorkspaceStackDuringTransition = false;
            OnWorkspaceLayoutChanged(this, new RoutedEventArgs());
        }
        finally { _settlingWorkspace = false; }
    }

    // Explicit user resizing, hide/unload and DPI changes must never leave a
    // callback resizing the HWND back to a stale rectangle after the operation.
    private void SettleWorkspaceForEnvironmentChange()
    {
        if (_workspaceTransition?.IsRunning != true) return;
        // Suspension is not a user resize. Remember to finish window geometry
        // after restore, using the then-current DPI, work area and content size.
        if (_lightweightWindowAnimationActive &&
            (!IsVisible || WindowState == WindowState.Minimized))
            _workspaceWindowFitOnResume = true;
        _lightweightWindowAnimationActive = false;
        CompleteWorkspaceTransition();
    }

    private void ResumeWorkspaceWindowFit()
    {
        if (!_workspaceWindowFitOnResume) return;
        if (!_viewModel.IsLightweightApplicationMode || _isFullScreen ||
            _isWindowMaximized || _shutdownStarted)
        {
            _workspaceWindowFitOnResume = false;
            return;
        }
        if (IsVisible && IsLoaded && WindowState == WindowState.Normal)
            QueueInitialLightweightWorkspaceFit();
        // The existing Loaded-priority fit is coalesced. Keep the flag until
        // it actually runs, since the window can hide again before that turn.
    }

    private void OnWorkspaceWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_applyingWorkspaceWindowBounds && !_settlingWorkspace)
            SettleWorkspaceForEnvironmentChange();
    }

    private void OnWorkspaceWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) SettleWorkspaceForEnvironmentChange();
        else ResumeWorkspaceWindowFit();
    }

    private void OnWorkspaceUnloaded(object sender, RoutedEventArgs e) =>
        SettleWorkspaceForEnvironmentChange();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        SettleWorkspaceForEnvironmentChange();
        base.OnDpiChanged(oldDpi, newDpi);
    }
}
