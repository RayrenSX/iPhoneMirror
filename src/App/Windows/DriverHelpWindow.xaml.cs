using System.Windows;
namespace IPhoneMirror.App.Windows;
public partial class DriverHelpWindow : Window
{
    public DriverHelpWindow() => InitializeComponent();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
