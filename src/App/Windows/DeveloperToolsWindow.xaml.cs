using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace IPhoneMirror.App.Windows;

public sealed record DeveloperSurfaceItem(string Key, SymbolRegular Icon,
    string Title, string Description);

public sealed record DeveloperOpenWindowItem(string Title, Window Window);

public partial class DeveloperToolsWindow : Wpf.Ui.Controls.FluentWindow,
    INotifyPropertyChanged
{
    private readonly MainWindow _owner;
    private readonly DispatcherTimer _diagnosticsTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(750),
    };
    private string _statusText = string.Empty;
    private bool _updatingControls;

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<DeveloperSurfaceItem> WorkspaceItems { get; private set; }
    public IReadOnlyList<DeveloperSurfaceItem> WindowItems { get; private set; }
    public IReadOnlyList<DeveloperOpenWindowItem> OpenWindows { get; private set; } = [];
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string OpacityText => $"{_owner.Opacity:P0}";
    public string DiagnosticsText { get; private set; } = string.Empty;

    internal DeveloperToolsWindow(MainWindow owner)
    {
        _owner = owner;
        Topmost = false;
        WorkspaceItems =
        [
            Surface("workspace-mirroring", SymbolRegular.ProjectionScreen20, "DeveloperMirroring", "DeveloperMirroringDescription"),
            Surface("workspace-devices", SymbolRegular.Phone20, "DeveloperDevices", "DeveloperDevicesDescription"),
            Surface("workspace-settings", SymbolRegular.Settings20, "DeveloperSettings", "DeveloperSettingsDescription"),
            Surface("workspace-output", SymbolRegular.Speaker220, "DeveloperOutput", "DeveloperOutputDescription"),
            Surface("driver-manager", SymbolRegular.WrenchScrewdriver20, "DeveloperDriver", "DeveloperDriverDescription"),
            Surface("about", SymbolRegular.Info20, "DeveloperAbout", "DeveloperAboutDescription"),
        ];
        WindowItems =
        [
            Surface("advanced-settings", SymbolRegular.Settings20, "DeveloperAdvancedSettings", "DeveloperAdvancedSettingsDescription"),
            Surface("prompt", SymbolRegular.QuestionCircle20, "DeveloperPrompt", "DeveloperPromptDescription"),
            Surface("reverse-control-wired-prerequisite", SymbolRegular.UsbPlug20, "DeveloperReverseControlWiredPrerequisite", "DeveloperReverseControlWiredPrerequisiteDescription"),
            Surface("reverse-control-wireless-prerequisite", SymbolRegular.Info20, "DeveloperReverseControlWirelessPrerequisite", "DeveloperReverseControlWirelessPrerequisiteDescription"),
            Surface("reverse-control-error", SymbolRegular.ErrorCircle20, "DeveloperReverseControlError", "DeveloperReverseControlErrorDescription"),
            Surface("capture-error", SymbolRegular.ErrorCircle20, "DeveloperCaptureError", "DeveloperCaptureErrorDescription"),
            Surface("session-closed", SymbolRegular.ErrorCircle20, "DeveloperSessionClosed", "DeveloperSessionClosedDescription"),
            Surface("usb-config-error", SymbolRegular.UsbPlug20, "DeveloperUsbConfigError", "DeveloperUsbConfigErrorDescription"),
            Surface("capture-recovery", SymbolRegular.Warning20, "DeveloperCaptureRecovery", "DeveloperCaptureRecoveryDescription"),
            Surface("image-settings", SymbolRegular.Image20, "DeveloperImageSettings", "DeveloperImageSettingsDescription"),
            Surface("projection-settings", SymbolRegular.ProjectionScreen20, "DeveloperProjectionSettings", "DeveloperProjectionSettingsDescription"),
            Surface("media-output", SymbolRegular.Speaker220, "DeveloperMediaOutput", "DeveloperMediaOutputDescription"),
            Surface("usb-mode", SymbolRegular.UsbPlug20, "DeveloperUsbMode", "DeveloperUsbModeDescription"),
            Surface("startup-error", SymbolRegular.ErrorCircle20, "DeveloperStartupError", "DeveloperStartupErrorDescription"),
            Surface("update", SymbolRegular.ArrowDownload20, "DeveloperUpdate", "DeveloperUpdateDescription"),
            Surface("instance-conflict", SymbolRegular.Warning20, "DeveloperInstanceConflict", "DeveloperInstanceConflictDescription"),
            Surface("protected-content", SymbolRegular.Warning20, "DeveloperProtectedContent", "DeveloperProtectedContentDescription"),
            Surface("native-preview", SymbolRegular.Window20, "DeveloperNativePreview", "DeveloperNativePreviewDescription"),
        ];
        DataContext = this;
        InitializeComponent();
        ThemeService.Attach(this);
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _diagnosticsTimer.Tick += (_, _) => RefreshDiagnostics();
        Loaded += (_, _) =>
        {
            SyncControls();
            RefreshDiagnostics();
            _diagnosticsTimer.Start();
        };
        Closed += (_, _) =>
        {
            _diagnosticsTimer.Stop();
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        };
    }

    private static DeveloperSurfaceItem Surface(string key, SymbolRegular icon,
        string titleKey, string descriptionKey) => new(key, icon,
        LocalizationService.Get(titleKey), LocalizationService.Get(descriptionKey));

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Recreate the small catalog so the developer window follows the app
        // language without keeping a second localization state machine.
        var workspace = WorkspaceItems.ToArray();
        var windows = WindowItems.ToArray();
        for (var index = 0; index < workspace.Length; ++index)
            workspace[index] = workspace[index] with
            {
                Title = LocalizationService.Get(workspace[index].Key switch
                {
                    "workspace-mirroring" => "DeveloperMirroring",
                    "workspace-devices" => "DeveloperDevices",
                    "workspace-settings" => "DeveloperSettings",
                    "workspace-output" => "DeveloperOutput",
                    "driver-manager" => "DeveloperDriver",
                    _ => "DeveloperAbout",
                }),
                Description = LocalizationService.Get(workspace[index].Key switch
                {
                    "workspace-mirroring" => "DeveloperMirroringDescription",
                    "workspace-devices" => "DeveloperDevicesDescription",
                    "workspace-settings" => "DeveloperSettingsDescription",
                    "workspace-output" => "DeveloperOutputDescription",
                    "driver-manager" => "DeveloperDriverDescription",
                    _ => "DeveloperAboutDescription",
                }),
            };
        for (var index = 0; index < windows.Length; ++index)
        {
            var titleKey = windows[index].Key switch
            {
                "advanced-settings" => "DeveloperAdvancedSettings",
                "prompt" => "DeveloperPrompt",
                "reverse-control-wired-prerequisite" => "DeveloperReverseControlWiredPrerequisite",
                "reverse-control-wireless-prerequisite" => "DeveloperReverseControlWirelessPrerequisite",
                "reverse-control-error" => "DeveloperReverseControlError",
                "capture-error" => "DeveloperCaptureError",
                "session-closed" => "DeveloperSessionClosed",
                "usb-config-error" => "DeveloperUsbConfigError",
                "capture-recovery" => "DeveloperCaptureRecovery",
                "image-settings" => "DeveloperImageSettings",
                "projection-settings" => "DeveloperProjectionSettings",
                "media-output" => "DeveloperMediaOutput",
                "usb-mode" => "DeveloperUsbMode",
                "startup-error" => "DeveloperStartupError",
                "update" => "DeveloperUpdate",
                "instance-conflict" => "DeveloperInstanceConflict",
                "protected-content" => "DeveloperProtectedContent",
                _ => "DeveloperNativePreview",
            };
            var descriptionKey = titleKey + "Description";
            windows[index] = windows[index] with
            {
                Title = LocalizationService.Get(titleKey),
                Description = LocalizationService.Get(descriptionKey),
            };
        }
        ReplaceCollection(nameof(WorkspaceItems), workspace);
        ReplaceCollection(nameof(WindowItems), windows);
        SyncControls();
    }

    private void ReplaceCollection(string propertyName, IReadOnlyList<DeveloperSurfaceItem> value)
    {
        if (propertyName == nameof(WorkspaceItems))
        {
            // The properties are initialized once, but the ItemsControl observes
            // PropertyChanged and receives the localized replacement here.
            WorkspaceItems = value;
        }
        else WindowItems = value;
        OnPropertyChanged(propertyName);
    }

    private void SyncControls()
    {
        _updatingControls = true;
        try
        {
            ThemeComboBox.SelectedValue = ThemeService.Preference.ToString();
            LanguageComboBox.SelectedValue = LocalizationService.SelectedLanguage;
            OpacitySlider.Value = Math.Clamp(_owner.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
            TopmostCheckBox.IsChecked = _owner.Topmost;
        }
        finally { _updatingControls = false; }
    }

    private void RefreshDiagnostics()
    {
        OpenWindows = Application.Current.Windows.Cast<Window>()
            .Where(window => !ReferenceEquals(window, this))
            .Select(window => new DeveloperOpenWindowItem(
                string.IsNullOrWhiteSpace(window.Title) ? window.GetType().Name : window.Title,
                window))
            .ToArray();
        DiagnosticsText = string.Join(Environment.NewLine,
            $"{LocalizationService.Get("DeveloperRuntimeVersion")}: {VersionManager.DisplayVersion}",
            $"{LocalizationService.Get("DeveloperRuntimeCulture")}: {LocalizationService.EffectiveCulture.Name}",
            $"{LocalizationService.Get("DeveloperRuntimeTheme")}: {ThemeService.Preference}",
            $"{LocalizationService.Get("DeveloperRuntimeDpi")}: {VisualTreeHelper.GetDpi(this).PixelsPerDip:F2}",
            $"{LocalizationService.Get("DeveloperRuntimeWindows")}: {OpenWindows.Count}");
        OnPropertyChanged(nameof(OpenWindows));
        OnPropertyChanged(nameof(DiagnosticsText));
        OnPropertyChanged(nameof(OpacityText));
    }

    private void OnSurfaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        try
        {
            _owner.OpenDeveloperSurface(key);
            StatusText = LocalizationService.Get("DeveloperSurfaceOpened");
            RefreshDiagnostics();
        }
        catch (Exception error)
        {
            StatusText = $"{LocalizationService.Get("DeveloperSurfaceOpenFailed")}: {error.Message}";
            DiagnosticLogger.Exception("ui", "developer_surface_open_failed", error,
                ("surface", key));
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || ThemeComboBox.SelectedValue is not string value ||
            !Enum.TryParse<AppTheme>(value, out var theme)) return;
        ThemeService.Apply(theme);
        RefreshDiagnostics();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || LanguageComboBox.SelectedValue is not string value) return;
        LocalizationService.SetLanguage(value);
        RefreshDiagnostics();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls) return;
        _owner.Opacity = e.NewValue;
        OnPropertyChanged(nameof(OpacityText));
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingControls) return;
        _owner.Topmost = TopmostCheckBox.IsChecked == true;
        RefreshDiagnostics();
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string preset }) return;
        _owner.WindowState = WindowState.Normal;
        switch (preset)
        {
            case "compact":
                _owner.Width = 1280;
                _owner.Height = 700;
                break;
            case "maximize":
                _owner.WindowState = WindowState.Maximized;
                break;
            default:
                _owner.Width = 1540;
                _owner.Height = 900;
                break;
        }
        RefreshDiagnostics();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ThemeService.Apply(AppTheme.System);
        LocalizationService.SetLanguage(LocalizationService.SystemLanguage);
        _owner.Opacity = 1;
        _owner.Topmost = false;
        _owner.WindowState = WindowState.Normal;
        _owner.Width = 1540;
        _owner.Height = 900;
        SyncControls();
        StatusText = LocalizationService.Get("DeveloperParametersReset");
        RefreshDiagnostics();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void OnActivateWindowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeveloperOpenWindowItem item })
        {
            item.Window.Activate();
            item.Window.Focus();
        }
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeveloperOpenWindowItem item } &&
            !ReferenceEquals(item.Window, _owner))
        {
            item.Window.Close();
            RefreshDiagnostics();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

}
