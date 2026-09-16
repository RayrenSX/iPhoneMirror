using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

internal sealed class ReverseControlStatusViewModel : INotifyPropertyChanged
{
    private readonly ControlStatusService _service;
    private readonly DispatcherTimer _timer;
    private ControlStatusSnapshot? _snapshot;
    private int _remaining = 5;
    private bool _details;
    internal ObservableCollection<ControlStageItem> Stages { get; } = [];
    internal ObservableCollection<string> Diagnostics { get; } = [];
    public string DeviceSummary => $"{_snapshot?.DeviceName ?? "iPhone"} · {_snapshot?.Mode switch { ControlStatusMode.Usb => "USB", ControlStatusMode.Wireless => "无线", ControlStatusMode.Bluetooth => "蓝牙", _ => "" }}";
    public string StageTitle => _snapshot?.Stage switch { ControlStage.CheckingDevice => "检查设备", ControlStage.CheckingPermissions => "检查设备权限", ControlStage.PreparingDeviceSupport => "准备设备支持文件", ControlStage.Connecting => "建立控制连接", ControlStage.InitializingServices => "初始化控制服务", ControlStage.StartingInputRouter => "启动输入控制", ControlStage.Ready => "控制已连接", ControlStage.Recovering => "正在恢复控制连接", ControlStage.Failed => "无法启用反向控制", _ => "反向控制" };
    public string StageDescription => _snapshot?.Error ?? _snapshot?.Description ?? "正在准备控制连接…";
    public string CountdownText => _snapshot?.Stage == ControlStage.Ready ? $"窗口将在 {_remaining} 秒后关闭" : string.Empty;
    public Visibility CancelVisibility => _snapshot is { CanCancel: true, IsTerminal: false } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CloseVisibility => _snapshot is { IsTerminal: true } ? Visibility.Visible : Visibility.Collapsed;
    public bool IsDetailsExpanded { get => _details; set { _details = value; OnPropertyChanged(); } }
    internal Action? CancelRequested { get; set; }
    internal event Action? AutoCloseRequested;
    internal ReverseControlStatusViewModel(ControlStatusService service)
    {
        _service = service; _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; _timer.Tick += OnTick;
        service.StatusChanged += OnStatusChanged; Apply(service.Current);
    }
    private void OnStatusChanged(object? sender, ControlStatusSnapshot snapshot) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(snapshot));
    private void Apply(ControlStatusSnapshot? snapshot)
    {
        if (snapshot is null) return; _snapshot = snapshot; _remaining = 5;
        Stages.Clear();
        foreach (var stage in Enum.GetValues<ControlStage>().Where(s => s <= ControlStage.StartingInputRouter))
            Stages.Add(new(stage, snapshot.Stage, _service));
        Diagnostics.Clear(); foreach (var item in _service.Diagnostics) Diagnostics.Add($"[{item.Timestamp:HH:mm:ss.fff}] {item.Message}");
        if (snapshot.Stage == ControlStage.Ready) { _timer.Stop(); _timer.Start(); } else _timer.Stop();
        NotifyAll();
    }
    private void OnTick(object? sender, EventArgs e) { if (--_remaining <= 0) { _timer.Stop(); AutoCloseRequested?.Invoke(); } OnPropertyChanged(nameof(CountdownText)); }
    internal void Dispose() { _timer.Stop(); _service.StatusChanged -= OnStatusChanged; }
    private void NotifyAll() { foreach (var p in new[] { nameof(DeviceSummary), nameof(StageTitle), nameof(StageDescription), nameof(CountdownText), nameof(CancelVisibility), nameof(CloseVisibility) }) OnPropertyChanged(p); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

internal sealed class ControlStageItem(ControlStage stage, ControlStage current, ControlStatusService service)
{
    public string Title => stage switch { ControlStage.CheckingDevice => "检查设备", ControlStage.CheckingPermissions => "检查设备权限", ControlStage.PreparingDeviceSupport => "准备设备支持文件", ControlStage.Connecting => "建立控制连接", ControlStage.InitializingServices => "初始化控制服务", ControlStage.StartingInputRouter => "启动输入控制", _ => stage.ToString() };
    public string Marker => stage < current ? "✓" : stage == current ? "●" : "○";
    public Brush MarkerBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(stage < current ? "#2E9B63" : stage == current ? "#3478F6" : "#8A8A8A"));
    public string DurationText => service.GetDuration(stage) is { } value
        ? $"{value.TotalSeconds:0.0}s" : stage < current ? "0.0s" : stage == current ? "…" : string.Empty;
}

public partial class ReverseControlStatusWindow : Wpf.Ui.Controls.FluentWindow
{
    private static ReverseControlStatusWindow? _active;
    private readonly ReverseControlStatusViewModel _viewModel;
    private ReverseControlStatusWindow(Window owner, ControlStatusService service, Action? cancel)
    {
        Owner = owner; _viewModel = new(service) { CancelRequested = cancel }; _viewModel.AutoCloseRequested += Close; DataContext = _viewModel; InitializeComponent();
        Closed += (_, _) => { _viewModel.Dispose(); if (ReferenceEquals(_active, this)) _active = null; };
    }
    internal static void Show(Window owner, ControlStatusService service, Action? cancel = null)
    {
        if (_active is { IsVisible: true }) return;
        _active = new(owner, service, cancel); _active.Show(); _active.Activate();
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnCancelClick(object sender, RoutedEventArgs e) { _viewModel.CancelRequested?.Invoke(); Close(); }
}
