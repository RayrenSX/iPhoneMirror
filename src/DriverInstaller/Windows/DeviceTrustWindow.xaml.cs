using System.Windows;
using System.Windows.Input;

namespace IPhoneMirror.DriverInstaller.Windows;

internal enum DeviceTrustResponse
{
    Trusted,
    NoPrompt,
    NotHandled,
}

public partial class DeviceTrustWindow : Window
{
    private DeviceTrustResponse _response = DeviceTrustResponse.NotHandled;

    private DeviceTrustWindow() => InitializeComponent();

    internal static DeviceTrustResponse Ask(Window owner)
    {
        var window = new DeviceTrustWindow { Owner = owner };
        window.ShowDialog();
        return window._response;
    }

    private void OnTrustedClick(object sender, RoutedEventArgs e) =>
        Complete(DeviceTrustResponse.Trusted);

    private void OnNoPromptClick(object sender, RoutedEventArgs e) =>
        Complete(DeviceTrustResponse.NoPrompt);

    private void OnNotHandledClick(object sender, RoutedEventArgs e) =>
        Complete(DeviceTrustResponse.NotHandled);

    private void Complete(DeviceTrustResponse response)
    {
        _response = response;
        DialogResult = response != DeviceTrustResponse.NotHandled;
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
