using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Controls;

namespace IPhoneMirror.App.Windows;

public partial class DeveloperToolsWindow
{
    private DeviceHubPanel? _deviceToolsPanel;
    internal Func<DeviceHubPanel> CreateDeviceToolsPanel { private get; set; } = null!;

    private void InitializeDeviceTools()
    {
        CreateDeviceToolsPanel = () => new DeviceHubPanel(_owner.DeviceHubViewModel, developerMode: true);
        _owner.DeviceHubViewModel.PropertyChanged += OnDeviceToolsContextChanged;
        IsVisibleChanged += (_, _) => RefreshDeviceToolsContext();
        Closed += (_, _) =>
        {
            _owner.DeviceHubViewModel.PropertyChanged -= OnDeviceToolsContextChanged;
            _deviceToolsPanel?.Dispose();
        };
    }

    internal void ShowDeviceTools() => DeviceToolsTab.IsSelected = true;

    private void OnDeveloperPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DeveloperPages)) RefreshDeviceToolsContext();
    }

    private void OnDeviceToolsContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnDeviceToolsContextChanged(sender, e)); return; }
        if (e.PropertyName is null or "SelectedDevice" or "IsUsbControlEnabled" or "IsWirelessControlEnabled" or "CurrentSessionHandle")
            RefreshDeviceToolsContext();
    }

    private void RefreshDeviceToolsContext()
    {
        // InitializeComponent can raise selection events before setup finishes.
        if (CreateDeviceToolsPanel is null) return;
        var active = IsVisible && DeviceToolsTab.IsSelected;
        if (active && _deviceToolsPanel is null)
        {
            _deviceToolsPanel = CreateDeviceToolsPanel();
            _deviceToolsPanel.CloseRequested += (_, _) => DeveloperPages.SelectedIndex = 0;
            DeveloperDeviceHubHost.Content = _deviceToolsPanel;
        }
        _deviceToolsPanel?.SetDevice(_owner.DeviceHubViewModel.SelectedDevice, active);
    }
}
