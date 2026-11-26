using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Controls;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private NetworkProxySettingsEditor? _networkProxyEditor;

    private void InitializeProxySettings() => (_networkProxyEditor ??= new(this)).Initialize();

    private void OnNetworkProxyModeChanged(object sender, SelectionChangedEventArgs e) =>
        _networkProxyEditor?.ChangeMode();

    private void OnNetworkProxyDraftChanged(object sender, TextChangedEventArgs e) =>
        _networkProxyEditor?.ChangeDraft();

    private void OnSaveNetworkProxyClick(object sender, RoutedEventArgs e) =>
        _networkProxyEditor?.Save();
}
