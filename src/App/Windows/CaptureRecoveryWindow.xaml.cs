using System.Windows;
using System.Windows.Input;

namespace IPhoneMirror.App.Windows;

public partial class CaptureRecoveryWindow : Window
{
    private CaptureRecoveryWindow() => InitializeComponent();

    internal static void ShowRecovery() =>
        new CaptureRecoveryWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
