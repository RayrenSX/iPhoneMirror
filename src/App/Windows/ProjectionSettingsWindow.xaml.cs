using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public partial class ProjectionSettingsWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private Func<(bool Enabled, bool Available, string Description)>? _phoneFrameState;
    private Func<bool, Task<bool>>? _setPhoneFrame;
    private bool _phoneFrameBusy;
    private bool _refreshingFrameColor;
    private Func<DeviceFrameColorOption[]>? _frameColorOptions;
    private Func<string>? _selectedFrameColor;
    private Func<string, bool>? _setFrameColor;
    private Func<bool>? _frameColorAvailable;

    internal void ConfigurePhoneFrameColors(Func<DeviceFrameColorOption[]> options,
        Func<string> selected, Func<string, bool> apply, Func<bool>? available = null)
    {
        _frameColorOptions = options;
        _selectedFrameColor = selected;
        _setFrameColor = apply;
        _frameColorAvailable = available;
        RefreshPhoneFrame();
    }

    private void OnFrameColorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingFrameColor || _setFrameColor is null || FrameColorComboBox.SelectedValue is not string color) return;
        var success = _setFrameColor(color);
        RefreshPhoneFrame();
        PhoneFrameFeedback.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void ConfigurePhoneFrame(Func<(bool Enabled, bool Available, string Description)> state,
        Func<bool, Task<bool>> apply)
    {
        _phoneFrameState = state;
        _setPhoneFrame = apply;
        RefreshPhoneFrame();
        Activated += (_, _) => RefreshPhoneFrame();
    }

    internal void RefreshPhoneFrame()
    {
        if (_phoneFrameState is null) return;
        var state = _phoneFrameState();
        PhoneFrameCheckBox.IsChecked = state.Enabled;
        PhoneFrameCheckBox.IsEnabled = state.Available && !_phoneFrameBusy;
        PhoneFrameDescription.Text = state.Description;
        _refreshingFrameColor = true;
        try
        {
            var options = _frameColorOptions?.Invoke() ?? [];
            if (FrameColorComboBox.ItemsSource is not DeviceFrameColorOption[] previous || !previous.SequenceEqual(options))
                FrameColorComboBox.ItemsSource = options;
            FrameColorComboBox.SelectedValue = _selectedFrameColor?.Invoke() ?? "";
            FrameColorComboBox.IsEnabled = (_frameColorAvailable?.Invoke() ?? state.Available) && _setFrameColor is not null;
        }
        finally { _refreshingFrameColor = false; }
    }

    private async void OnPhoneFrameClick(object sender, RoutedEventArgs e)
    {
        if (_setPhoneFrame is null || _phoneFrameBusy) return;
        var enabled = PhoneFrameCheckBox.IsChecked == true;
        _phoneFrameBusy = true;
        PhoneFrameCheckBox.IsEnabled = false;
        bool success = false;
        try { success = await _setPhoneFrame(enabled); }
        catch (Exception error) { DiagnosticLogger.Exception("window", "phone_frame_setting_failed", error); }
        finally
        {
            _phoneFrameBusy = false;
            RefreshPhoneFrame();
            PhoneFrameFeedback.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private readonly Func<Task> _refresh;
    private readonly Func<Task> _fullScreen;
    private readonly Func<Task> _separateWindow;
    private readonly Func<Task> _screenshot;
    private readonly Action _mediaOutput;
    private readonly bool _previewOnly;

    internal ProjectionSettingsWindow(object dataContext,
        Func<Task> refresh, Func<Task> fullScreen,
        Func<Task> separateWindow, Func<Task> screenshot,
        Action mediaOutput, bool previewOnly = false)
    {
        InitializeComponent();
        DataContext = dataContext;
        _refresh = refresh;
        _fullScreen = fullScreen;
        _separateWindow = separateWindow;
        _screenshot = screenshot;
        _mediaOutput = mediaOutput;
        _previewOnly = previewOnly;
    }

    internal static void ShowDeveloperPreview(Window owner, object dataContext)
    {
        var window = new ProjectionSettingsWindow(dataContext,
            () => Task.CompletedTask, () => Task.CompletedTask,
            () => Task.CompletedTask, () => Task.CompletedTask,
            () => { }, previewOnly: true)
        {
            Owner = owner,
        };
        window.Show();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_refresh);

    private void OnFirstRunSetupClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        if (Application.Current?.MainWindow is MainWindow main)
        {
            Hide();
            try { if (main.ShowFirstRunSetup(rerun: true)) main.StartAfterSetup(); }
            finally { Show(); }
        }
    }

    private async void OnFullScreenClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_fullScreen);

    private async void OnSeparateWindowClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_separateWindow);

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || DataContext is not MainViewModel viewModel) return;
        if (viewModel.IsTrayApplicationMode) await RunAsync(_separateWindow);
        else if (viewModel.StartCommand.CanExecute(null)) viewModel.StartCommand.Execute(null);
    }

    private void OnApplyLightweightModeClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight;
    }

    private async void OnScreenshotClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_screenshot);

    private void OnCreateCompactShortcutClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        if (DataContext is not MainViewModel { SelectedDevice: { IsMediaCast: false } device }) return;
        try
        {
            var path = CompactLaunchOptions.CreateDesktopShortcut(device.Udid, device.DisplayName);
            AppPromptWindow.Inform(LocalizationService.Get("CompactLaunchTitle"),
                LocalizationService.Format("CompactShortcutCreated", path), this);
        }
        catch (Exception error)
        {
            AppPromptWindow.Inform(LocalizationService.Get("CompactLaunchTitle"),
                LocalizationService.Format("CompactLaunchFailed", error.Message), this);
        }
    }

    private void OnMediaOutputClick(object sender, RoutedEventArgs e) =>
        _mediaOutput();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            // The main window actions already publish localized UI and
            // diagnostic errors; the floating panel must remain usable.
            DiagnosticLogger.Exception("window", "projection_action_failed", error);
        }
    }
}
