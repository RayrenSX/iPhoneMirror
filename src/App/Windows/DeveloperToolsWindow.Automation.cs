using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App.Windows;

public partial class DeveloperToolsWindow
{
    private void InitializeAutomationSettings()
    {
        var settings = _owner.AutomationConfiguration;
        ApiEnabled.IsChecked = settings.Enabled;
        ApiPort.Text = settings.Port.ToString();
        _owner.AutomationStateChanged += RefreshAutomationState;
        RefreshAutomationState();
    }

    private void RefreshAutomationState()
    {
        ApiStatus.SetResourceReference(TextBlock.TextProperty, _owner.AutomationStatusKey);
        ApiAddress.Text = _owner.AutomationAddress;
    }

    private async void OnAutomationApply(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ApiPort.Text, out var port) || port is < 1024 or > 65535)
        { ApiStatus.SetResourceReference(TextBlock.TextProperty, "AutomationInvalidPort"); return; }
        var settings = _owner.AutomationConfiguration;
        settings.Enabled = ApiEnabled.IsChecked == true;
        settings.Port = port;
        await _owner.ConfigureAutomationAsync(settings, save: true);
    }

    private async void OnAutomationRegenerate(object sender, RoutedEventArgs e) =>
        await _owner.RegenerateAutomationKeyAsync();

    private async void OnAutomationCopyKey(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(await _owner.LoadAutomationKeyAsync()); }
        catch { ApiStatus.SetResourceReference(TextBlock.TextProperty, "AutomationStateFailed"); }
    }

    private void OnAutomationCopyAddress(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ApiAddress.Text); }
        catch { ApiStatus.SetResourceReference(TextBlock.TextProperty, "AutomationStateFailed"); }
    }

    private void OnAutomationDocs(object sender, RoutedEventArgs e)
    {
        if (!_owner.AutomationRunning) return;
        try { Process.Start(new ProcessStartInfo(_owner.AutomationAddress + "/api/docs") { UseShellExecute = true }); }
        catch { ApiStatus.SetResourceReference(TextBlock.TextProperty, "AutomationStateFailed"); }
    }

    private async void OnAutomationReclaim(object sender, RoutedEventArgs e) =>
        await _owner.ReclaimAutomationAsync();
}
