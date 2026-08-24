using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public partial class AboutWindow : Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    private readonly App _app;
    private readonly MainViewModel _mainViewModel;
    private readonly DispatcherTimer _logTimer;
    private string _updateStatus;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string VersionText => VersionManager.DisplayVersion;
    public string DiagnosticPath => DiagnosticLogger.DirectoryPath;
    public object MainViewModel => _mainViewModel;
    public string UpdateStatus
    {
        get => _updateStatus;
        private set { _updateStatus = value; OnPropertyChanged(); }
    }
    public bool CheckOnStartup
    {
        get => _app.UpdateSettings.CheckOnStartup;
        set => _app.UpdateSettings.CheckOnStartup = value;
    }
    public bool AutoDownload
    {
        get => _app.UpdateSettings.AutoDownload;
        set => _app.UpdateSettings.AutoDownload = value;
    }
    public bool AllowMirrorFallback
    {
        get => _app.UpdateSettings.AllowMirrorFallback;
        set => _app.UpdateSettings.AllowMirrorFallback = value;
    }
    public bool NotifyStableReleases
    {
        get => _app.UpdateSettings.NotifyStableReleases;
        set => _app.UpdateSettings.NotifyStableReleases = value;
    }
    public bool NotifyPrereleaseReleases
    {
        get => _app.UpdateSettings.NotifyPrereleaseReleases;
        set => _app.UpdateSettings.NotifyPrereleaseReleases = value;
    }
    internal AboutWindow(App app, MainViewModel mainViewModel)
    {
        _app = app;
        _mainViewModel = mainViewModel;
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _logTimer.Tick += OnLogTimerTick;
        _updateStatus = LocalizationService.Get("UpdateStatusReady");
        _diagnosticStatus = LocalizationService.Get("DiagnosticsRetentionSummary");
        DataContext = this;
        InitializeComponent();
        ThemeService.Attach(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logTimer.Start();
        await _mainViewModel.RefreshLogsAsync();
    }

    private void OnLogTimerTick(object? sender, EventArgs e) =>
        _ = _mainViewModel.RefreshLogsAsync();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _logTimer.Stop();
        _logTimer.Tick -= OnLogTimerTick;
        Loaded -= OnLoaded;
        Closing -= OnClosing;
        _app.SaveUpdateSettings();
    }

    private void OnLiveLogTextChanged(object sender, TextChangedEventArgs e) =>
        LiveLogTextBox.ScrollToEnd();

    internal void ShowDiagnostics() => AboutTabs.SelectedIndex = 2;

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        UpdateStatus = LocalizationService.Get("CheckingForUpdates");
        try
        {
            var release = await _app.CheckForUpdatesAsync(manual: true);
            if (release is null)
            {
                UpdateStatus = LocalizationService.Get("AlreadyUpToDate");
                return;
            }
            UpdateStatus = string.Format(LocalizationService.Get("UpdateAvailableFormat"),
                release.TagName);
            _app.ShowUpdateWindow(release, this, autoDownload: false);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLogger.Info("updater", "manual_check_cancelled");
            UpdateStatus = LocalizationService.Get("UpdateCheckCancelled");
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("updater", "manual_check_failed", error);
            UpdateStatus = string.Format(LocalizationService.Get("UpdateCheckFailedFormat"),
                FriendlyError(error));
        }
    }

    private static string FriendlyError(Exception error) => error switch
    {
        HttpRequestException => LocalizationService.Get("UpdateNetworkUnavailable"),
        TaskCanceledException => LocalizationService.Get("UpdateRequestTimedOut"),
        _ => error.Message,
    };

    private static void Open(string target)
    {
        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true,
                });
                return;
            }

            var path = Path.GetFullPath(target);
            if (Directory.Exists(path))
            {
                StartExplorer(path);
                return;
            }
            if (File.Exists(path))
            {
                // LICENSE and changelog files are deliberately opened through
                // Explorer. In particular, LICENSE has no extension and must
                // never be handed to a user's Photoshop/unknown-file handler.
                StartExplorer(path, selectFile: true);
                return;
            }

            throw new FileNotFoundException("The local target does not exist.", path);
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("shell", "open_target_failed", error,
                ("target", Path.GetFileName(target)));
                AppPromptWindow.Inform(LocalizationService.Get("OpenLinkFailedTitle"), error.Message);
        }
    }

    private static void StartExplorer(string path, bool selectFile = false)
    {
        var arguments = selectFile ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) =>
        Open(DiagnosticLogger.DirectoryPath);

    private void OnCleanLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var logs = DiagnosticLogger.Cleanup(includeActiveLogs: true);
            var updates = GitHubReleaseClient.CleanupOldDownloads(
                includeCompleted: true);
            var deletedFiles = logs.DeletedFiles + updates.DeletedFiles;
            var deletedBytes = logs.DeletedBytes + updates.DeletedBytes;
            var skipped = logs.SkippedFiles + updates.SkippedFiles;
            DiagnosticStatus = string.Format(LocalizationService.Get(
                "DiagnosticsCleanedFormat"), deletedFiles,
                FormatBytes(deletedBytes), skipped);
            DiagnosticLogger.Info("logging", "manual_cleanup_complete",
                ("deleted_files", deletedFiles), ("deleted_bytes", deletedBytes),
                ("skipped_files", skipped));
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("logging", "manual_cleanup_failed", error);
            DiagnosticStatus = string.Format(LocalizationService.Get(
                "DiagnosticsCleanupFailedFormat"), AppLog.Error(error));
        }
    }

    private string _diagnosticStatus = string.Empty;
    public string DiagnosticStatus
    {
        get => _diagnosticStatus;
        private set { _diagnosticStatus = value; OnPropertyChanged(); }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / 1024d / 1024d:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:F0} KB";
        return $"{bytes} B";
    }

    private void OnGitHubClick(object sender, RoutedEventArgs e) =>
        Open("https://github.com/RayrenSX/iPhoneMirror");

    private void OnChangelogClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
        Open(File.Exists(path) ? path :
            "https://github.com/RayrenSX/iPhoneMirror/releases");
    }

    private void OnLicenseClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        Open(File.Exists(path) ? path :
            "https://github.com/RayrenSX/iPhoneMirror/blob/main/LICENSE");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
