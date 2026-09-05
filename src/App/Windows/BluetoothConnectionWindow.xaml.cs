using System.Windows;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class BluetoothConnectionWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _targetName;
    private readonly Func<Task<IReadOnlyList<BluetoothClientInfo>>> _refresh;
    private readonly Func<string, bool> _unbind;
    private BluetoothConnectionWindow(Window owner, string targetName,
        Func<Task<IReadOnlyList<BluetoothClientInfo>>> refresh, Func<string, bool> unbind)
    { Owner = owner; _targetName = targetName; _refresh = refresh; _unbind = unbind; InitializeComponent(); }
    internal static string? Show(Window owner, string targetName,
        Func<Task<IReadOnlyList<BluetoothClientInfo>>> refresh, Func<string, bool> unbind)
    { var window = new BluetoothConnectionWindow(owner, targetName, refresh, unbind); return window.ShowDialog() == true ? window.Tag as string : null; }
    private async void NextClick(object sender, RoutedEventArgs e)
    {
        var clients = await _refresh();
        var selected = BluetoothClientBindingWindow.Show(this, _targetName, clients, null, _refresh, _unbind);
        if (string.IsNullOrWhiteSpace(selected)) return;
        Tag = selected;
        DialogResult = true;
    }
    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
