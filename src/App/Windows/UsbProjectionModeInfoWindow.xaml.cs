using System.Windows;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public partial class UsbProjectionModeInfoWindow : Window
{
    internal UsbProjectionModeInfoWindow(UsbProjectionModeOption option)
    {
        InitializeComponent();
        ModeTitle.Text = option.Label;
        AdvantageText.Text = option.Advantage;
        DisadvantageText.Text = option.Disadvantage;
        NoticeText.Text = option.Notice;
    }
}
