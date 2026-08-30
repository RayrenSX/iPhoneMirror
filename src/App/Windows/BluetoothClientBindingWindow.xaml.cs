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
    private bool _isRefreshing;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (CanConfirm) DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

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
