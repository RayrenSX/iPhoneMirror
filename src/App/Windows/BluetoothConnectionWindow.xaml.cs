using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class BluetoothConnectionWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private bool _searching;
    private bool _closed;
    private readonly string _targetName;
    private readonly bool _previewOnly;
    private readonly Func<Task<IReadOnlyList<BluetoothClientInfo>>> _refresh;
    private readonly Func<string, bool> _unbind;
    private BluetoothConnectionWindow(Window owner, string targetName,
        Func<Task<IReadOnlyList<BluetoothClientInfo>>> refresh, Func<string, bool> unbind, bool previewOnly = false)
    { _previewOnly = previewOnly; Owner = owner; _targetName = targetName; _refresh = refresh; _unbind = unbind; InitializeComponent(); Closed += (_, _) => _closed = true; }
    internal static string? Show(Window owner, string targetName,
        Func<Task<IReadOnlyList<BluetoothClientInfo>>> refresh, Func<string, bool> unbind)
    { var window = new BluetoothConnectionWindow(owner, targetName, refresh, unbind); return window.ShowDialog() == true ? window.Tag as string : null; }
    internal static void ShowDeveloperPreview(Window owner) =>
        new BluetoothConnectionWindow(owner, "iPhone",
            () => Task.FromResult<IReadOnlyList<BluetoothClientInfo>>([]), _ => false, previewOnly: true).Show();
    private async void FindDevicesClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) { BluetoothClientBindingWindow.ShowDeveloperPreview(this); return; }
        if (_searching || _closed) return;
        _searching = true;
        FindDevicesButton.IsEnabled = false;
        FeedbackText.SetResourceReference(TextBlock.TextProperty, "DeviceBindingBluetoothSearching");
        try
        {
            var clients = await _refresh();
            if (_closed) return;
            if (clients.Count == 0)
            {
                FeedbackText.SetResourceReference(TextBlock.TextProperty, "DeviceBindingBluetoothNoneFound");
                return;
            }

            FeedbackText.SetResourceReference(TextBlock.TextProperty, "DeviceBindingBluetoothWaitingStatus");
            var selected = BluetoothClientBindingWindow.Show(this, _targetName, clients, null, _refresh, _unbind);
            if (_closed || string.IsNullOrWhiteSpace(selected)) return;
            Tag = selected;
            DialogResult = true;
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("bluetooth", "binding_connection_refresh_failed", error);
            if (!_closed) FeedbackText.SetResourceReference(TextBlock.TextProperty, "DeviceBindingBluetoothSearchFailed");
        }
        finally
        {
            _searching = false;
            if (!_closed) FindDevicesButton.IsEnabled = true;
        }
    }
    private void CancelClick(object sender, RoutedEventArgs e)
    { if (_previewOnly) Close(); else DialogResult = false; }
}
