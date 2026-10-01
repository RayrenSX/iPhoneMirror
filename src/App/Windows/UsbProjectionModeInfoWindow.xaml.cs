using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public partial class UsbProjectionModeInfoWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    internal UsbProjectionModeInfoWindow(UsbProjectionModeOption option)
    {
        InitializeComponent();
        void Refresh()
        {
            ModeTitle.Text = option.Label;
            AdvantageText.Text = option.Advantage;
            DisadvantageText.Text = option.Disadvantage;
            NoticeText.Text = option.Notice;
        }
        Refresh();
        LocalizationService.RefreshWhenLanguageChanges(this, Refresh);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
