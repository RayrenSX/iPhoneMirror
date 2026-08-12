using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Windows;

public partial class UpdateWindow : Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    private readonly ReleaseInfo _release;
    private readonly GitHubReleaseClient _client;
    private readonly bool _allowMirrorFallback;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _downloading;
    private bool _installationStarted;
    private double _progressValue;
    private bool _isIndeterminate;
    private string _statusText;
    private string _speedText = string.Empty;
    private string _updateButtonText;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string CurrentVersion => VersionManager.DisplayVersion;
    public string LatestVersion => _release.TagName;
    public string PublishedAt => _release.PublishedAt.LocalDateTime.ToString("yyyy-MM-dd");
    public Visibility ProgressVisibility => _downloading || !string.IsNullOrWhiteSpace(StatusText)
        ? Visibility.Visible : Visibility.Collapsed;
    public bool CanUpdate => !_downloading;
    public double ProgressValue { get => _progressValue; private set { _progressValue = value; OnPropertyChanged(); } }
    public bool IsIndeterminate { get => _isIndeterminate; private set { _isIndeterminate = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressVisibility)); } }
    public string SpeedText { get => _speedText; private set { _speedText = value; OnPropertyChanged(); } }
    public string UpdateButtonText { get => _updateButtonText; private set { _updateButtonText = value; OnPropertyChanged(); } }

    internal UpdateWindow(ReleaseInfo release, GitHubReleaseClient client,
        bool autoDownload, bool allowMirrorFallback = true)
    {
        _release = release;
        _client = client;
        _allowMirrorFallback = allowMirrorFallback;
        _statusText = string.Empty;
        _updateButtonText = LocalizationService.Get("UpdateNow");
        DataContext = this;
        InitializeComponent();
        ThemeService.Attach(this);
        ReleaseNotesViewer.Document = MarkdownFlowDocumentRenderer.Render(release.Body);
        Loaded += (_, _) =>
        {
            if (autoDownload) _ = DownloadAndInstallAsync();
        };
        Closing += OnClosing;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e) =>
        await DownloadAndInstallAsync();

    private async Task DownloadAndInstallAsync()
    {
        if (_downloading) return;
        _downloading = true;
        UpdateButtonText = LocalizationService.Get("DownloadingUpdate");
        StatusText = LocalizationService.Get("PreparingDownload");
        IsIndeterminate = true;
        OnPropertyChanged(nameof(CanUpdate));
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            IsIndeterminate = value.Percentage is null;
            ProgressValue = value.Percentage ?? 0;
            StatusText = value.Percentage is double percentage
                ? string.Format(LocalizationService.Get("DownloadProgressFormat"), percentage)
                : LocalizationService.Get("DownloadingUpdate");
            SpeedText = FormatSpeed(value.BytesPerSecond);
        });
        try
        {
            var downloaded = await _client.DownloadAsync(_release, progress,
                _cancellation.Token, _allowMirrorFallback);
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
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            DiagnosticLogger.Info("updater", "download_cancelled",
                ("release", _release.TagName));
            StatusText = LocalizationService.Get("UpdateDownloadCancelled");
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("updater", "update_workflow_failed", error,
                ("release", _release.TagName));
            StatusText = string.Format(LocalizationService.Get("UpdateDownloadFailedFormat"),
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
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
