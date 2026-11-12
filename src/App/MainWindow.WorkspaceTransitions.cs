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
    private bool _workspaceTreeSettlementQueued;
    private int _workspaceLayoutPassDepth;
    private bool _workspaceFrameDeferred;
    private double _workspaceDeferredProgress;
    private int _workspaceDeferredRevision;
    private int _workspaceDeferredLayoutWrites;
    private readonly Dictionary<FrameworkElement, Visibility> _workspaceExternalVisibility = [];
    private nint _workspaceAnimationWindow;
    private double _devicePageStartOpacity, _mirroringPageStartOpacity;
    private double _workspacePreviewStartWidth, _workspacePreviewTargetWidth;
    private bool _workspacePreviewAnimationActive;

    protected override Size MeasureOverride(Size availableSize)
    {
        _workspaceLayoutPassDepth++;
        try { return base.MeasureOverride(availableSize); }
        finally { _workspaceLayoutPassDepth--; }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _workspaceLayoutPassDepth++;
        try { return base.ArrangeOverride(finalSize); }
        finally { _workspaceLayoutPassDepth--; }
    }

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
        _completeResizePreviewWidth = 0;
        _workspaceTransition ??= new RevealTransition(ApplyWorkspaceTransitionFrame,
            CompleteWorkspaceTransition);
        _workspaceTransition.Start(this);
    }

    private void AnimateCompleteWorkspace(bool showLeft, bool showSettings)
    {
        _workspaceLeftSurfaceStartWidth = GetLightweightElementWidth(LeftPanelHost);
        _workspaceRightSurfaceStartWidth = GetLightweightElementWidth(ControlPanel);
        _workspaceLeftSurfaceTargetWidth = showLeft ? WorkspaceDeviceWidth : 0;
        _workspaceRightSurfaceTargetWidth = showSettings ? WorkspaceSettingsWidth : 0;
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
        if (_workspaceLayoutPassDepth > 0)
        {
            _workspaceDeferredLayoutWrites++;
            _workspaceDeferredProgress = progress;
            _workspaceDeferredRevision = _workspaceTransition?.Revision ?? -1;
            if (_workspaceFrameDeferred) return;
            _workspaceFrameDeferred = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                _workspaceFrameDeferred = false;
                if (_workspaceTransition?.IsRunning == true && _workspaceTransition.Revision == _workspaceDeferredRevision)
                    ApplyWorkspaceTransitionFrame(_workspaceDeferredProgress);
            });
            return;
        }
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
        if (_workspacePreviewAnimationActive)
        {
            SetLightweightPreviewMaximumWidth(Interpolate(_workspacePreviewStartWidth,
                _workspacePreviewTargetWidth, progress));
        }
        if (_lightweightWindowAnimationActive)
        {
            ApplyLightweightWindowAnimationFrame(_workspaceAnimationWindow, progress);
        }
    }

    private void CompleteWorkspaceTransition()
    {
        if (_workspaceLayoutPassDepth > 0)
        {
            _workspaceDeferredLayoutWrites++;
            QueueWorkspaceSettlementForTreeChange(this, force: true);
            return;
        }
        if (_settlingWorkspace) return;
        _settlingWorkspace = true;
        try
        {
            ApplyWorkspaceSurfaceWidths(1);
            if (_workspacePreviewAnimationActive)
                SetLightweightPreviewMaximumWidth(_workspacePreviewTargetWidth);
            _workspacePreviewAnimationActive = false;
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
        if (e.WidthChanged && !_viewModel.IsLightweightApplicationMode &&
            !_applyingWorkspaceWindowBounds && !_settlingWorkspace && !_isFullScreen)
            _workspacePinnedLeftWidth = _workspacePinnedRightWidth = 0;
        if (!_applyingWorkspaceWindowBounds && !_settlingWorkspace)
            SettleWorkspaceForEnvironmentChange();
    }

    private void OnWorkspaceWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            SettleWorkspaceForEnvironmentChange();
        }
        else
        {
            ResumeWorkspaceWindowFit();
        }
    }

    private void QueueWorkspaceSettlementForTreeChange(FrameworkElement element, bool force = false)
    {
        if (_settlingWorkspace || (!force && _workspaceTransition?.IsRunning != true && !_workspaceSurfaceAnimationActive && !_workspaceTreeSettlementQueued)) return;
        if (element.Visibility != Visibility.Visible) _workspaceExternalVisibility[element] = element.Visibility;
        // Unload/IsVisibleChanged can run inside UIElementCollection.Remove.
        // Stop the clock immediately, but do not resize the HWND or complete
        // Grid geometry until the parent has finished changing its children.
        _workspaceTransition?.Stop();
        _lightweightWindowAnimationActive = false;
        if (_workspaceTreeSettlementQueued) return;
        _workspaceTreeSettlementQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _workspaceTreeSettlementQueued = false;
            var preserve = _workspaceExternalVisibility.Where(pair => pair.Key.Visibility == pair.Value).ToArray();
            _workspaceExternalVisibility.Clear();
            if (_workspaceTransition?.IsRunning == true || _shutdownStarted || !IsLoaded) return;
            CompleteWorkspaceTransition();
            _settlingWorkspace = true;
            try { foreach (var (item, visibility) in preserve) item.Visibility = visibility; }
            finally { _settlingWorkspace = false; }
        });
    }

    private void OnWorkspaceUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) QueueWorkspaceSettlementForTreeChange(element);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        SettleWorkspaceForEnvironmentChange();
        base.OnDpiChanged(oldDpi, newDpi);
    }
}
