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

public partial class AboutWindow : Window, INotifyPropertyChanged
{
    public sealed record ThemeChoice(AppTheme Value, string Label);

    private readonly App _app;
    private string _updateStatus;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string VersionText => VersionManager.DisplayVersion;
    public IReadOnlyList<ThemeChoice> ThemeChoices { get; }
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
    public AppTheme SelectedTheme
    {
        get => _app.UpdateSettings.Theme;
        set
        {
            _app.UpdateSettings.Theme = value;
            ThemeService.Apply(value);
        }
    }

    internal AboutWindow(App app)
    {
        _app = app;
        _updateStatus = LocalizationService.Get("UpdateStatusReady");
        ThemeChoices =
        [
            new(AppTheme.System, LocalizationService.Get("ThemeSystem")),
            new(AppTheme.Dark, LocalizationService.Get("ThemeDark")),
            new(AppTheme.Light, LocalizationService.Get("ThemeLight")),
        ];
        DataContext = this;
        InitializeComponent();
        ThemeService.Attach(this);
        Closing += (_, _) => _app.SaveUpdateSettings();
    }

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
            UpdateStatus = LocalizationService.Get("UpdateCheckCancelled");
        }
        catch (Exception error)
        {
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
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception error)
        {
            AppPromptWindow.Inform(LocalizationService.Get("OpenLinkFailedTitle"), error.Message);
        }
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
