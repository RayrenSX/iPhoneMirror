using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Windows;

public partial class ComponentDownloadWindow : IPhoneMirror.UI.Controls.RoundedWindow, INotifyPropertyChanged
{
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _closeRequested;
    private bool _failed;
    private readonly Func<IProgress<UpdateDownloadProgress>, Action, CancellationToken, Task> _install;
    private string _statusText = LocalizationService.Get("PreparingDownload");
    private string _detailText = string.Empty;
    private double _percentage;
    private bool _isIndeterminate = true;
    public string StatusText { get => LocalizationService.RefreshText(_statusText); private set { _statusText = value; Changed(); } }
    public string DetailText { get => LocalizationService.RefreshText(_detailText); private set { _detailText = value; Changed(); } }
    public double Percentage { get => _percentage; private set { _percentage = value; Changed(); } }
    public bool IsIndeterminate { get => _isIndeterminate; private set { _isIndeterminate = value; Changed(); } }
    public bool CanRetry => _failed && !_busy && !_closeRequested;

    public ComponentDownloadWindow() : this(false) { }

    internal ComponentDownloadWindow(bool previewOnly) : this(previewOnly, UxPlayComponent.InstallAsync) { }

    internal ComponentDownloadWindow(bool previewOnly,
        Func<IProgress<UpdateDownloadProgress>, Action, CancellationToken, Task> install)
    {
        _install = install;
        InitializeComponent();
        DataContext = this;
        ThemeService.Attach(this);
        LocalizationService.RefreshWhenLanguageChanges(this, () => Changed(null));
        Loaded += async (_, _) => { if (!previewOnly) await DownloadAsync(); };
        Closing += (_, args) =>
        {
            _closeRequested = true;
            Changed(nameof(CanRetry));
            if (!_busy) return;
            args.Cancel = true;
            _cancellation?.Cancel();
            StatusText = LocalizationService.Get("UxPlayDownloadCancelling");
        };
    }

    private async Task DownloadAsync()
    {
        if (_busy || _closeRequested) return;
        _busy = true;
        _failed = false;
        Changed(nameof(CanRetry));
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var installing = false;
        var succeeded = false;
        Percentage = 0;
        DetailText = string.Empty;
        IsIndeterminate = true;
        StatusText = LocalizationService.Get("PreparingDownload");
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            if (!ReferenceEquals(_cancellation, cancellation) || !_busy || _closeRequested ||
                installing || cancellation.IsCancellationRequested) return;
            if (value.Phase != UpdateDownloadPhase.Download)
            {
                IsIndeterminate = true;
                DetailText = string.Empty;
                StatusText = LocalizationService.Get(value.Phase switch
                {
                    UpdateDownloadPhase.ConnectivityTest => "UxPlayDownloadFindingMirror",
                    UpdateDownloadPhase.Verification => "VerifyingDownload",
                    _ => "UxPlayDownloadTestingMirror",
                });
                return;
            }
            IsIndeterminate = value.Percentage is null;
            Percentage = value.Percentage ?? 0;
            StatusText = LocalizationService.Format("DownloadProgressFormat", Percentage);
            DetailText = $"{value.BytesReceived / 1_000_000.0:F1} / {value.TotalBytes.GetValueOrDefault() / 1_000_000.0:F1} MB   ·   {value.BytesPerSecond / 1_000_000.0:F1} MB/s";
        });
        try
        {
            await _install(progress, () =>
            {
                if (_closeRequested || cancellation.IsCancellationRequested) return;
                installing = true;
                IsIndeterminate = true;
                DetailText = string.Empty;
                StatusText = LocalizationService.Get("UxPlayDownloadInstalling");
            }, cancellation.Token);
            succeeded = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("components", "uxplay_install_failed", error);
            _failed = true;
            StatusText = LocalizationService.Get("UxPlayDownloadFailed");
            DetailText = LocalizationService.Get(error switch
            {
                System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    => "UxPlayDownloadNotFound",
                System.Net.Http.HttpRequestException or OperationCanceledException => "UpdateNetworkUnavailable",
                System.IO.InvalidDataException => "UxPlayDownloadIntegrityFailed",
                System.IO.IOException or UnauthorizedAccessException => "UxPlayDownloadStorageFailed",
                _ => "UxPlayComponentUnavailable",
            });
            IsIndeterminate = false;
        }
        finally
        {
            _busy = false;
            _cancellation = null;
            Changed(nameof(CanRetry));
        }
        if (succeeded && !_closeRequested) DialogResult = true;
        else if (_closeRequested) Close();
    }

    private void OnCancel(object sender, RoutedEventArgs args) => Close();
    private async void OnRetry(object sender, RoutedEventArgs args) => await DownloadAsync();
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
