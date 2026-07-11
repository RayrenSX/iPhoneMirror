using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App;

// Build marker: GUI hosts the native D3D11 swapchain; decoded presentation
// frames no longer pass through WPF WriteableBitmap or CompositionTarget.
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _logTimer;
    private readonly PreviewWindowManager _previewWindows = new();
    private readonly SecondaryMirrorProcessManager _secondaryMirrors = new();
    private readonly SemaphoreSlim _screenshotGate = new(1, 1);
    private bool _isFullScreen;
    private WindowStyle _restoreWindowStyle;
    private WindowState _restoreWindowState;
    private bool _shutdownStarted;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => _ = _viewModel.RefreshAsync();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _logTimer.Tick += (_, _) => _ = _viewModel.RefreshLogsAsync();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Let WPF render the window before Apple/usbmux enumeration runs. A
        // stalled service or USB re-enumeration must not make the GUI appear
        // frozen or prevent the user from seeing the current status.
        _refreshTimer.Start();
        _ = _viewModel.RefreshAsync();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _refreshTimer.Stop();
        _logTimer.Stop();
        _previewWindows.Dispose();
        _secondaryMirrors.Dispose();
        try
        {
            await _viewModel.ShutdownAsync();
        }
        catch (Exception error)
        {
            // Window shutdown must complete even if a broken USB stack reports
            // an error after the explicit stop/dispose attempts have run.
            Debug.WriteLine($"iPhoneMirror shutdown cleanup failed: {error}");
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void OnRefreshPreviewClick(object sender, RoutedEventArgs e) => RefreshPreview();

    private void OnMirrorSimultaneouslyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu ||
            menu.PlacementTarget is not FrameworkElement { DataContext: Models.DeviceViewModel device }) return;
        if (Models.DeviceViewModel.UdidEquals(device.Udid, _viewModel.SelectedDevice?.Udid) &&
            _viewModel.HasCaptureSession)
        {
            _viewModel.AddUiLog(LocalizationService.Get("DeviceAlreadyMirroring"));
            return;
        }
        var result = _secondaryMirrors.Show(device);
        _viewModel.AddUiLog(result.Success
            ? LocalizationService.Format("SimultaneousMirrorStartedFormat", device.DisplayName)
            : LocalizationService.Format("SimultaneousMirrorFailedFormat", result.Message));
    }

    private void OnDeviceListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        if (current is not ListBoxItem item || item.ContextMenu is null) return;

        // WPF selects a ListBoxItem on right-click before opening its menu.
        // That would stop the current phone as a normal device switch. Open
        // the item's menu ourselves and leave the active selection untouched.
        e.Handled = true;
        item.ContextMenu.PlacementTarget = item;
        item.ContextMenu.IsOpen = true;
    }

    private void RefreshPreview()
    {
        var refreshed = _previewWindows.IsOpen ? _previewWindows.Refresh() : MainPreviewHost.ForceRefresh();
        _viewModel.AddUiLog(LocalizationService.Get(
            refreshed ? "PreviewRefreshed" : "PreviewRefreshFailed"));
    }

    private void OnPreviewWindowClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _previewWindows.UpdateSourceSize(
                _viewModel.SourceVideoWidth, _viewModel.SourceVideoHeight);
            _previewWindows.Show();
            _viewModel.AddUiLog(LocalizationService.Get("PreviewWindowOpened"));
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog(LocalizationService.Format("PreviewWindowOpenFailedFormat", error.Message));
        }
    }

    private void OnObsWindowClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _previewWindows.UpdateSourceSize(
                _viewModel.SourceVideoWidth, _viewModel.SourceVideoHeight);
            _previewWindows.Show();
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog(LocalizationService.Format("ObsWindowOpenFailedFormat", error.Message));
            return;
        }
        try
        {
            Clipboard.SetText("iPhoneMirror OBS Preview");
            _viewModel.AddUiLog(LocalizationService.Get("ObsWindowOpened"));
        }
        catch (ExternalException error)
        {
            // Another process can temporarily hold the Win32 clipboard. The
            // preview is already usable, so never terminate the application.
            _viewModel.AddUiLog(LocalizationService.Format("ObsTitleCopyFailedFormat", error.Message));
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Width is raised before height as one atomic status update. Listening
        // to the final height notification avoids resizing twice per frame-
        // format/orientation change.
        if (e.PropertyName is nameof(MainViewModel.SourceVideoHeight) or
            nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.SelectedModel))
        {
            _previewWindows.UpdateSourceDevice(
                _viewModel.SelectedDevice?.ProductType,
                _viewModel.SourceVideoWidth,
                _viewModel.SourceVideoHeight);
        }
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleActiveFullScreen();

    private void ToggleActiveFullScreen()
    {
        try
        {
            if (_previewWindows.IsOpen)
                _previewWindows.ToggleFullScreen();
            else
                ToggleFullScreen();
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog(LocalizationService.Format("FullScreenFailedFormat", error.Message));
        }
    }

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            WindowStyle = _restoreWindowStyle;
            WindowState = _restoreWindowState == WindowState.Minimized ? WindowState.Normal : _restoreWindowState;
            RootLayout.Margin = new Thickness(24);
            HeaderGapRow.Height = new GridLength(18);
            EnvironmentGapRow.Height = new GridLength(14);
            StatsGapRow.Height = new GridLength(14);
            LogGapRow.Height = new GridLength(10);
            PreviewPanel.BorderThickness = new Thickness(1);
            PreviewPanel.CornerRadius = new CornerRadius(8);
            HeaderPanel.Visibility = Visibility.Visible;
            DevicePanel.Visibility = Visibility.Visible;
            ControlPanel.Visibility = Visibility.Visible;
            EnvironmentPanel.Visibility = Visibility.Visible;
            StatsPanel.Visibility = Visibility.Visible;
            LogExpander.Visibility = Visibility.Visible;
            FooterPanel.Visibility = Visibility.Visible;
            DeviceColumn.Width = new GridLength(300);
            LeftGapColumn.Width = new GridLength(18);
            RightGapColumn.Width = new GridLength(18);
            ControlColumn.Width = new GridLength(336);
            _isFullScreen = false;
        }
        else
        {
            _restoreWindowStyle = WindowStyle;
            _restoreWindowState = WindowState;
            HeaderPanel.Visibility = Visibility.Collapsed;
            DevicePanel.Visibility = Visibility.Collapsed;
            ControlPanel.Visibility = Visibility.Collapsed;
            EnvironmentPanel.Visibility = Visibility.Collapsed;
            StatsPanel.Visibility = Visibility.Collapsed;
            LogExpander.IsExpanded = false;
            LogExpander.Visibility = Visibility.Collapsed;
            FooterPanel.Visibility = Visibility.Collapsed;
            DeviceColumn.Width = new GridLength(0);
            LeftGapColumn.Width = new GridLength(0);
            RightGapColumn.Width = new GridLength(0);
            ControlColumn.Width = new GridLength(0);
            RootLayout.Margin = new Thickness(0);
            HeaderGapRow.Height = new GridLength(0);
            EnvironmentGapRow.Height = new GridLength(0);
            StatsGapRow.Height = new GridLength(0);
            LogGapRow.Height = new GridLength(0);
            PreviewPanel.BorderThickness = new Thickness(0);
            PreviewPanel.CornerRadius = new CornerRadius(0);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullScreen = true;
        }
        MainPreviewHost.Activate();
    }

    private void OnScreenshotClick(object sender, RoutedEventArgs e) => _ = CaptureScreenshotAsync();

    private async Task CaptureScreenshotAsync()
    {
        if (!await _screenshotGate.WaitAsync(0))
        {
            _viewModel.AddUiLog(LocalizationService.Get("ScreenshotBusy"));
            return;
        }
        try
        {
            var path = ScreenshotService.CreateDefaultPath();
            var saved = await Task.Run(() => _viewModel.CaptureScreenshot(path));
            _viewModel.AddUiLog(LocalizationService.Format("ScreenshotSavedFormat", saved));
        }
        catch (Exception error)
        {
            _viewModel.AddUiLog(LocalizationService.Format("ScreenshotFailedFormat", error.Message));
        }
        finally
        {
            _screenshotGate.Release();
        }
    }

    private void OnLogExpanded(object sender, RoutedEventArgs e)
    {
        _logTimer.Start();
        _ = _viewModel.RefreshLogsAsync();
    }

    private void OnLogCollapsed(object sender, RoutedEventArgs e) => _logTimer.Stop();

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (LogExpander.IsExpanded) LogTextBox.ScrollToEnd();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.F11) ToggleActiveFullScreen();
        else if (e.Key == Key.Escape && _isFullScreen) ToggleFullScreen();
        else if (e.Key == Key.F5) _ = _viewModel.RefreshAsync(forceDeviceEnumeration: true);
        else if (ctrl && e.Key == Key.R) RefreshPreview();
        else if (ctrl && shift && e.Key == Key.P) OnPreviewWindowClick(this, new RoutedEventArgs());
        else if (ctrl && e.Key == Key.L) LogExpander.IsExpanded = !LogExpander.IsExpanded;
        else if (ctrl && e.Key == Key.M) _viewModel.PlayAudio = !_viewModel.PlayAudio;
        else if (ctrl && e.Key == Key.S) _ = CaptureScreenshotAsync();
        else return;
        e.Handled = true;
    }
}
