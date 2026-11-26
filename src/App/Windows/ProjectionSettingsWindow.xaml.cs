using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
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
        FrameColorFeedback.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
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
    private readonly IndependentWindowLayoutEditor? _layoutEditor;
    private readonly NetworkProxySettingsEditor _networkProxyEditor;

    internal ProjectionSettingsWindow(object dataContext,
        Func<Task> refresh, Func<Task> fullScreen,
        Func<Task> separateWindow, Func<Task> screenshot,
        Action mediaOutput, bool previewOnly = false)
    {
        InitializeComponent();
        var visibility = new BooleanToVisibilityConverter();
        BindCategory(ProjectionSettingsScrollViewer, ProjectionSettingsTab);
        BindCategory(ControlSettingsScrollViewer, ControlSettingsTab);
        BindCategory(ApplicationSettingsScrollViewer, ApplicationSettingsTab);

        DataContext = dataContext;
        _refresh = refresh;
        _fullScreen = fullScreen;
        _separateWindow = separateWindow;
        _screenshot = screenshot;
        _mediaOutput = mediaOutput;
        _previewOnly = previewOnly;
        _networkProxyEditor = new(this);
        _networkProxyEditor.Initialize();
        if (dataContext is MainViewModel viewModel)
        {
            _layoutEditor = new(this, viewModel, udid =>
                (Application.Current?.MainWindow as MainWindow)?.GetCurrentIndependentWindowLayout(udid));
            _layoutEditor.Refresh();
            viewModel.PropertyChanged += OnSelectedDeviceChanged;
            Closed += (_, _) => viewModel.PropertyChanged -= OnSelectedDeviceChanged;
        }
        Activated += (_, _) =>
        {
            _layoutEditor?.RefreshIfChanged();
            _networkProxyEditor.RefreshIfChanged();
        };
        RefreshThemeSelection(null, EventArgs.Empty);
        ThemeService.ThemeChanged += RefreshThemeSelection;
        Closed += (_, _) => ThemeService.ThemeChanged -= RefreshThemeSelection;

        void BindCategory(ScrollViewer page, RadioButton tab) =>
            page.SetBinding(VisibilityProperty, new Binding(nameof(RadioButton.IsChecked))
            {
                Source = tab,
                Converter = visibility,
            });
    }

    private void RefreshThemeSelection(object? sender, EventArgs e) =>
        ThemeComboBox.SelectedValue = ThemeService.Preference.ToString();

    private void OnSelectedDeviceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedDevice)) _layoutEditor?.Refresh();
    }

    private void OnIndependentLayoutModeChanged(object sender, SelectionChangedEventArgs e) => _layoutEditor?.ChangeMode();
    private void OnSaveIndependentWindowLayout(object sender, RoutedEventArgs e) => _layoutEditor?.Save();
    private void OnSaveCurrentIndependentWindowLayout(object sender, RoutedEventArgs e) => _layoutEditor?.SaveCurrent();
    private void OnNetworkProxyModeChanged(object sender, SelectionChangedEventArgs e) => _networkProxyEditor?.ChangeMode();
    private void OnNetworkProxyDraftChanged(object sender, TextChangedEventArgs e) => _networkProxyEditor?.ChangeDraft();
    private void OnSaveNetworkProxyClick(object sender, RoutedEventArgs e) => _networkProxyEditor.Save();

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_previewOnly && Application.Current?.MainWindow is MainWindow main)
            main.OnThemeSelectionChanged(sender, e);
    }

    private void OnUsbProjectionModeInfoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UsbProjectionModeOption option }) return;
        e.Handled = true;
        new UsbProjectionModeInfoWindow(option) { Owner = this }.ShowDialog();
    }

    private void OnShortcutSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!_previewOnly && Application.Current?.MainWindow is MainWindow main) main.OpenShortcutSettings(this);
    }

    private void OnCustomizePreviewToolbar(object sender, RoutedEventArgs e)
    {
        if (!_previewOnly && Application.Current?.MainWindow is MainWindow main) main.OpenToolbarEditor(this);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        if (!_previewOnly && Application.Current is App app && DataContext is MainViewModel viewModel)
            app.ShowAboutWindow(this, viewModel);
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

    private async void OnScreenshotClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_screenshot);

    private void OnCreateCompactShortcutClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        if (DataContext is MainViewModel { SelectedDevice: { IsMediaCast: false } device })
            MainWindow.CreateCompactShortcut(device, this);
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
