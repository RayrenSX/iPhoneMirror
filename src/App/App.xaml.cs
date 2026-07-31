using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
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
        StartupDiagnostics.Initialize();
        DispatcherUnhandledException += (_, args) =>
            StartupDiagnostics.Write("WPF dispatcher", args.Exception);
        try
        {
            DiagnosticLogger.Info("lifecycle", "startup_begin",
                ("arguments", e.Args.Length));
            LocalizationService.Initialize();
            AppIdentity.Initialize();
            StartupDiagnostics.ValidateRequiredRuntime();
            UpdateSettings = _settingsStore.Load();
            ThemeService.Apply(UpdateSettings.Theme);
            GitHubReleaseClient.CleanupInterruptedDownloads();
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => ThemeService.Attach((Window)sender)));
            WindowWorkAreaController.EnableForApplication();
            base.OnStartup(e);
            MainWindow = new MainWindow();
            MainWindow.ContentRendered += OnMainWindowContentRendered;
            MainWindow.Show();
            DiagnosticLogger.Info("lifecycle", "startup_complete");
        }
        catch (Exception error)
        {
            var logPath = StartupDiagnostics.Write("Application startup", error);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { new StartupErrorWindow(error, logPath).ShowDialog(); }
            catch (Exception dialogError)
            {
                DiagnosticLogger.Exception("startup", "error_dialog_failed",
                    dialogError);
            }
            Shutdown(-1);
        }
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
        catch (Exception error)
        {
            // Startup update checks are best-effort and never block mirroring.
            DiagnosticLogger.Exception("updater", "startup_check_failed", error);
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

    internal void ShowAboutWindow(Window owner, MainViewModel mainViewModel,
        bool showDiagnostics = false)
    {
        if (_aboutWindow is not null)
        {
            if (showDiagnostics) _aboutWindow.ShowDiagnostics();
            _aboutWindow.Activate();
            return;
        }
        try
        {
            var window = new AboutWindow(this, mainViewModel) { Owner = owner };
            _aboutWindow = window;
            window.Closed += (_, _) => _aboutWindow = null;
            if (showDiagnostics) window.ShowDiagnostics();
            window.Show();
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("ui", "about_window_open_failed", error);
            AppPromptWindow.Inform(LocalizationService.Get("AboutTitle"), error.Message);
        }
    }

    internal void ShowUpdateWindow(ReleaseInfo release, Window owner,
        bool autoDownload)
    {
        if (_updateWindow is not null)
        {
            _updateWindow.Activate();
            return;
        }
        var window = new UpdateWindow(release, _releaseClient, autoDownload,
            UpdateSettings.AllowMirrorFallback)
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
        catch (Exception error)
        {
            DiagnosticLogger.Exception("settings", "save_failed", error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SaveUpdateSettings();
        ThemeService.Shutdown();
        try { _releaseClient.Dispose(); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("updater", "client_dispose_failed", error);
        }
        DiagnosticLogger.Shutdown(e.ApplicationExitCode);
        base.OnExit(e);
    }
}
