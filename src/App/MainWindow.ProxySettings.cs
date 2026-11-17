using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private bool _proxySettingsReady;

    private void InitializeProxySettings()
    {
        var settings = Application.Current is App app
            ? app.UpdateSettings.NetworkProxy : new AppProxySettings();
        LoadProxySettingsDraft(settings);
    }

    private void LoadProxySettingsDraft(AppProxySettings settings)
    {
        _proxySettingsReady = false;
        try
        {
            NetworkProxyModeComboBox.SelectedValue = settings.Mode.ToString();
            NetworkProxyAddressTextBox.Text = string.IsNullOrWhiteSpace(settings.Address)
                ? "http://127.0.0.1:7897" : settings.Address;
            NetworkProxyAddressTextBox.IsEnabled = settings.Mode == AppProxyMode.Manual;
            NetworkProxyFeedback.Visibility = Visibility.Collapsed;
        }
        finally { _proxySettingsReady = true; }
    }

    private void OnNetworkProxyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_proxySettingsReady) return;
        NetworkProxyAddressTextBox.IsEnabled =
            NetworkProxyModeComboBox.SelectedValue is "Manual";
        NetworkProxyFeedback.Visibility = Visibility.Collapsed;
    }

    private void OnNetworkProxyDraftChanged(object sender, TextChangedEventArgs e)
    {
        if (_proxySettingsReady) NetworkProxyFeedback.Visibility = Visibility.Collapsed;
    }

    private void OnSaveNetworkProxyClick(object sender, RoutedEventArgs e)
    {
        if (!_proxySettingsReady || Application.Current is not App app) return;
        if (NetworkProxyModeComboBox.SelectedValue is not string modeValue ||
            !Enum.TryParse<AppProxyMode>(modeValue, out var mode) ||
            !Enum.IsDefined(mode) ||
            !AppProxyPolicy.TryNormalize(new AppProxySettings
            {
                Mode = mode,
                Address = NetworkProxyAddressTextBox.Text,
            }, out var normalized))
        {
            SetProxySettingsFeedback("NetworkProxyInvalidAddress", error: true);
            return;
        }

        var previous = app.UpdateSettings.NetworkProxy;
        app.UpdateSettings.NetworkProxy = normalized;
        if (!app.SaveUpdateSettings())
        {
            app.UpdateSettings.NetworkProxy = previous;
            SetProxySettingsFeedback("NetworkProxySaveFailed", error: true);
            return;
        }

        LoadProxySettingsDraft(normalized);
        SetProxySettingsFeedback("NetworkProxySaved", error: false);
    }

    private void SetProxySettingsFeedback(string resourceKey, bool error)
    {
        NetworkProxyFeedback.SetResourceReference(TextBlock.TextProperty, resourceKey);
        NetworkProxyFeedback.SetResourceReference(TextBlock.ForegroundProperty,
            error ? "ErrorBrush" : "MutedTextBrush");
        NetworkProxyFeedback.Visibility = Visibility.Visible;
    }
}
