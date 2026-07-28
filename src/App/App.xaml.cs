using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class App : Application
{
    private readonly UpdateSettingsStore _settingsStore = new();
    private readonly GitHubReleaseClient _releaseClient = new();
    private AboutWindow? _aboutWindow;
    private UpdateWindow? _updateWindow;

    internal UpdateSettings UpdateSettings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        LocalizationService.Initialize();
        AppIdentity.Initialize();
        UpdateSettings = _settingsStore.Load();
        ThemeService.Apply(UpdateSettings.Theme);
        GitHubReleaseClient.CleanupInterruptedDownloads();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ThemeService.Attach((Window)sender)));
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.ContentRendered += OnMainWindowContentRendered;
        MainWindow.Show();
    }

    private async void OnMainWindowContentRendered(object? sender, EventArgs e)
    {
        if (MainWindow is not null)
            MainWindow.ContentRendered -= OnMainWindowContentRendered;
        if (!UpdateSettings.CheckOnStartup) return;
        try
        {
            var release = await CheckForUpdatesAsync(manual: false);
            if (release is not null && MainWindow is not null)
                ShowUpdateWindow(release, MainWindow, UpdateSettings.AutoDownload);
        }
        catch
        {
            // Startup update checks are best-effort and never block mirroring.
        }
    }

    internal async Task<ReleaseInfo?> CheckForUpdatesAsync(bool manual,
        CancellationToken cancellationToken = default)
    {
        var settings = UpdateSettings.Clone();
        if (manual && !settings.NotifyStableReleases &&
            !settings.NotifyPrereleaseReleases)
            settings.NotifyStableReleases = true;
        var release = await _releaseClient.GetLatestAsync(settings, cancellationToken);
        return release is not null && release.Version > VersionManager.Current
            ? release : null;
    }

    internal void ShowAboutWindow(Window owner)
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }
        var window = new AboutWindow(this) { Owner = owner };
        _aboutWindow = window;
        window.Closed += (_, _) => _aboutWindow = null;
        window.Show();
    }

    internal void ShowUpdateWindow(ReleaseInfo release, Window owner,
        bool autoDownload)
    {
        if (_updateWindow is not null)
        {
            _updateWindow.Activate();
            return;
        }
        var window = new UpdateWindow(release, _releaseClient, autoDownload)
        {
            Owner = owner,
        };
        _updateWindow = window;
        window.Closed += (_, _) => _updateWindow = null;
        window.Show();
    }

    internal void SaveUpdateSettings()
    {
        try { _settingsStore.Save(UpdateSettings); }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SaveUpdateSettings();
        _releaseClient.Dispose();
        base.OnExit(e);
    }
}
