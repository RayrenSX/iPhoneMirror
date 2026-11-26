using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public partial class TrayPanelWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly MainViewModel _viewModel;
    private readonly Func<DeviceViewModel, Task> _start;
    private readonly Action _settings;
    private readonly Action _exit;
    private bool _busy;
    private bool _closingPermanently;

    internal TrayPanelWindow(MainViewModel viewModel, Func<DeviceViewModel, Task> start,
        Action settings, Action exit)
    {
        _viewModel = viewModel;
        _start = start;
        _settings = settings;
        _exit = exit;
        InitializeComponent();
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
            FeedbackText.Text = LocalizationService.RefreshText(FeedbackText.Text));
        DataContext = viewModel;
        DeviceList.ItemsSource = viewModel.DeviceCards;
        viewModel.PropertyChanged += OnViewModelChanged;
        Deactivated += (_, _) => QueueDismissIfInactive();
        ModeSelector.DropDownClosed += (_, _) => QueueDismissIfInactive();
        Closing += (_, args) =>
        {
            if (_closingPermanently) return;
            args.Cancel = true;
            Hide();
        };
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelChanged;
            DeviceList.ItemsSource = null;
        };
        UpdateActions();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args) => UpdateActions();

    private void QueueDismissIfInactive() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (!_closingPermanently && !IsActive && !ModeSelector.IsDropDownOpen) Hide();
    });

    internal void ClosePermanently()
    {
        _closingPermanently = true;
        Close();
    }

    private void UpdateActions()
    {
        StartButton.IsEnabled = !_busy && _viewModel.SelectedDevice is not null;
        DeviceList.IsEnabled = ModeSelector.IsEnabled = RefreshButton.IsEnabled = !_busy;
    }

    internal void ReportError(string message)
    {
        FeedbackText.Text = message;
        FeedbackText.Visibility = Visibility.Visible;
    }

    internal void SetPreviewInteractionRestoreVisible(bool visible) =>
        RestorePreviewInteractionButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    internal event Action? PreviewInteractionRestoreRequested;

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy || DeviceList.SelectedItem is not DeviceViewModel device) return;
        _busy = true;
        UpdateActions();
        FeedbackText.Text = LocalizationService.Get("TrayStarting");
        FeedbackText.Visibility = Visibility.Visible;
        try
        {
            await _start(device);
            if (_closingPermanently) return;
            FeedbackText.Visibility = Visibility.Collapsed;
            Hide();
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("tray", "start_failed", error);
            ReportError(LocalizationService.ErrorMessage(error, "TrayStartFailed"));
        }
        finally { _busy = false; UpdateActions(); }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        UpdateActions();
        FeedbackText.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "UiRefreshing");
        FeedbackText.Visibility = Visibility.Visible;
        try
        {
            await _viewModel.RefreshAsync(forceDeviceEnumeration: true);
            if (!_closingPermanently) FeedbackText.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("tray", "refresh_failed", error);
            ReportError(LocalizationService.ErrorMessage(error, "TrayRefreshFailed"));
        }
        finally { _busy = false; UpdateActions(); }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) { Hide(); _settings(); }
    private void OnRestoreClick(object sender, RoutedEventArgs e) => _viewModel.RestoreMainWindowFromTray();
    private void OnRestorePreviewInteractionClick(object sender, RoutedEventArgs e)
    {
        Hide();
        PreviewInteractionRestoreRequested?.Invoke();
    }
    private void OnExitClick(object sender, RoutedEventArgs e) => _exit();
    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    private void OnModeSelectorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Scrolling the panel must never switch application modes inadvertently.
        if (!ModeSelector.IsDropDownOpen) e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ModeSelector.IsDropDownOpen) return;
        e.Handled = true;
        Hide();
    }
}
