using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class BluetoothClientBindingWindow : Wpf.Ui.Controls.FluentWindow,
    INotifyPropertyChanged
{
    private BluetoothClientInfo? _selectedClient;
    private readonly Func<string, bool> _unbind;
    private readonly Func<Task<IReadOnlyList<BluetoothClientInfo>>> _refresh;
    private readonly string? _suggestedId;
    private readonly TaskCompletionSource<string?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isRefreshing;
    private bool _resultCompleted;

    public ObservableCollection<BluetoothClientInfo> Clients { get; } = [];
    public string TargetText { get; }
    public BluetoothClientInfo? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (ReferenceEquals(_selectedClient, value)) return;
            _selectedClient = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirm));
        }
    }
    public bool CanConfirm => SelectedClient?.CanBind == true;
    public bool CanRefresh => !_isRefreshing;

    private BluetoothClientBindingWindow(Window owner, string targetName,
        IReadOnlyList<BluetoothClientInfo> clients, string? suggestedId,
        Func<Task<IReadOnlyList<BluetoothClientInfo>>> refresh,
        Func<string, bool> unbind)
    {
        _unbind = unbind;
        _refresh = refresh;
        _suggestedId = suggestedId;
        TargetText = LocalizationService.Format("BluetoothClientBindingTargetFormat",
            targetName);
        ReplaceClients(clients, suggestedId);
        Owner = owner;
        DataContext = this;
        InitializeComponent();
    }

    internal static string? Show(Window owner, string targetName,
        IReadOnlyList<BluetoothClientInfo> clients, string? suggestedId,
        Func<Task<IReadOnlyList<BluetoothClientInfo>>> refresh,
        Func<string, bool> unbind)
    {
        var window = new BluetoothClientBindingWindow(owner, targetName, clients, suggestedId,
            refresh, unbind);
        return window.ShowDialog() == true ? window.SelectedClient?.Id : null;
    }

    internal static void ShowDeveloperPreview(Window owner) =>
        new BluetoothClientBindingWindow(owner, "iPhone", [], null,
            () => Task.FromResult<IReadOnlyList<BluetoothClientInfo>>([]), _ => false).Show();

    // The Bluetooth control startup path must not enter a nested modal
    // dispatcher loop. Keep the window modeless and let callers await the
    // user's choice without blocking the main window.
    internal static Task<string?> ShowAsync(Window owner, string targetName,
        IReadOnlyList<BluetoothClientInfo> clients, string? suggestedId,
        Func<Task<IReadOnlyList<BluetoothClientInfo>>> refresh,
        Func<string, bool> unbind)
    {
        var window = new BluetoothClientBindingWindow(owner, targetName, clients,
            suggestedId, refresh, unbind);
        window.Closed += window.OnClosed;
        window.Show();
        return window._result.Task;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (!CanConfirm) return;
        CompleteResult(SelectedClient?.Id);
        if (IsVisible) Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CompleteResult(null);
        if (IsVisible) Close();
    }

    private void OnClosed(object? sender, EventArgs e) => CompleteResult(null);

    private void CompleteResult(string? value)
    {
        if (_resultCompleted) return;
        _resultCompleted = true;
        _result.TrySetResult(value);
    }

    private void OnUnbindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BluetoothClientInfo client } ||
            !_unbind(client.Id)) return;
        var index = Clients.IndexOf(client);
        if (index < 0) return;
        var replacement = client with { BoundDeviceName = null };
        Clients[index] = replacement;
        if (ReferenceEquals(SelectedClient, client)) SelectedClient = null;
        e.Handled = true;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing) return;
        var selectedId = SelectedClient?.Id;
        _isRefreshing = true;
        OnPropertyChanged(nameof(CanRefresh));
        try
        {
            var clients = await _refresh();
            if (IsLoaded) ReplaceClients(clients, selectedId ?? _suggestedId);
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("bluetooth", "binding_client_refresh_failed", error);
        }
        finally
        {
            _isRefreshing = false;
            OnPropertyChanged(nameof(CanRefresh));
        }
    }

    private void ReplaceClients(IReadOnlyList<BluetoothClientInfo> clients,
        string? preferredId)
    {
        Clients.Clear();
        foreach (var client in clients) Clients.Add(client);
        SelectedClient = Clients.FirstOrDefault(client => client.CanBind &&
            string.Equals(client.Id, preferredId, StringComparison.OrdinalIgnoreCase))
            ?? (Clients.Count(client => client.CanBind) == 1
                ? Clients.First(client => client.CanBind) : null);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
