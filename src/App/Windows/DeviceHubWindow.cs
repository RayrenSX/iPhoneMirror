using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

internal sealed partial class DeviceHubWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _stop = new();
    private readonly ComboBox _devices = new() { DisplayMemberPath = "DisplayName", MinWidth = 260 };
    private readonly ListBox _apps = new() { DisplayMemberPath = "Label", MinHeight = 120 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly CheckBox _audio = new() { IsChecked = true, Margin = new(0, 10, 16, 10) };
    private readonly CheckBox _record = new() { Margin = new(0, 10, 0, 10) };
    private readonly List<Button> _buttons = [];
    private bool _busy;
    private sealed record AppItem(string BundleId, string Name, bool FirstParty = false, bool Developer = false, bool Clip = false)
    { public string Label => $"{Name}  ·  {BundleId}"; }

    internal DeviceHubWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        SetResourceReference(TitleProperty, "DeviceHubTitle");
        Width = 900; Height = 800; MinWidth = 560; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var surface = new Border(); surface.SetResourceReference(StyleProperty, "RoundedWindowContent");
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new());
        surface.Child = layout;
        var header = new Grid(); header.SetResourceReference(StyleProperty, "SubWindowHeader");
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        var title = new TextBlock { Margin = new(0, 0, 48, 0) };
        title.SetResourceReference(TextBlock.TextProperty, "DeviceHubTitle"); title.SetResourceReference(StyleProperty, "SubWindowTitle");
        header.Children.Add(title);
        var close = new Button { Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Dismiss20, FontSize = 18 } };
        close.SetResourceReference(StyleProperty, "SubWindowCloseButton"); close.SetResourceReference(ToolTipProperty, "Close");
        close.Click += (_, e) => { e.Handled = true; Close(); };
        header.Children.Add(close); layout.Children.Add(header);
        var panel = new DockPanel { Margin = new(0, 12, 0, 0) }; Grid.SetRow(panel, 1); layout.Children.Add(panel);
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) };
        intro.SetResourceReference(TextBlock.TextProperty, "DeviceHubDescription"); top.Children.Add(intro);
        _devices.ItemsSource = new ListCollectionView(viewModel.Devices)
        { Filter = item => item is DeviceViewModel { IsMediaCast: false } };
        _devices.SelectedItem = viewModel.SelectedDevice;
        if (_devices.SelectedIndex < 0 && _devices.Items.Count == 1) _devices.SelectedIndex = 0;
        _devices.SelectionChanged += async (_, _) => { _apps.ItemsSource = null; await StopLogsAsync(); ClearManagementLists(); };
        top.Children.Add(_devices);
        var options = new WrapPanel(); options.Children.Add(_audio); options.Children.Add(_record);
        _audio.SetResourceReference(ContentProperty, "DeviceHubAudio");
        _record.SetResourceReference(ContentProperty, "DeviceHubRecord");
        var prepare = new WrapPanel { Margin = new(0, 10, 0, 0) }; top.Children.Add(prepare);
        Add(prepare, "DeviceHubPrepare", async () => { await viewModel.StartAutomationControlAsync(Device().Udid, Device().IsWireless, _stop.Token); });
        var actions = new WrapPanel();
        Add(actions, "DeviceHubStart", async () => Show(await viewModel.StartCoreDeviceCaptureAsync(Device(), _audio.IsChecked == true, _record.IsChecked == true, _stop.Token)));
        Add(actions, "DeviceHubStop", () => viewModel.StopCoreDeviceCaptureAsync(Device()));
        Add(actions, "DeviceHubStatus", async () => Show(await viewModel.DeviceHubUiAsync(Device(), "media_status", null, _stop.Token)));
        Add(actions, "DeviceHubReplay", ReplayAsync);
        var bottom = new ScrollViewer { MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _status };
        DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom); panel.Children.Add(CreateManagementTabs(options, actions));
        Content = surface;
        ThemeService.Attach(this);
        Closed += async (_, _) => { _stop.Cancel(); await StopLogsAsync(); };
    }

    private DeviceViewModel Device() => _devices.SelectedItem as DeviceViewModel ??
        throw new InvalidOperationException(LocalizationService.Get("DeviceHubChooseDevice"));
    private void Show(JsonElement result) => _status.Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    private void Add(Panel panel, string key, Func<Task> action)
    {
        var button = new Button { Margin = new(0, 0, 8, 8), Padding = new(12, 7, 12, 7) };
        button.SetResourceReference(ContentProperty, key); panel.Children.Add(button); _buttons.Add(button);
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; _buttons.ForEach(b => b.IsEnabled = false); _devices.IsEnabled = false;
            try { _status.Text = LocalizationService.Get("DeviceHubWorking"); await action(); }
            catch (OperationCanceledException) { _status.Text = LocalizationService.Get("DeviceHubCancelled"); }
            catch (Exception error) { _status.Text = error.Message; }
            finally { _busy = false; _buttons.ForEach(b => b.IsEnabled = true); _devices.IsEnabled = true; }
        };
    }

    private async Task ReplayAsync()
    {
        var dialog = new OpenFileDialog { Filter = "iPhoneMirror RTP (*.imrtp)|*.imrtp", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var session = await _viewModel.StartMediaReplayAsync(dialog.FileName, cancellation: _stop.Token);
        if (!NativePreviewWindow.TryCreateAndShowForSession(session.Handle, 720, 1280,
            "iPhoneMirror · RTP Replay", 0, 2, () => _viewModel.IsDeviceAudioEnabled(session.Id),
            () => 1, value => _viewModel.SetDeviceAudioEnabled(session.Id, value), () => { },
            _ => { }, () => { }, out var preview))
        {
            await _viewModel.StopDeviceSessionAsync(session.Id, session.Handle);
            throw new InvalidOperationException("The replay preview could not open.");
        }
        preview!.Closed += async (_, _) => await _viewModel.StopDeviceSessionAsync(session.Id, session.Handle);
        _status.Text = dialog.FileName;
    }
}
