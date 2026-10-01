using System.Windows;

namespace IPhoneMirror.App.Windows;

public partial class CaptureRecoveryWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private CaptureRecoveryWindow() => InitializeComponent();

    internal static void ShowRecovery() =>
        new CaptureRecoveryWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    internal static void ShowDeveloperPreview(Window owner) =>
        new CaptureRecoveryWindow { Owner = owner }.Show();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

}
