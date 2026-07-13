using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using IPhoneMirror.DriverInstaller.Services;

namespace IPhoneMirror.DriverInstaller.Windows;

public partial class FailureHelpWindow : Window
{
    public string ErrorMessage { get; }

    internal FailureHelpWindow(string errorMessage)
    {
        ErrorMessage = errorMessage;
        DataContext = this;
        InitializeComponent();
    }

    private void OnCopyGroupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DriverConstants.QqGroupNumber);
            Process.Start(new ProcessStartInfo("https://im.qq.com/") { UseShellExecute = true });
            PromptWindow.Inform(this, "群号已复制",
                "QQ群号 1050045279 已复制。请在 QQ 中搜索并申请加入。");
        }
        catch (Exception error)
        {
            PromptWindow.Inform(this, "无法打开 QQ", error.Message);
        }
    }

    private void OnOpenAisiClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(DriverConstants.AisiOfficialUrl)
            { UseShellExecute = true });
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
