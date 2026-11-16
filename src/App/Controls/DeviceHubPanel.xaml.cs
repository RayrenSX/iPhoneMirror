using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;
using Microsoft.Win32;

namespace IPhoneMirror.App.Controls;

internal sealed partial class DeviceHubPanel : UserControl, IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly Func<DeviceViewModel, string, object?, CancellationToken, Task<JsonElement>> _request;
    private readonly Func<DeviceViewModel, bool> _isReady;
    private readonly string _iconCacheDirectory;
    private readonly DispatcherTimer _connectionWatch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource _context = new();
    private CancellationTokenSource? _icons;
    private Task _operation = Task.CompletedTask;
    private Task _iconOperation = Task.CompletedTask;
    private DeviceViewModel? _device;
    private bool _active, _ready, _busy, _quietOperation, _initialized, _disposed;
    private bool _hasConnected, _refreshPending, _refreshQueued;
    private long _generation;
    private int _page = 1;
    private readonly HashSet<int> _loadedPages = [];
    internal event EventHandler? CloseRequested;

    internal DeviceHubPanel(MainViewModel viewModel,
        Func<DeviceViewModel, string, object?, CancellationToken, Task<JsonElement>>? request = null,
        Func<DeviceViewModel, bool>? isReady = null,
        string? iconCacheDirectory = null)
    {
        _viewModel = viewModel;
        _request = request ?? viewModel.DeviceHubUiAsync;
        _isReady = isReady ?? viewModel.IsDeviceHubReady;
        _iconCacheDirectory = iconCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iPhoneMirror", "AppIcons");
        InitializeComponent();
        InitializeMenus();
        CreateControlGroups();
        _logTimer.Tick += OnLogTick;
        _connectionWatch.Tick += OnConnectionWatchTick;
        AppsList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueIcons()));
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _initialized = true;
        AppsTab.IsChecked = true;
        ShowPage();
        UpdateConnection();
    }

    internal void SetDevice(DeviceViewModel? device, bool active)
    {
        if (_disposed) return;
        var ready = IsReady(device);
        var targetChanged = !ReferenceEquals(_device, device);
        var changed = targetChanged || _active != active;
        if (changed || (_ready && !ready))
        {
            if (_device is not null) _device.PropertyChanged -= OnDevicePropertyChanged;
            RenewContext();
            _ = StopLogsAsync(waitForOperation: true);
            if (_ready && !ready) _loadedPages.Clear();
            if (targetChanged)
            {
                ClearDeviceContent();
                _hasConnected = false;
                _refreshPending = false;
            }
            _device = device;
            if (_device is not null) _device.PropertyChanged += OnDevicePropertyChanged;
        }
        var becameReady = !_ready && ready;
        _active = active;
        _ready = ready;
        _hasConnected |= ready;
        if (active && device is not null) _connectionWatch.Start();
        else _connectionWatch.Stop();
        if (active && ready && (targetChanged || becameReady || (changed && !_loadedPages.Contains(_page)))) _refreshPending = true;
        UpdateConnection();
        SchedulePendingRefresh();
        if (active && ready && !_refreshPending && _page == 1) QueueIcons();
    }

    private void OnConnectionWatchTick(object? sender, EventArgs e)
    {
        // Read existing in-process bridge state only. No discovery, connection,
        // capture start, or device action is initiated by this observer.
        if (_active && IsReady(_device) != _ready) SetDevice(_device, true);
    }

    private void SchedulePendingRefresh()
    {
        if (_disposed || !_active || !_ready || _busy || !_refreshPending || _refreshQueued) return;
        _refreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            if (_disposed || !_active || !_ready || _busy || !_refreshPending) return;
            _refreshPending = false;
            _ = RefreshAsync();
        });
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnDevicePropertyChanged(sender, e)); return; }
        if (ReferenceEquals(sender, _device)) SetDevice(_device, _active);
    }

    // QuickTime activation temporarily removes the phone from normal usbmux
    // discovery. Its prepared management bridge is the authoritative connection.
    private bool IsReady(DeviceViewModel? device) => device is { IsMediaCast: false } && _isReady(device);

    private void RenewContext()
    {
        CloseMenus();
        _context.Cancel();
        _context.Dispose();
        _context = new();
        _icons?.Cancel();
        _generation++;
        _busy = false;
        _quietOperation = false;
    }

    private void ClearDeviceContent()
    {
        _loadedPages.Clear();
        _allApps = [];
        _allProcesses = [];
        AppsList.ItemsSource = ProfilesList.ItemsSource = FilesList.ItemsSource = null;
        AppSearch.Clear(); AppCount.Text = "";
        AppsEmpty.Visibility = ProfilesEmpty.Visibility = FilesEmpty.Visibility = Visibility.Collapsed;
        InfoRows.Children.Clear(); DiagnosticRows.Children.Clear();
        FilePath.Text = "/"; _listedFilePath = ""; _fileNext = null;
        _logLines.Clear(); LogText.Clear();
        LogSearch.Clear(); UpdateLogEmptyState();
        StatusText.Text = "";
        ResetSettingValues();
    }

    private void UpdateConnection()
    {
        if (!_initialized) return;
        DeviceNameText.Text = _device?.DisplayName ?? L("PanelNoDevice");
        DeviceDetailText.Text = _device is null ? L("PanelFollowsSelection") : $"{_device.ModelDisplay} · {_device.OsDisplay}";
        ConnectionText.Text = _ready ? L("PanelConnected") : _hasConnected ? L("PanelReconnecting") : "";
        var usable = _device is { IsMediaCast: false } && (_device.Ready || _device.IsWireless);
        PromptTitle.Text = _device is null ? L("PanelNoDevice") :
            _device.IsMediaCast ? L("PanelPairedDevice") : !usable ? L("PanelOffline") : L("PanelConnectTitle");
        PromptDetail.Text = _device is null ? L("PanelChooseFromSidebar") :
            _device.IsMediaCast ? L("PanelPairedHint") : !usable ? L("PanelReconnectHint") : L("PanelConnectHint");
        PrepareButton.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        PrepareButton.IsEnabled = !_busy;
        var showContent = _ready || _hasConnected;
        Pages.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPrompt.Visibility = showContent ? Visibility.Collapsed : Visibility.Visible;
        Pages.IsEnabled = _ready && (!_busy || _quietOperation);
        RecoveryHint.Visibility = _hasConnected && !_ready ? Visibility.Visible : Visibility.Collapsed;
        BusyProgress.Visibility = _busy && !_quietOperation ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommands();
    }

    private void OnConnectionTextSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Localized status text can grow after the header's first layout pass.
        // Reflow the name/status allocation once its final text width is known.
        if (e.WidthChanged && ConnectionText.Parent is FrameworkElement header)
        { header.InvalidateMeasure(); header.InvalidateArrange(); }
    }

    private static string L(string key) => LocalizationService.Get("DeviceHub" + key);
    private static string F(string key, params object?[] args) => LocalizationService.Format("DeviceHub" + key, args);
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p)
        ? p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString() : "";
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    private static string Option(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private Window? Owner => Window.GetWindow(this);
    private bool Confirm(string body) => AppPromptWindow.Confirm(L("Confirm"), body, Owner);

    // Every stage of a multi-step action uses the same device and cancellation scope.
    // A response from a previous device/page must never populate the current panel.
    private sealed record Operation(DeviceHubPanel Panel, DeviceViewModel? Device, long Generation, CancellationToken Token)
    {
        internal void Check()
        {
            Token.ThrowIfCancellationRequested();
            if (Panel._disposed || !Panel._active || Generation != Panel._generation || !ReferenceEquals(Device, Panel._device))
                throw new OperationCanceledException(Token);
        }
        internal async Task<JsonElement> Call(string action, object? arguments = null)
        {
            Check();
            var target = Device ?? throw new InvalidOperationException(L("ChooseDevice"));
            var result = await Panel._request(target, action, arguments, Token);
            Check();
            return result;
        }
    }

    private Task RunAsync(Func<Operation, Task> action, bool quiet = false, bool requiresDevice = true)
    {
        if (_disposed || !_active || (requiresDevice && _device is null)) return Task.CompletedTask;
        if (_busy)
        {
            if (!_quietOperation || quiet) return Task.CompletedTask;
            // A foreground action can interrupt a background log poll.
            RenewContext();
        }
        _icons?.Cancel();
        if (!quiet) CloseMenus();
        var previous = _operation;
        var icons = _iconOperation;
        var operation = new Operation(this, _device, _generation, _context.Token);
        _busy = true;
        _quietOperation = quiet;
        if (!quiet)
        {
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            StatusText.Text = L("Working");
        }
        UpdateConnection();
        return _operation = ExecuteAsync();

        async Task ExecuteAsync()
        {
            try
            {
                // Let cancelled bridge requests release their admission gate first.
                await previous;
                await icons;
                operation.Check();
                await action(operation);
                operation.Check();
                if (!quiet && StatusText.Text == L("Working")) StatusText.Text = L("PanelDone");
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (operation.Generation == _generation && !_disposed)
                {
                    if (quiet) _ = StopLogsAsync(waitForOperation: true);
                    StatusText.Text = FriendlyError(error);
                    StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
                    if (_page == 3) FilePath.Text = _listedFilePath.Length == 0 ? "/" : "/" + _listedFilePath;
                    _viewModel.AddDiagnosticLog("Device management: " + error);
                }
            }
            finally
            {
                if (operation.Generation == _generation && !_disposed)
                {
                    _busy = false;
                    _quietOperation = false;
                    SetDevice(_device, _active);
                    SchedulePendingRefresh();
                    if (_page == 1 && Option(AppFilter) != "processes") QueueIcons();
                }
            }
        }
    }

    private static string FriendlyError(Exception error) => error switch
    {
        AutomationException { Code: "CONTROL_NOT_AVAILABLE" or "SESSION_CHANGED" } => L("PanelConnectionLost"),
        AutomationException { Code: "CONTROL_LOCKED" or "MANAGEMENT_BUSY" } => L("PanelBusyHint"),
        AutomationException { Code: "CAPTURE_ALREADY_ACTIVE" } => L("PanelStopCaptureFirst"),
        AutomationException { Code: "CAPABILITY_NOT_SUPPORTED" or "DEVICE_HUB_UNAVAILABLE" } => L("PanelUnsupported"),
        AutomationException { Code: "DECODER_MISSING" } => L("PanelDecoderMissing"),
        AutomationException { Code: "APP_INSTALL_FAILED" } => L("PanelInstallFailed"),
        AutomationException { Code: "CANCELLED" } => L("Cancelled"),
        _ => L("PanelOperationFailed")
    };

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var page)) return;
        if (_page != page)
        {
            RenewContext();
            if (_page == 4) _ = StopLogsAsync(waitForOperation: true);
            _page = page;
        }
        ShowPage(); UpdateConnection();
        StatusText.Text = "";
        if (_active && _ready && !_loadedPages.Contains(_page)) _ = RefreshAsync();
        else if (_page == 1) QueueIcons();
    }

    private void ShowPage()
    {
        UIElement[] pages = [InfoPage, AppsPage, ProfilesPage, FilesPage, LogsPage, ControlsPage];
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == _page ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task RefreshAsync() => RunAsync(async op =>
    {
        switch (_page)
        {
            case 0: await LoadInfoAsync(op); break;
            case 1: await LoadAppsAsync(op); break;
            case 2: await LoadProfilesAsync(op); break;
            case 3: await BrowseAsync(op, targetPath: _listedFilePath); break;
            case 4: StatusText.Text = L("PanelLogHint"); break;
            case 5: await ControlActionAsync(op, "settings-read"); break;
        }
        _loadedPages.Add(_page);
    });

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        await InvokeActionAsync(action);
    }

    private Task InvokeActionAsync(string action)
    {
        if (action == "refresh") return _ready ? RefreshAsync() : Task.CompletedTask;
        var row = (_page switch { 1 => AppsList, 2 => ProfilesList, 3 => FilesList, _ => null })?.SelectedItem as HubRow;
        return RunAsync(op => ExecuteActionAsync(op, action, row));
    }

    private async Task ExecuteActionAsync(Operation op, string action, HubRow? row = null)
    {
        switch (action)
        {
            case "prepare":
                await _viewModel.StartAutomationControlAsync(op.Device!.Udid, op.Device.IsWireless, op.Token);
                op.Check(); _refreshPending = true; break;
            case "capabilities": RenderDetails(DiagnosticRows, await op.Call("capabilities")); break;
            case "tunnel": RenderDetails(DiagnosticRows, await op.Call("tunnel_info")); break;
            case "app-install": await InstallAsync(op, false); break;
            case "app-launch": await LaunchSelectedAsync(op, row); break;
            case "app-remove": await RemoveAppAsync(op, row); break;
            case "profile-install": await InstallAsync(op, true); break;
            case "profile-remove": await RemoveProfileAsync(op, row); break;
            case "file-parent":
                if (_listedFilePath.Length == 0) { StatusText.Text = ""; break; }
                var parent = _listedFilePath.Contains('/') ? _listedFilePath[.._listedFilePath.LastIndexOf('/')] : "";
                await BrowseAsync(op, targetPath: parent); break;
            case "file-browse": await BrowseAsync(op); break;
            case "file-next": if (_fileNext.HasValue) await BrowseAsync(op, append: true, targetPath: _listedFilePath); break;
            case "file-download": await OpenFileAsync(op, row); break;
            case "file-export": await ExportCrashesAsync(op); break;
            case "log-start": await StartLogsAsync(op); break;
            case "log-stop": await StopLogsAsync(); op.Check(); StatusText.Text = L("PanelLogStopped"); break;
            case "log-save": await SaveLogsAsync(op); break;
            default: await ControlActionAsync(op, action); break;
        }
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e) { if (_initialized) UpdateCommands(); }
    private void UpdateCommands()
    {
        if (!_initialized) return;
        HeaderRefresh.IsEnabled = _ready && (!_busy || _quietOperation);
        HeaderMore.IsEnabled = !_busy || _quietOperation;
        HeaderMore.Visibility = _page == 1 && Option(AppFilter) != "processes" || _page == 2 ||
            _page == 3 && Option(FileRoot) == "crashes" || _page == 4 && LogText.Text.Length > 0 ||
            !_ready && _device is { IsMediaCast: false } ? Visibility.Visible : Visibility.Collapsed;
        FileNext.IsEnabled = _ready && !_busy && _fileNext.HasValue;
        FileNext.Visibility = _fileNext.HasValue ? Visibility.Visible : Visibility.Collapsed;
        FileParent.IsEnabled = _ready && !_busy && _listedFilePath.Length > 0;
        LogStart.IsEnabled = !_busy && _logStream is null;
        LogStop.IsEnabled = (!_busy || _quietOperation) && _logStream is not null;
        LogStart.Visibility = _logStream is null ? Visibility.Visible : Visibility.Collapsed;
        LogStop.Visibility = _logStream is null ? Visibility.Collapsed : Visibility.Visible;
        LogSave.IsEnabled = (!_busy || _quietOperation) && LogText.Text.Length > 0;
        UpdateLogEmptyState();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnLanguageChanged(sender, e)); return; }
        if (_disposed) return;
        CloseMenus();
        UpdateConnection(); ApplyAppFilter(preservePosition: true);
        StatusText.Text = "";
        foreach (var page in new[] { 0, 2, 3 }) _loadedPages.Remove(page);
        if (_active && _ready && !_busy && _page is 0 or 2 or 3) _ = RefreshAsync();
        // Setting choices are localized resources; preserve the actual protocol values.
    }

    private static TextBlock Label(string key, string style = "HubCaption")
    {
        var label = new TextBlock(); label.SetResourceReference(TextBlock.TextProperty, "DeviceHub" + key);
        label.SetResourceReference(StyleProperty, style); return label;
    }

    private sealed class HubRow(string id, string title, string detail, string glyph, JsonElement data) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public string Title { get; private set; } = title;
        public string Detail { get; private set; } = detail;
        public string Glyph { get; private set; } = glyph;
        public JsonElement Data { get; private set; } = data;
        public string Initial { get; } = InitialOf(title);
        public int GroupOrder => InitialOrder(Initial);
        private ImageSource? _icon;
        public ImageSource? Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void UpdateFrom(HubRow row)
        {
            Data = row.Data;
            if (Title != row.Title) { Title = row.Title; PropertyChanged?.Invoke(this, new(nameof(Title))); }
            if (Detail != row.Detail) { Detail = row.Detail; PropertyChanged?.Invoke(this, new(nameof(Detail))); }
            if (Glyph != row.Glyph) { Glyph = row.Glyph; PropertyChanged?.Invoke(this, new(nameof(Glyph))); }
            if (!ReferenceEquals(Icon, row.Icon)) Icon = row.Icon;
        }
    }

    private static int InitialOrder(string initial) => initial == "#" ? 99 : initial[0] - 'A';

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseMenus();
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        if (_device is not null) _device.PropertyChanged -= OnDevicePropertyChanged;
        _context.Cancel(); _icons?.Cancel();
        _connectionWatch.Stop();
        _connectionWatch.Tick -= OnConnectionWatchTick;
        _ = StopLogsAsync(waitForOperation: true);
        _logTimer.Tick -= OnLogTick;
        _context.Dispose();
    }
}
