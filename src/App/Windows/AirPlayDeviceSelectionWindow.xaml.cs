using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Windows;

public partial class AirPlayDeviceSelectionWindow : IPhoneMirror.UI.Controls.RoundedWindow, INotifyPropertyChanged
{
    private readonly bool _previewOnly;
    private DeviceViewModel? _selectedDevice;
    public IReadOnlyList<DeviceViewModel> Devices { get; }
    public DeviceViewModel? SelectedDevice { get => _selectedDevice; set { _selectedDevice = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanConfirm)); } }
    public bool CanConfirm => SelectedDevice is not null;
    private AirPlayDeviceSelectionWindow(Window owner, IReadOnlyList<DeviceViewModel> devices, bool previewOnly = false)
    { _previewOnly = previewOnly; Owner = owner; Devices = devices; DataContext = this; InitializeComponent(); }
    internal static DeviceViewModel? Show(Window owner, IReadOnlyList<DeviceViewModel> devices)
    { var window = new AirPlayDeviceSelectionWindow(owner, devices); return window.ShowDialog() == true ? window.SelectedDevice : null; }
    internal static void ShowDeveloperPreview(Window owner) =>
        new AirPlayDeviceSelectionWindow(owner, [], previewOnly: true).Show();
    private void ConfirmClick(object sender, RoutedEventArgs e) { if (CanConfirm) DialogResult = true; }
    private void CancelClick(object sender, RoutedEventArgs e)
    { if (_previewOnly) Close(); else DialogResult = false; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
