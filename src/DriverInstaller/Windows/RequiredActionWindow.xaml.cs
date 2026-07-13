using System.Windows;
using System.Windows.Input;

namespace IPhoneMirror.DriverInstaller.Windows;

public partial class RequiredActionWindow : Window
{
    public string ActionTitle { get; }
    public string ActionBody { get; }
    public string ConfirmText { get; }

    internal RequiredActionWindow(string title, string body, string confirmText)
    {
        ActionTitle = title;
        ActionBody = body;
        ConfirmText = confirmText;
        DataContext = this;
        InitializeComponent();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
