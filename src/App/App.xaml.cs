using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;
using IPhoneMirror.SharedUI.Services;

namespace IPhoneMirror.App;

public partial class App : Application
{
    internal bool IsSystemSessionEnding { get; private set; }
    internal bool IsUiPreviewMode { get; set; }
    private readonly UpdateSettingsStore _settingsStore = new();
    private readonly GitHubReleaseClient _releaseClient = new();
    private AboutWindow? _aboutWindow;
    private UpdateWindow? _updateWindow;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private int _remoteShutdownRequested;

    internal UpdateSettings UpdateSettings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        if (IsUiPreviewMode)
        {
            base.OnStartup(e);
            return;
        }

        // Keep WPF on its normal hardware-first composition path. Forcing the
        // whole shell to SoftwareOnly also forces MediaElement composition and
        // makes high-resolution media casting visibly drop frames. WPF still
        // falls back to software automatically when the GPU path is not
        // available; the native DirectComposition preview remains unaffected.
        StartupDiagnostics.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            StartupDiagnostics.Write("WPF dispatcher", args.Exception);
            args.Handled = true;
        };
        try
        {
            DiagnosticLogger.Info("lifecycle", "startup_begin",
                ("arguments", e.Args.Length));
            LocalizationService.Initialize();
            AppIdentity.Initialize();
            UpdateSettings = _settingsStore.Load();
            ThemeService.Apply(UpdateSettings.Theme);
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    var window = (Window)sender;
                    ThemeService.Attach(window);
                    BossKeyWindowVisibility.Apply(window);
                }));
            WindowWorkAreaController.EnableForApplication();
            base.OnStartup(e);

            _singleInstanceCoordinator = new SingleInstanceCoordinator();
            if (!_singleInstanceCoordinator.OwnsPrimaryInstance ||
                _singleInstanceCoordinator.HasPreExistingInstance())
            {
                DiagnosticLogger.Info("lifecycle", "instance_conflict_detected",
                    ("owns_primary", _singleInstanceCoordinator.OwnsPrimaryInstance));
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var conflictWindow = new InstanceConflictWindow(_singleInstanceCoordinator);
                AppIdentity.Attach(conflictWindow);
                conflictWindow.ShowDialog();
                if (!conflictWindow.ContinueWithCurrentInstance ||
                    !_singleInstanceCoordinator.OwnsPrimaryInstance)
                {
                    Shutdown(0);
                    return;
                }
            }

            _singleInstanceCoordinator.StartShutdownListener(OnRemoteShutdownRequested);
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            StartupDiagnostics.ValidateRequiredRuntime();
            GitHubReleaseClient.CleanupInterruptedDownloads();
            MainWindow = new MainWindow();
            AppIdentity.Attach(MainWindow);
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

    private void OnRemoteShutdownRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Interlocked.Exchange(ref _remoteShutdownRequested, 1) != 0) return;
            DiagnosticLogger.Info("lifecycle", "remote_shutdown_requested");
            if (MainWindow is { } window) window.Close();
            else Shutdown(0);
        });
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
        if (release is null || release.Version <= VersionManager.Current)
            return null;
        return await _releaseClient.EnrichReleaseNotesAsync(release, cancellationToken);
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

    internal void ShowDeveloperUpdateWindow(Window owner)
    {
        var current = VersionManager.Current;
        var previewVersion = new SemanticVersion(current.Major, current.Minor,
            current.Patch + 1, "developer-preview");
        var release = new ReleaseInfo(
            $"v{previewVersion}", LocalizationService.Get("DeveloperPreviewUpdateTitle"),
            LocalizationService.Get("DeveloperPreviewUpdateBody"),
            DateTimeOffset.Now, previewVersion, true, null, null, null);
        var window = new UpdateWindow(release, _releaseClient, autoDownload: false,
            allowMirrorFallback: false, readOnlyPreview: true)
        {
            Owner = owner,
        };
        window.Show();
    }

    internal bool SaveUpdateSettings()
    {
        try
        {
            _settingsStore.Save(UpdateSettings);
            return true;
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("settings", "save_failed", error);
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (IsUiPreviewMode)
        {
            ThemeService.Shutdown();
            base.OnExit(e);
            return;
        }

        _ = SaveUpdateSettings();
        ThemeService.Shutdown();
        try { _releaseClient.Dispose(); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("updater", "client_dispose_failed", error);
        }
        _singleInstanceCoordinator?.Dispose();
        _singleInstanceCoordinator = null;
        DiagnosticLogger.Shutdown(e.ApplicationExitCode);
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsSystemSessionEnding = true;
        DiagnosticLogger.Info("shutdown", "windows_session_ending",
            ("reason", e.ReasonSessionEnding));
        base.OnSessionEnding(e);
    }
}
