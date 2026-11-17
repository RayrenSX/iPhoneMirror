using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public partial class AboutWindow : IPhoneMirror.UI.Controls.RoundedWindow, INotifyPropertyChanged
{
    private const string ProjectUrl = DonationAssets.OfficialProjectUrl;
    private readonly App _app;
    private readonly MainViewModel _mainViewModel;
    private readonly DispatcherTimer _logTimer;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private CancellationTokenSource? _updateCheckCancellation;
    private bool _closed;
    private bool _isCheckingForUpdates;
    private bool _isLogPaused;
    private string _displayedLogText;
    private string _updateStatus;
    private int _versionClickCount;
    private DateTime _lastVersionClickUtc;
    private double _overviewHeight = 722;
    private double _supportHeight = 842;
    private bool _supportPageSelected;

    private void OnVersionClick(object sender, RoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastVersionClickUtc).TotalSeconds > 2) _versionClickCount = 0;
        _lastVersionClickUtc = now;
        if (++_versionClickCount < 5) return;
        _versionClickCount = 0;
        (Owner as MainWindow ?? Application.Current.MainWindow as MainWindow)?.EnableDeveloperToolsFromAbout();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string VersionText => VersionManager.DisplayVersion;
    public string DiagnosticPath => DiagnosticLogger.DirectoryPath;
    public string DonationRecipientName => DonationAssets.RecipientDisplayName;
    public string DonationOfficialAddress => ProjectUrl;
    public object MainViewModel => _mainViewModel;
    public string DisplayedLogText
    {
        get => _displayedLogText;
        private set
        {
            if (_displayedLogText == value) return;
            _displayedLogText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCopyLog));
        }
    }
    public bool CanCopyLog => !string.IsNullOrWhiteSpace(DisplayedLogText);
    public bool IsLogPaused
    {
        get => _isLogPaused;
        private set
        {
            if (_isLogPaused == value) return;
            _isLogPaused = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LogPauseActionText));
            if (!value)
            {
                LiveLogTextBox.Select(0, 0);
                DisplayedLogText = _mainViewModel.LogText;
                LiveLogTextBox.ScrollToEnd();
            }
            UpdateLogPolling();
        }
    }
    public string LogPauseActionText => LocalizationService.Get(IsLogPaused ? "LogResume" : "LogPause");
    public bool CanCheckForUpdates => !_closed && !_isCheckingForUpdates;
    public string CheckUpdatesButtonText => LocalizationService.Get(
        _isCheckingForUpdates ? "CheckingForUpdates" : "CheckForUpdates");
    public string UpdateStatus
    {
        get => LocalizationService.RefreshText(_updateStatus);
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
        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _copyFeedbackTimer.Tick += OnCopyFeedbackExpired;
        _displayedLogText = _mainViewModel.LogText;
        _updateStatus = LocalizationService.Get("UpdateStatusReady");
        _diagnosticStatus = LocalizationService.Get("DiagnosticsRetentionSummary");
        DataContext = this;
        InitializeComponent();
        InitializeDonationImages();
        _mainViewModel.PropertyChanged += OnMainViewModelChanged;
        ThemeService.Attach(this);
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
        {
            OnPropertyChanged(nameof(UpdateStatus));
            OnPropertyChanged(nameof(DiagnosticStatus));
            OnPropertyChanged(nameof(LogPauseActionText));
            OnPropertyChanged(nameof(CheckUpdatesButtonText));
        });
        Loaded += OnLoaded;
        IsVisibleChanged += OnVisibilityChanged;
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateLogPolling();

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateLogPolling();

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateLogPolling();

    private void OnAboutTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, AboutTabs)) return;
        if (IsLoaded && WindowState == WindowState.Normal)
        {
            var support = ReferenceEquals(AboutTabs.SelectedItem, DonationTab);
            if (support != _supportPageSelected)
            {
                if (_supportPageSelected) _supportHeight = ActualHeight;
                else _overviewHeight = ActualHeight;
                var available = IPhoneMirror.SharedUI.Services.WindowWorkAreaController.GetAvailableSize(this);
                var ratio = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY > 1 ? 0.8 : 1;
                Height = Math.Max(MinHeight, Math.Min(support ? _supportHeight : _overviewHeight, available.Height * ratio));
            }
            _supportPageSelected = support;
        }
        UpdateLogPolling();
    }

    private void UpdateLogPolling()
    {
        if (_closed || IsLogPaused || !IsLoaded || !IsVisible || WindowState == WindowState.Minimized ||
            !ReferenceEquals(AboutTabs.SelectedItem, OverviewTab))
        {
            _logTimer.Stop();
            return;
        }
        if (_logTimer.IsEnabled) return;
        _logTimer.Start();
        // The overview contains the visible diagnostic log. The support page
        // does not need file I/O or log text layout.
        OnLogTimerTick(this, EventArgs.Empty);
    }

    private int _refreshInProgress;

    private async void OnLogTimerTick(object? sender, EventArgs e)
    {
        // Reentrancy guard: the timer fires every 500ms but a refresh may take
        // longer. Skip overlapping invocations instead of queueing them, and
        // observe exceptions so a slow refresh never surfaces unobserved.
        if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0) return;
        try
        {
            // Loaded/visibility callbacks can arrive without a dispatcher
            // synchronization context. Enter through the dispatcher so an
            // asynchronous file read resumes UI-bound log updates on its thread.
            await Dispatcher.InvokeAsync(_mainViewModel.RefreshLogsAsync).Task.Unwrap();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Exception("about", "log_refresh_failed", ex);
        }
        finally
        {
            _refreshInProgress = 0;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closed = true;
        _updateCheckCancellation?.Cancel();
        _logTimer.Stop();
        _app.SaveUpdateSettings();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _updateCheckCancellation?.Cancel();
        _mainViewModel.PropertyChanged -= OnMainViewModelChanged;
        _logTimer.Stop();
        _logTimer.Tick -= OnLogTimerTick;
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Tick -= OnCopyFeedbackExpired;
        Loaded -= OnLoaded;
        IsVisibleChanged -= OnVisibilityChanged;
        StateChanged -= OnWindowStateChanged;
        AboutTabs.SelectionChanged -= OnAboutTabChanged;
        Closing -= OnClosing;
        Closed -= OnClosed;
    }

    private void OnMainViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_mainViewModel.LogText)) return;
        void RefreshDisplayedLog()
        {
            if (!_closed && !IsLogPaused) DisplayedLogText = _mainViewModel.LogText;
        }
        if (Dispatcher.CheckAccess()) RefreshDisplayedLog();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshDisplayedLog);
    }

    private void OnLiveLogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLogPaused) LiveLogTextBox.ScrollToEnd();
    }

    private void OnLogPauseClick(object sender, RoutedEventArgs e) => IsLogPaused = !IsLogPaused;

    private void OnLiveLogPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) IsLogPaused = true;
    }

    private void OnLiveLogPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0 && LiveLogTextBox.VerticalOffset > 0) IsLogPaused = true;
    }

    private void OnLiveLogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or
            Key.PageUp or Key.PageDown or Key.Home or Key.End) IsLogPaused = true;
    }

    private void OnLiveLogSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (LiveLogTextBox.SelectionLength > 0) IsLogPaused = true;
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        _mainViewModel.ClearLogCommand.Execute(null);
        DisplayedLogText = _mainViewModel.LogText;
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e) =>
        CopyWithFeedback(DisplayedLogText, LogCopyLabel, "LogCopied", "LogCopyFailed");

    internal void ShowDiagnostics()
    {
        AboutTabs.SelectedItem = OverviewTab;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsVisible) return;
            DiagnosticsCard.BringIntoView();
            LiveLogTextBox.Focus();
        }));
    }

    private void OnOverviewHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 760;
        Grid.SetRow(OverviewActions, stacked ? 1 : 0);
        Grid.SetColumn(OverviewActions, stacked ? 0 : 2);
        Grid.SetColumnSpan(OverviewActions, stacked ? 3 : 1);
        OverviewActions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        OverviewActions.Margin = stacked ? new Thickness(-6, 8, -8, -2) : new Thickness(12, 0, -8, 0);
    }

    private void OnOverviewColumnsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 780;
        Grid.SetColumnSpan(UpdateSettingsCard, stacked ? 3 : 1);
        Grid.SetColumn(DiagnosticsCard, stacked ? 0 : 2);
        Grid.SetColumnSpan(DiagnosticsCard, stacked ? 3 : 1);
        Grid.SetRow(DiagnosticsCard, stacked ? 2 : 0);
        OverviewCardsRowGap.Height = new GridLength(stacked ? 12 : 0);
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (!CanCheckForUpdates) return;
        using var cancellation = new CancellationTokenSource();
        _updateCheckCancellation = cancellation;
        _isCheckingForUpdates = true;
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CheckUpdatesButtonText));
        UpdateStatus = LocalizationService.Get("CheckingForUpdates");
        try
        {
            var release = await _app.CheckForUpdatesAsync(manual: true,
                cancellationToken: cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested) return;
            if (release is null)
            {
                UpdateStatus = LocalizationService.Get("AlreadyUpToDate");
                return;
            }
            UpdateStatus = LocalizationService.Format("UpdateAvailableFormat",
                release.TagName);
            _app.ShowUpdateWindow(release, this, autoDownload: false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            DiagnosticLogger.Info("updater", "manual_check_cancelled");
            if (!_closed) UpdateStatus = LocalizationService.Get("UpdateCheckCancelled");
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("updater", "manual_check_failed", error);
            if (!_closed) UpdateStatus = LocalizationService.Format("UpdateCheckFailedFormat",
                FriendlyError(error));
        }
        finally
        {
            _updateCheckCancellation = null;
            _isCheckingForUpdates = false;
            OnPropertyChanged(nameof(CanCheckForUpdates));
            OnPropertyChanged(nameof(CheckUpdatesButtonText));
        }
    }

    private static string FriendlyError(Exception error) => error switch
    {
        HttpRequestException => LocalizationService.Get("UpdateNetworkUnavailable"),
        OperationCanceledException => LocalizationService.Get("UpdateRequestTimedOut"),
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

            throw new FileNotFoundException(LocalizationService.Get("LocalTargetMissing"), path);
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("shell", "open_target_failed", error,
                ("target", Path.GetFileName(target)));
                AppPromptWindow.Inform(LocalizationService.Get("OpenLinkFailedTitle"),
                    LocalizationService.Get("GeneralOperationFailed"));
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
            DiagnosticStatus = LocalizationService.Format("DiagnosticsCleanedFormat", deletedFiles,
                FormatBytes(deletedBytes), skipped);
            DiagnosticLogger.Info("logging", "manual_cleanup_complete",
                ("deleted_files", deletedFiles), ("deleted_bytes", deletedBytes),
                ("skipped_files", skipped));
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("logging", "manual_cleanup_failed", error);
            DiagnosticStatus = LocalizationService.Format("DiagnosticsCleanupFailedFormat", AppLog.Error(error));
        }
    }

    private string _diagnosticStatus = string.Empty;
    public string DiagnosticStatus
    {
        get => LocalizationService.RefreshText(_diagnosticStatus);
        private set { _diagnosticStatus = value; OnPropertyChanged(); }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / 1024d / 1024d:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:F0} KB";
        return $"{bytes} B";
    }

    private void OnGitHubClick(object sender, RoutedEventArgs e) =>
        Open(ProjectUrl);

    private void OnDonationHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 560;
        Grid.SetColumnSpan(DonationHeaderIntro, stacked ? 2 : 1);
        DonationHeaderIntro.Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 16, 0);
        Grid.SetColumn(DonationHeaderActions, stacked ? 0 : 1);
        Grid.SetColumnSpan(DonationHeaderActions, stacked ? 2 : 1);
        Grid.SetRow(DonationHeaderActions, stacked ? 1 : 0);
        DonationHeaderActions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void OnCopyProjectLinkClick(object sender, RoutedEventArgs e) =>
        CopyWithFeedback(ProjectUrl, DonationCopyLinkLabel,
            "DonationProjectLinkCopied", "DonationProjectLinkCopyFailed");

    private void CopyWithFeedback(string text, TextBlock label, string copiedKey, string failedKey)
    {
        try
        {
            Clipboard.SetText(text);
            label.SetResourceReference(TextBlock.TextProperty, copiedKey);
        }
        catch (ExternalException error)
        {
            DiagnosticLogger.Exception("about", "copy_failed", error);
            label.SetResourceReference(TextBlock.TextProperty, failedKey);
        }
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void OnCopyFeedbackExpired(object? sender, EventArgs e)
    {
        _copyFeedbackTimer.Stop();
        LogCopyLabel.SetResourceReference(TextBlock.TextProperty, "LogCopy");
        DonationCopyLinkLabel.SetResourceReference(TextBlock.TextProperty, "DonationCopyProjectLink");
    }

    private void InitializeDonationImages()
    {
        try
        {
            var wechat = DonationAssets.Load(DonationPaymentMethod.WeChat);
            var alipay = DonationAssets.Load(DonationPaymentMethod.Alipay);
            WeChatDonationQrCode.Source = wechat;
            AlipayDonationQrCode.Source = alipay;
            DonationPaymentCards.Visibility = Visibility.Visible;
        }
        catch (Exception error)
        {
            WeChatDonationQrCode.Source = null;
            AlipayDonationQrCode.Source = null;
            DonationPaymentCards.Visibility = Visibility.Collapsed;
            DonationIntegrityError.Visibility = Visibility.Visible;
            DiagnosticLogger.Exception("about", "donation_integrity_failed", error);
        }
    }

    private void OnDonationCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 640;
        Grid.SetColumnSpan(WeChatDonationCard, stacked ? 3 : 1);
        Grid.SetColumn(AlipayDonationCard, stacked ? 0 : 2);
        Grid.SetColumnSpan(AlipayDonationCard, stacked ? 3 : 1);
        Grid.SetRow(AlipayDonationCard, stacked ? 2 : 0);
        DonationCardsRowGap.Height = new GridLength(stacked ? 16 : 0);
    }

    private void OnSupportAuthorClick(object sender, RoutedEventArgs e) =>
        AboutTabs.SelectedItem = DonationTab;

    private void OnChangelogClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, LocalizedReleaseNotes.ChangelogFileName);
        Open(File.Exists(path) ? path :
            LocalizationService.EffectiveCulture.Name == LocalizationService.TraditionalChineseTaiwan
                ? "https://github.com/RayrenSX/iPhoneMirror/blob/main/CHANGELOG.zh-TW.md"
                : "https://github.com/RayrenSX/iPhoneMirror/releases");
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
