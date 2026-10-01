using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private bool _workspacePanelsStacked;

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
        StatisticsItems.Columns = shortArea || width >= 480 ? 4 : 2;
        StatisticsItems.Width = shortArea ? Math.Max(560, width) : width;
    }

    private void OnWorkspacePanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        OnWorkspaceLayoutChanged(sender, new RoutedEventArgs());

    // At small work-area widths, keep a usable preview and stack the two
    // independently scrollable side panels. Native preview stays in its own cell.
    private void OnWorkspaceLayoutChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || MainContentGrid is null || ControlPanel is null) return;
        UpdateStatisticsLayout();
        var stack = !_viewModel.IsLightweightApplicationMode &&
            MainContentGrid.ActualWidth < 960 && LeftPanelHost.IsVisible && ControlPanel.IsVisible &&
            LeftPanelHost.ActualWidth > 1 && ControlPanel.ActualWidth > 1;
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
