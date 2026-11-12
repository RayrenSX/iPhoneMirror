using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    // Leave room for window controls; the video-derived limit below takes
    // precedence whenever preserving the current picture needs more space.
    private const double CompleteMinimumWindowWidth = 480;
    private bool _workspaceLayoutQueued;
    private double _workspacePinnedLeftWidth, _workspacePinnedRightWidth;
    private bool _preparingWorkspaceReveal;
    private (double Width, double Height, double Scale, double Aspect)? _horizontalProjectionResize;
    private double _completeResizePreviewWidth;
    private double WorkspaceReadingScale => TryFindResource("BodyFontSize") is double size ? size / 14 : 1;
    private double WorkspaceDeviceWidth => 300 * (1 + .5 * (WorkspaceReadingScale - 1));
    private double WorkspaceSettingsWidth => 362 * (1 + .5 * (WorkspaceReadingScale - 1));

    private void OnIdleSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WaitingDeviceIllustration is null || ReadyDeviceIllustration is null) return;
        // Prioritize the status and instructions when there is no room for the illustration.
        var compact = e.NewSize.Height < 220;
        WaitingDeviceIllustration.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ReadyDeviceIllustration.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        WaitingDeviceTitle.Margin = ReadyDeviceTitle.Margin = new Thickness(0, compact ? 0 : 20, 0, 0);
    }

    private void OnStatisticsSizeChanged(object sender, SizeChangedEventArgs e) => OnWorkspaceLayoutChanged(sender, e);

    private void UpdateStatisticsLayout()
    {
        if (StatisticsViewport is null || StatisticsItems is null || MainContentGrid is null) return;
        var width = StatisticsViewport.ActualWidth;
        if (width <= 0) return;
        // Keep all four metrics in one row. Detailed groups share the available
        // width; at very narrow sizes the existing horizontal scroll preserves
        // their readable minimum instead of moving audio onto a second row.
        StatsPanel.MaxHeight = Math.Max(84 * WorkspaceReadingScale, MainContentGrid.ActualHeight * .30);
        UpdateWorkspaceBarMargins();
        var items = StatisticsItems.Children.OfType<StackPanel>().ToArray();
        foreach (var item in items)
            item.Width = _viewModel.HideInterfaceDescriptions ? double.NaN :
                Math.Max(125 * WorkspaceReadingScale, width / Math.Max(1, items.Length) - item.Margin.Left - item.Margin.Right);
    }

    private void UpdateWorkspaceBarMargins()
    {
        var gap = MainContentGrid.ActualHeight < 360 ? 4 : 14;
        EnvironmentPanel.Margin = new Thickness(0, 0, 0, gap);
        StatsPanel.Margin = new Thickness(0, gap, 0, 0);
    }

    private void OnWorkspacePanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && !element.IsVisible && !_settlingWorkspace)
            QueueWorkspaceSettlementForTreeChange(element);
    }

    // Keep the two independently scrollable sidebars in their horizontal cells.
    private void OnWorkspaceLayoutChanged(object sender, RoutedEventArgs e)
    {
        if (_workspaceLayoutQueued || _shutdownStarted) return;
        _workspaceLayoutQueued = true;
        // WM_SIZE can reenter WPF measurement during a native width animation.
        // Defer responsive constraints until the current layout pass has finished.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _workspaceLayoutQueued = false;
            if (!_shutdownStarted && IsLoaded)
            {
                UpdateWorkspaceLayout();
                TrimLightweightWorkspaceWidth();
            }
        });
    }

    private void UpdateWorkspaceLayout(bool preserveSurfaceWidths = false)
    {
        if (_workspaceLayoutPassDepth > 0)
        {
            _workspaceDeferredLayoutWrites++;
            OnWorkspaceLayoutChanged(this, new RoutedEventArgs());
            return;
        }
        if (!IsLoaded || MainContentGrid is null || ControlPanel is null) return;
        if (_isFullScreen) return;
        if (!preserveSurfaceWidths && _workspaceTransition?.IsRunning != true)
        {
            if (LeftPanelHost.Visibility == Visibility.Visible) LeftPanelHost.Width = WorkspaceDeviceWidth;
            if (ControlPanel.Visibility == Visibility.Visible) ControlPanel.Width = WorkspaceSettingsWidth;
        }
        UpdateStatisticsLayout();
        // Keep the original single-row toolbar. AdaptiveToolbar compacts labels
        // when necessary and horizontal scrolling handles the remaining overflow.
        PreviewToolbarScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        PreviewToolbarScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        PreviewToolbarScrollViewer.MaxHeight = double.PositiveInfinity;
        UpdateWorkspaceBarMargins();
        // Keep the same single-row header in complete and lightweight modes.
        // Width changes only compact secondary status, never move the action
        // into a new row or change the subtitle's user-selected visibility.
        var readingFactor = 1 + .35 * (WorkspaceReadingScale - 1);
        var compactStatus = _viewModel.IsLightweightApplicationMode || RootLayout.ActualWidth < 1100 * readingFactor;
        DetectionStatus.MaxWidth = compactStatus ? 0 : 360;
        DriverStatusGap.Width = new GridLength(compactStatus || _viewModel.HideInterfaceDescriptions ? 0 : 18);
        VersionBadge.Visibility = RootLayout.ActualWidth < 580 * readingFactor ? Visibility.Collapsed : Visibility.Visible;
        RefreshLightweightPreviewViewport();
        UpdateWorkspaceViewportConstraints();
    }

    private void RefreshLightweightPreviewViewport()
    {
        if (!_viewModel.IsLightweightApplicationMode || _isFullScreen ||
            _preparingWorkspaceReveal || _workspaceSurfaceAnimationActive ||
            _workspaceTransition?.IsRunning == true) return;
        // Startup may pin the center to the first source size. User resizing
        // must instead consume the current available space. Recompute the
        // source-aspect cap from the laid-out height without resizing the HWND.
        SetLightweightCenterColumnFill();
        ApplyLightweightPreviewFramePolicy();
    }

    private bool CanTrimLightweightWorkspace => IsLoaded && IsVisible &&
        _viewModel.IsLightweightApplicationMode && !_isFullScreen && !_isWindowMaximized &&
        WindowState == WindowState.Normal && HasLightweightVideoPresentation && HasLightweightContentDimensions &&
        !_preparingWorkspaceReveal && !_workspaceSurfaceAnimationActive && _workspaceTransition?.IsRunning != true;

    private double GetLightweightSnugWidth(double previewHeight)
        => CalculateLightweightSnugWidth(previewHeight, GetLightweightCurrentWindowWidth());

    private double CalculateLightweightSnugWidth(double previewHeight, double currentWidth)
    {
        var fixedWidth = GetLightweightFixedWorkspaceWidth(currentWidth);
        var picture = Math.Round(Math.Max(1, previewHeight) * _viewModel.SourceVideoWidth / _viewModel.SourceVideoHeight);
        var maximum = GetLightweightMaximumWindowWidth();
        return Math.Clamp(fixedWidth + picture, Math.Min(LightweightMinimumWindowWidth, maximum), maximum);
    }

    private double GetLightweightFixedWorkspaceWidth(double currentWidth) =>
        GetLightweightFixedChromeWidth(currentWidth) +
        (_leftWorkspacePanel != LeftWorkspacePanel.None ? WorkspaceDeviceWidth + 18 : 0) +
        (_isSettingsPanelVisible ? WorkspaceSettingsWidth + 18 : 0);

    private bool CanConstrainCompletePreview => IsLoaded && IsVisible &&
        !_viewModel.IsLightweightApplicationMode && !_lightweightModeApplied && !_isFullScreen && !_isWindowMaximized &&
        WindowState == WindowState.Normal && HasLightweightVideoPresentation && HasLightweightContentDimensions &&
        PreviewPanel.IsVisible && PreviewPanel.ActualWidth > 0 && PreviewPanel.ActualHeight > 0 &&
        !_applyingWorkspaceWindowBounds && !_settlingWorkspace && _workspaceTransition?.IsRunning != true;

    private double GetCompletePreviewMinimumWindowWidth(NativeRect current, int proposedHeight, double scale)
    {
        var border = PreviewPanel.BorderThickness;
        var padding = PreviewPanel.Padding;
        var horizontalFrame = border.Left + border.Right + padding.Left + padding.Right;
        var verticalFrame = border.Top + border.Bottom + padding.Top + padding.Bottom;
        var heightChange = (proposedHeight - (current.Bottom - current.Top)) / scale;
        var pictureHeight = Math.Max(0, PreviewPanel.ActualHeight - verticalFrame + heightChange);
        var pictureWidth = Math.Min(Math.Max(0, PreviewPanel.ActualWidth - horizontalFrame),
            pictureHeight * _viewModel.SourceVideoWidth / _viewModel.SourceVideoHeight);
        // Preserve the displayed picture, including a landscape picture that
        // is already width-limited. Only its surrounding black space can go.
        _completeResizePreviewWidth = horizontalFrame + pictureWidth;
        var chrome = (current.Right - current.Left) / scale - PreviewPanel.ActualWidth;
        var maximum = Math.Min(MaxWidth,
            IPhoneMirror.SharedUI.Services.WindowWorkAreaController.GetAvailableSize(this).Width);
        return Math.Min(maximum, Math.Max(CompleteMinimumWindowWidth, chrome + _completeResizePreviewWidth));
    }

    private void ScaleLightweightProjectionFromVerticalEdge(ref NativeRect proposed,
        NativeRect current, double scale, bool fromTop)
    {
        var currentHeight = (current.Bottom - current.Top) / scale;
        var frameHeight = Math.Max(0, currentHeight - PreviewPanel.ActualHeight);
        var fixedWidth = GetLightweightFixedWorkspaceWidth((current.Right - current.Left) / scale);
        var aspect = (double)_viewModel.SourceVideoWidth / _viewModel.SourceVideoHeight;
        var maximumWidth = MaxWidth;
        var maximumHeight = MaxHeight;
        if (TryGetLightweightWorkArea(out var workArea, out _))
        {
            maximumWidth = Math.Min(maximumWidth,
                Math.Max(1, (workArea.Right - workArea.Left) / scale - LightweightWorkAreaInset));
            var availableHeight = fromTop ? proposed.Bottom - workArea.Top : workArea.Bottom - proposed.Top;
            maximumHeight = Math.Min(maximumHeight, Math.Max(currentHeight, availableHeight / scale));
        }
        // Stop scaling when the picture runs out of space on either axis.
        maximumHeight = Math.Min(maximumHeight, frameHeight + Math.Max(1, maximumWidth - fixedWidth) / aspect);
        maximumHeight = Math.Max(MinHeight, maximumHeight);
        var requestedHeight = (proposed.Bottom - proposed.Top) / scale;
        var height = Math.Clamp(requestedHeight, MinHeight, maximumHeight);
        var heightPixels = Math.Max(1, (int)Math.Round(height * scale));
        var pictureWidth = Math.Round(Math.Max(1, heightPixels / scale - frameHeight) * aspect);
        var width = Math.Clamp(fixedWidth + pictureWidth, Math.Min(LightweightMinimumWindowWidth, maximumWidth), maximumWidth);
        proposed.Right = proposed.Left + Math.Max(1, (int)Math.Round(width * scale));
        if (fromTop) proposed.Top = proposed.Bottom - heightPixels;
        else proposed.Bottom = proposed.Top + heightPixels;
    }

    private void ScaleLightweightProjectionFromSide(ref NativeRect proposed, NativeRect current,
        double scale, bool fromLeft)
    {
        var currentWidth = (current.Right - current.Left) / scale;
        var currentHeight = (current.Bottom - current.Top) / scale;
        var requestedWidth = (proposed.Right - proposed.Left) / scale;
        var aspect = (double)_viewModel.SourceVideoWidth / _viewModel.SourceVideoHeight;
        if (_horizontalProjectionResize is not { } start || start.Scale != scale || start.Aspect != aspect)
        {
            start = (currentWidth, currentHeight, scale, aspect);
            _horizontalProjectionResize = start;
        }

        // Use the initial rectangle throughout the gesture. Incremental
        // scaling from the last rounded frame stalls at the minimum width
        // and can drift as the pointer reverses direction.
        var top = proposed.Top;
        var maximumHeight = MaxHeight;
        var minimumHeight = MinHeight;
        var hasWorkArea = TryGetLightweightWorkArea(out var workArea, out _);
        if (hasWorkArea)
            maximumHeight = Math.Min(maximumHeight, (workArea.Bottom - workArea.Top) / scale);
        var height = Math.Clamp(start.Height + (requestedWidth - start.Width) / aspect,
            Math.Min(minimumHeight, maximumHeight), maximumHeight);
        var heightPixels = Math.Max(1, (int)Math.Round(height * scale));
        if (hasWorkArea && top + heightPixels > workArea.Bottom)
            top = Math.Max(workArea.Top, workArea.Bottom - heightPixels);
        var previewHeight = PreviewPanel.ActualHeight + heightPixels / scale - currentHeight;
        var snugWidth = CalculateLightweightSnugWidth(previewHeight, currentWidth);
        var widthPixels = Math.Max(1, (int)Math.Round(Math.Min(requestedWidth, snugWidth) * scale));
        widthPixels = Math.Max((int)Math.Round(Math.Min(LightweightMinimumWindowWidth, snugWidth) * scale), widthPixels);
        proposed.Top = top;
        proposed.Bottom = top + heightPixels;
        if (fromLeft) proposed.Left = proposed.Right - widthPixels;
        else proposed.Right = proposed.Left + widthPixels;
    }

    private void TrimLightweightWorkspaceWidth()
    {
        if (!CanTrimLightweightWorkspace || PreviewPanel.ActualHeight <= 0) return;
        var target = GetLightweightSnugWidth(PreviewPanel.ActualHeight);
        // Old source-height minimums must not keep surplus width after a height
        // reduction. Correct directly, without starting another width animation.
        if (MinWidth > target) MinWidth = target;
        if (GetLightweightCurrentWindowWidth() > target + 1)
            SetLightweightWindowWidthImmediately(target);
    }

    private void UpdateWorkspaceViewportConstraints()
    {
        if (_workspaceLayoutPassDepth > 0)
        {
            _workspaceDeferredLayoutWrites++;
            OnWorkspaceLayoutChanged(this, new RoutedEventArgs());
            return;
        }
        // The workspace stays in one row at every width. Only the viewport
        // allocation changes; no card is reparented across the preview.
        var showLeft = _leftWorkspacePanel != LeftWorkspacePanel.None ||
            (LeftPanelHost.Visibility == Visibility.Visible && GetLightweightElementWidth(LeftPanelHost) > 0);
        var showSettings = _isSettingsPanelVisible ||
            (ControlPanel.Visibility == Visibility.Visible && GetLightweightElementWidth(ControlPanel) > 0);
        var animating = _preparingWorkspaceReveal || _workspaceSurfaceAnimationActive || _workspaceTransition?.IsRunning == true;
        var leftRequest = showLeft ? (animating ? Math.Max(0, LeftPanelHost.Width) : WorkspaceDeviceWidth) : 0;
        var rightRequest = showSettings ? (animating ? Math.Max(0, ControlPanel.Width) : WorkspaceSettingsWidth) : 0;
        var gaps = animating
            ? GetLightweightColumnWidth(LeftGapColumn) + GetLightweightColumnWidth(RightGapColumn)
            : (showLeft ? 18 : 0) + (showSettings ? 18 : 0);
        var centerGroup = _viewModel.IsLightweightApplicationMode && !_isFullScreen &&
            (_isWindowMaximized || WindowState == WindowState.Maximized);
        if (centerGroup)
        {
            var picture = double.IsFinite(PreviewPanel.MaxWidth) ? PreviewPanel.MaxWidth : RootLayout.ActualWidth;
            MainContentGrid.MaxWidth = Math.Max(1, leftRequest + gaps + picture + rightRequest);
            // A centered Grid shrink-wraps its desired size. A star column's
            // maximum alone does not reserve the picture width, especially
            // when the idle message or native host has a small desired width.
            MainContentGrid.Width = Math.Max(1, Math.Min(RootLayout.ActualWidth, MainContentGrid.MaxWidth));
            MainContentGrid.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            MainContentGrid.ClearValue(WidthProperty);
            MainContentGrid.MaxWidth = double.PositiveInfinity;
            MainContentGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        var readingFactor = 1 + .35 * (WorkspaceReadingScale - 1);
        var availableWidth = Math.Max(0, (centerGroup ? Math.Min(RootLayout.ActualWidth, MainContentGrid.MaxWidth) : MainContentGrid.ActualWidth) - gaps);
        var reservePreview = Math.Max(160 * readingFactor,
            CanConstrainCompletePreview ? _completeResizePreviewWidth : 0);
        if (_viewModel.IsLightweightApplicationMode && !_isFullScreen)
            reservePreview = Math.Max(reservePreview,
                Math.Min(LightweightMinimumPreviewWidth * readingFactor, PreviewPanel.MaxWidth));
        var budget = Math.Max(0, availableWidth - reservePreview);
        double left, right;
        if (_workspacePinnedRightWidth > 0 && _workspacePinnedLeftWidth == 0)
        {
            right = Math.Min(rightRequest, Math.Min(_workspacePinnedRightWidth, budget));
            left = Math.Min(leftRequest, Math.Max(0, budget - right));
        }
        else
        {
            if (!_viewModel.IsLightweightApplicationMode && _workspacePinnedLeftWidth == 0 &&
                leftRequest > 0 && rightRequest > 0 && leftRequest + rightRequest > budget)
            {
                // A user resize shares the available sidebar budget instead of
                // letting the left card consume all of the settings viewport.
                left = budget * leftRequest / (leftRequest + rightRequest);
                right = budget - left;
            }
            else
            {
                left = Math.Min(leftRequest, Math.Min(_workspacePinnedLeftWidth > 0 ? _workspacePinnedLeftWidth : leftRequest, budget));
                right = Math.Min(rightRequest, Math.Max(0, budget - left));
            }
        }
        left = Math.Max(1, left); right = Math.Max(1, right);
        if (centerGroup && double.IsFinite(PreviewPanel.MaxWidth))
        {
            // This group already has an explicit width equal to its cards and
            // capped picture. Reserve that picture directly: a capped star
            // column can leave WPF's rounding solver with a negative definition
            // index when auto sidebars exchange their remaining space.
            var pictureSlot = Math.Max(0, availableWidth -
                (showLeft ? left : 0) - (showSettings ? right : 0));
            CenterColumn.Width = new GridLength(Math.Min(PreviewPanel.MaxWidth, pictureSlot));
        }
        DeviceColumn.MaxWidth = showLeft ? left : double.PositiveInfinity;
        ControlColumn.MaxWidth = showSettings ? right : double.PositiveInfinity;
        // Width is the animation request, but the viewport itself must never
        // exceed its cell. WPF otherwise centers an oversized fixed-width
        // element in the constrained cell, revealing it from the middle.
        LeftPanelHost.MaxWidth = showLeft ? left : double.PositiveInfinity;
        ControlPanel.MaxWidth = showSettings ? right : double.PositiveInfinity;
        if (!animating)
        {
            LeftGapColumn.Width = new GridLength(showLeft ? 18 : 0);
            RightGapColumn.Width = new GridLength(showSettings ? 18 : 0);
        }
        if (!animating)
        {
            // Keep content measure stable during the reveal. At rest it wraps
            // within the allocated sidebar instead of clipping its controls.
            LeftPanelHost.ContentWidth = showLeft ? left : WorkspaceDeviceWidth;
            ControlPanel.ContentWidth = showSettings ? right : WorkspaceSettingsWidth;
        }
    }
}
