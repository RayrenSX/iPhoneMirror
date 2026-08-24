using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class ProtectedContentNoticeWindow :
    Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    private bool _audioActive;

    internal string DeviceUdid { get; }
    public string TitleText =>
        LocalizationService.Get("CaptureVideoProtectedNoticeTitle");
    public string VideoStatusText =>
        LocalizationService.Get("CaptureVideoProtectedNoticeVideo");
    public string ProtectionStatusText =>
        LocalizationService.Get("CaptureVideoProtectedNoticeProtection");
    public string HintText =>
        LocalizationService.Get("CaptureVideoProtectedNoticeHint");
    public string AudioBadgeText =>
        LocalizationService.Get(_audioActive
            ? "CaptureVideoProtectedAudioBadgeActive"
            : "CaptureVideoProtectedAudioBadgeUnavailable");
    internal ProtectedContentNoticeWindow(string deviceUdid,
        ProtectedContentPresentation presentation, Window owner)
    {
        DeviceUdid = deviceUdid;
        _audioActive = presentation.AudioActive;
        Owner = owner;
        DataContext = this;
        InitializeComponent();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
            LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    internal static void ShowDeveloperPreview(Window owner)
    {
        var presentation = new ProtectedContentPresentation(
            IsProtected: true, AudioActive: false,
            AudioSampleRate: 0, AudioChannels: 0);
        new ProtectedContentNoticeWindow(
            LocalizationService.Get("DeveloperPreviewDeviceName"),
            presentation, owner).Show();
    }

    internal void UpdatePresentation(
        ProtectedContentPresentation presentation)
    {
        if (!presentation.IsProtected)
        {
            Close();
            return;
        }
        var audioStateChanged = _audioActive != presentation.AudioActive;
        _audioActive = presentation.AudioActive;
        if (audioStateChanged) OnPropertyChanged(nameof(AudioBadgeText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(VideoStatusText));
        OnPropertyChanged(nameof(ProtectionStatusText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(AudioBadgeText));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
