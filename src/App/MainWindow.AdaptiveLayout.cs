using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private bool _workspacePanelsStacked;
    private bool _retainWorkspaceStackDuringTransition;
    private bool? _workspaceCompactHeader, _workspaceShortHeader;

    private void OnIdleSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WaitingDeviceIllustration is null || ReadyDeviceIllustration is null) return;
        // Prioritize the status and instructions when there is no room for the illustration.
        var compact = e.NewSize.Height < 220;
        WaitingDeviceIllustration.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ReadyDeviceIllustration.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        WaitingDeviceTitle.Margin = ReadyDeviceTitle.Margin = new Thickness(0, compact ? 0 : 20, 0, 0);
    }

    private void OnStatisticsSizeChanged(object sender, SizeChangedEventArgs e) => UpdateStatisticsLayout();

    private void UpdateStatisticsLayout()
    {
        if (StatisticsViewport is null || StatisticsItems is null || MainContentGrid is null) return;
        var width = StatisticsViewport.ActualWidth;
        if (width <= 0) return;
        // A short work area keeps the stats in one horizontally scrollable row,
        // leaving height for video. Taller narrow windows use two rows instead.
        var shortArea = MainContentGrid.ActualHeight < 360;
        StatsPanel.MaxHeight = shortArea ? 40 : Math.Max(120, MainContentGrid.ActualHeight * 0.35);
        StatisticsItems.Columns = shortArea || width >= 480 ? 4 : 2;
        StatisticsItems.Width = shortArea ? Math.Max(560, width) : width;
    }

    private void OnWorkspacePanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // A host can be hidden by full screen, tray mode or removal from the tree.
        // Preserve an external Visibility value while settling its animation.
        if (sender is FrameworkElement element && !element.IsVisible && !_settlingWorkspace)
        {
            var visibility = element.Visibility;
            SettleWorkspaceForEnvironmentChange();
            element.Visibility = visibility;
        }
    }

    // At small work-area widths, keep a usable preview and stack the two
    // independently scrollable side panels. Native preview stays in its own cell.
    private void OnWorkspaceLayoutChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || MainContentGrid is null || ControlPanel is null) return;
        UpdateStatisticsLayout();
        var compactHeader = !_viewModel.IsLightweightApplicationMode && RootLayout.ActualWidth < 1100;
        // The shell already exposes the app title. In a very short work area,
        // reserve the in-window heading's space for the preview and controls.
        var shortHeader = compactHeader && RootLayout.ActualHeight < 480;
        if (compactHeader != _workspaceCompactHeader || shortHeader != _workspaceShortHeader)
        {
            _workspaceCompactHeader = compactHeader;
            _workspaceShortHeader = shortHeader;
            WorkspaceHeading.Visibility = shortHeader ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(WorkspaceHeading, compactHeader ? 5 : 1);
            WorkspaceSubtitle.Visibility = compactHeader ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetRow(DetectionStatus, compactHeader && !shortHeader ? 1 : 0);
            Grid.SetColumn(DetectionStatus, compactHeader ? 0 : 1);
            Grid.SetRow(CaptureActionButton, compactHeader && !shortHeader ? 1 : 0);
            Grid.SetColumn(CaptureActionButton, compactHeader ? 1 : 3);
            CaptureActionButton.Margin = compactHeader && !shortHeader ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        }
        var stack = !_viewModel.IsLightweightApplicationMode &&
            MainContentGrid.ActualWidth < 960 &&
            ((_leftWorkspacePanel != LeftWorkspacePanel.None && _isSettingsPanelVisible) ||
             _retainWorkspaceStackDuringTransition);
        if (stack == _workspacePanelsStacked) return;
        _workspacePanelsStacked = stack;
        MainContentGrid.RowDefinitions.Clear();
        if (stack)
        {
            MainContentGrid.RowDefinitions.Add(new RowDefinition());
            MainContentGrid.RowDefinitions.Add(new RowDefinition());
        }
        Grid.SetRow(ControlPanel, stack ? 1 : 0);
        Grid.SetColumn(ControlPanel, stack ? 0 : 4);
        Grid.SetRowSpan(CenterPanel, stack ? 2 : 1);
        ControlColumn.MaxWidth = stack ? 0 : double.PositiveInfinity;
        RightGapColumn.MaxWidth = stack ? 0 : double.PositiveInfinity;
        LeftPanelHost.Margin = stack ? new Thickness(0, 0, 0, 8) : new Thickness(0);
    }
}
