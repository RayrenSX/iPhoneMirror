using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Controls;

internal sealed class NetworkProxySettingsEditor
{
    private readonly ComboBox NetworkProxyModeComboBox;
    private readonly TextBox NetworkProxyAddressTextBox;
    private readonly TextBlock NetworkProxyFeedback;

    internal NetworkProxySettingsEditor(Window window)
    {
        NetworkProxyModeComboBox = (ComboBox)window.FindName("NetworkProxyModeComboBox");
        NetworkProxyAddressTextBox = (TextBox)window.FindName("NetworkProxyAddressTextBox");
        NetworkProxyFeedback = (TextBlock)window.FindName("NetworkProxyFeedback");
    }

    private bool _proxySettingsReady;
    private (AppProxyMode Mode, string Address) _savedSettings;

    internal void Initialize()
    {
        var settings = Application.Current is App app
            ? app.UpdateSettings.NetworkProxy : new AppProxySettings();
        LoadProxySettingsDraft(settings);
    }

    internal void RefreshIfChanged()
    {
        if (Application.Current is App app)
        {
            var current = app.UpdateSettings.NetworkProxy;
            if (_savedSettings != (current.Mode, current.Address)) LoadProxySettingsDraft(current);
        }
    }

    private void LoadProxySettingsDraft(AppProxySettings settings)
    {
        _savedSettings = (settings.Mode, settings.Address);
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

    internal void ChangeMode()
    {
        if (!_proxySettingsReady) return;
        NetworkProxyAddressTextBox.IsEnabled =
            NetworkProxyModeComboBox.SelectedValue is "Manual";
        NetworkProxyFeedback.Visibility = Visibility.Collapsed;
    }

    internal void ChangeDraft()
    {
        if (_proxySettingsReady) NetworkProxyFeedback.Visibility = Visibility.Collapsed;
    }

    internal void Save()
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
