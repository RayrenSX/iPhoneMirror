using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Windows;

public partial class UpdateWindow : IPhoneMirror.UI.Controls.RoundedWindow, INotifyPropertyChanged
{
    private readonly ReleaseInfo _release;
    private readonly GitHubReleaseClient _client;
    private readonly bool _allowMirrorFallback;
    private readonly bool _readOnlyPreview;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _downloading;
    private bool _installationStarted;
    private double _progressValue;
    private bool _isIndeterminate;
    private string _statusText;
    private string _speedText = string.Empty;
    private string _updateButtonText;
    private string _displayedReleaseBody = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string CurrentVersion => VersionManager.DisplayVersion;
    public string LatestVersion => _release.TagName;
    public string ReleaseName => LocalizationService.RefreshText(_release.Name);
    public string PublishedAt => _release.PublishedAt.LocalDateTime.ToString("yyyy-MM-dd");
    public Visibility ProgressVisibility => _downloading || !string.IsNullOrWhiteSpace(StatusText)
        ? Visibility.Visible : Visibility.Collapsed;
    public bool CanUpdate => !_downloading && !_readOnlyPreview;
    public double ProgressValue { get => _progressValue; private set { _progressValue = value; OnPropertyChanged(); } }
    public bool IsIndeterminate { get => _isIndeterminate; private set { _isIndeterminate = value; OnPropertyChanged(); } }
    public string StatusText { get => LocalizationService.RefreshText(_statusText); private set { _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressVisibility)); } }
    public string SpeedText { get => _speedText; private set { _speedText = value; OnPropertyChanged(); } }
    public string UpdateButtonText { get => LocalizationService.RefreshText(_updateButtonText); private set { _updateButtonText = value; OnPropertyChanged(); } }

    // Keep the historical constructor shape available to reflection-based
    // runtime tests and other in-process preview callers. Optional parameters
    // are not applied by Activator.CreateInstance.
    internal UpdateWindow(ReleaseInfo release, GitHubReleaseClient client,
        bool autoDownload, bool allowMirrorFallback)
        : this(release, client, autoDownload, allowMirrorFallback,
            readOnlyPreview: false)
    {
    }

    internal UpdateWindow(ReleaseInfo release, GitHubReleaseClient client,
        bool autoDownload, bool allowMirrorFallback = true,
        bool readOnlyPreview = false)
    {
        _release = release;
        _client = client;
        _allowMirrorFallback = allowMirrorFallback;
        _readOnlyPreview = readOnlyPreview;
        _statusText = string.Empty;
        _updateButtonText = LocalizationService.Get("UpdateNow");
        DataContext = this;
        InitializeComponent();
        ThemeService.Attach(this);
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(UpdateButtonText));
            OnPropertyChanged(nameof(ReleaseName));
            RefreshReleaseNotes();
        });
        RefreshReleaseNotes();
        Loaded += (_, _) =>
        {
            if (autoDownload) _ = DownloadAndInstallAsync();
        };
        Closing += OnClosing;
    }

    private void RefreshReleaseNotes()
    {
        var body = LocalizationService.RefreshText(_release.Body);
        if (body == _displayedReleaseBody && ReleaseNotesViewer.Document is not null) return;
        _displayedReleaseBody = body;
        ReleaseNotesViewer.Document = MarkdownFlowDocumentRenderer.Render(body);
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e) =>
        await DownloadAndInstallAsync();

    private async Task DownloadAndInstallAsync()
    {
        if (_downloading) return;
        // Capture the token up front. OnClosing disposes _cancellation; reading
        // _cancellation.IsCancellationRequested in the catch filter after that
        // would throw ObjectDisposedException.
        var token = _cancellation.Token;
        _downloading = true;
        UpdateButtonText = LocalizationService.Get("DownloadingUpdate");
        StatusText = LocalizationService.Get("PreparingDownload");
        IsIndeterminate = true;
        OnPropertyChanged(nameof(CanUpdate));
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            if (value.Phase != UpdateDownloadPhase.Download)
            {
                IsIndeterminate = true;
                ProgressValue = 0;
                SpeedText = string.Empty;
                StatusText = LocalizationService.Get(value.Phase switch
                {
                    UpdateDownloadPhase.ConnectivityTest => "TestingUpdateRoutes",
                    UpdateDownloadPhase.Verification => "VerifyingDownload",
                    _ => "MeasuringUpdateRoutes",
                });
                return;
            }
            IsIndeterminate = value.Percentage is null;
            ProgressValue = value.Percentage ?? 0;
            StatusText = value.Percentage is double percentage
                ? LocalizationService.Format("DownloadProgressFormat", percentage)
                : LocalizationService.Get("DownloadingUpdate");
            SpeedText = FormatSpeed(value.BytesPerSecond);
        });
        try
        {
            var downloaded = await _client.DownloadAsync(_release, progress,
                token, _allowMirrorFallback);
            StatusText = downloaded.HashVerified
                ? LocalizationService.Get("UpdateVerified")
                : LocalizationService.Get("UpdateDownloadedNoChecksum");
            SpeedText = string.Empty;
            IsIndeterminate = true;
            UpdateInstallerLauncher.Launch(downloaded);
            _installationStarted = true;
            StatusText = LocalizationService.Get("StartingInstaller");
            // Keep the current version alive until Setup reaches the file
            // replacement stage. Inno Setup's Restart Manager closes it then;
            // if Setup is cancelled or fails earlier, the app remains usable.
            Close();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DiagnosticLogger.Info("updater", "download_cancelled",
                ("release", _release.TagName));
            StatusText = LocalizationService.Get("UpdateDownloadCancelled");
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("updater", "update_workflow_failed", error,
                ("release", _release.TagName));
            StatusText = LocalizationService.Format("UpdateDownloadFailedFormat",
                FriendlyError(error));
            UpdateButtonText = LocalizationService.Get("RetryUpdate");
            IsIndeterminate = false;
        }
        finally
        {
            _downloading = false;
            OnPropertyChanged(nameof(CanUpdate));
        }
    }

    private static string FriendlyError(Exception error) => error switch
    {
        HttpRequestException => LocalizationService.Get("UpdateNetworkUnavailable"),
        TaskCanceledException => LocalizationService.Get("UpdateRequestTimedOut"),
        InvalidDataException => error.Message,
        _ => error.Message,
    };

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / 1024 / 1024:F1} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F0} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_installationStarted) _cancellation.Cancel();
        // Release the cancellation source now that the window is closing.
        _cancellation.Dispose();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        var target = _release.ReleaseUrl.AbsoluteUri;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("shell", "open_target_failed", error,
                ("target", target));
            AppPromptWindow.Inform(LocalizationService.Get("OpenLinkFailedTitle"),
                LocalizationService.Get("GeneralOperationFailed"));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
