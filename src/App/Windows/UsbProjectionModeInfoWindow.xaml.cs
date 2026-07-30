using System.Windows;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public partial class UsbProjectionModeInfoWindow : Wpf.Ui.Controls.FluentWindow
{
    internal UsbProjectionModeInfoWindow(UsbProjectionModeOption option)
    {
        InitializeComponent();
        ModeTitle.Text = option.Label;
        AdvantageText.Text = option.Advantage;
        DisadvantageText.Text = option.Disadvantage;
        NoticeText.Text = option.Notice;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
