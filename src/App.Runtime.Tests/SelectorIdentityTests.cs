using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunSelectorIdentityTests()
    {
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            // Inventory every notifying record in the application assembly. New ones
            // require a fixture rather than silently escaping this regression audit.
            var notifyingRecords = typeof(App).Assembly.GetTypes().Where(type =>
                typeof(INotifyPropertyChanged).IsAssignableFrom(type) &&
                type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null).ToHashSet();
            InteractionAssert(notifyingRecords.SetEquals(new[] { typeof(BluetoothClientInfo), typeof(ControlPromptOption) }),
                $"Update selector fixtures for notifying records: {string.Join(", ", notifyingRecords.Select(type => type.FullName))}");
            var timestamp = DateTimeOffset.UtcNow;
            var fixtures = new (object[] Items, string Label)[]
            {
                ([new BluetoothClientInfo("a", "A", "A1", timestamp), new BluetoothClientInfo("b", "B", "B2", timestamp)], "DisplayName"),
                ([new ControlPromptOption("a", "Continue"), new ControlPromptOption("b", "Cancel")], "DisplayTitle"),
                ([new MirroringControlModeOption(ReverseControlMode.None, "MirroringAutoControlOff"),
                  new MirroringControlModeOption(ReverseControlMode.Usb, "WiredControlLabel")], "Label"),
            };
            foreach (var (items, label) in fixtures)
            {
                var hashes = items.Select(item => item.GetHashCode()).ToArray();
                var lookup = items.ToHashSet();
                var selection = new SelectorSelection { Value = items[0] };
                var list = new ListBox { ItemsSource = items, DisplayMemberPath = label };
                list.SetBinding(ListBox.SelectedItemProperty, new Binding(nameof(SelectorSelection.Value))
                    { Source = selection, Mode = BindingMode.TwoWay });
                var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = label };
                combo.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(SelectorSelection.Value))
                    { Source = selection, Mode = BindingMode.TwoWay });
                var panel = new StackPanel(); panel.Children.Add(list); panel.Children.Add(combo);
                var window = new Window { Content = panel, Width = 450, Height = 250, ShowInTaskbar = false };
                try
                {
                    window.Show(); DrainDispatcher();
                    PropertyChangedEventHandler observer = (_, _) => { };
                    foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
                    {
                        SetAuditCulture(culture);
                        foreach (INotifyPropertyChanged item in items) item.PropertyChanged += observer;
                        for (var index = 0; index < items.Length; index++)
                        {
                            var item = items[index];
                            InteractionAssert(item.GetHashCode() == hashes[index] && lookup.Contains(item),
                                $"{item.GetType().Name}: localization subscriptions changed hash identity.");
                        }
                        foreach (var item in items.Concat(items.Reverse()))
                        {
                            list.SelectedItem = item; DrainDispatcher();
                            InteractionAssert(ReferenceEquals(selection.Value, item) && ReferenceEquals(combo.SelectedItem, item),
                                $"{item.GetType().Name}: ListBox selection did not propagate to the model and ComboBox.");
                            combo.SelectedItem = items.First(candidate => !ReferenceEquals(candidate, item)); DrainDispatcher();
                            InteractionAssert(ReferenceEquals(selection.Value, combo.SelectedItem) && ReferenceEquals(list.SelectedItem, combo.SelectedItem),
                                $"{item.GetType().Name}: ComboBox selection reverted or diverged.");
                        }
                        foreach (INotifyPropertyChanged item in items) item.PropertyChanged -= observer;
                    }
                    if (items[0] is BluetoothClientInfo client)
                        InteractionAssert(client.Equals(client with { }) && !client.Equals(client with { BoundDeviceName = "Other" }),
                            "Bluetooth client value equality lost binding metadata.");
                    if (items[0] is ControlPromptOption option)
                        InteractionAssert(option.Equals(option with { }) && !option.Equals(option with { IsEnabled = false }),
                            "Prompt value equality lost availability data.");
                }
                finally { window.Close(); }
            }
            TestBluetoothBindingSelector();
            TestControlPromptSelector();
            Console.WriteLine("PASS selector identities: all notifying records inventoried; real ListBox/ComboBox two-way selection, subscription/hash stability, five languages and value equality.");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void TestBluetoothBindingSelector()
    {
        var clients = new[]
        {
            new BluetoothClientInfo("a", "A", "A1", DateTimeOffset.UtcNow),
            new BluetoothClientInfo("b", "B", "B2", DateTimeOffset.UtcNow),
            new BluetoothClientInfo("bound", "Bound", "B3", DateTimeOffset.UtcNow, "Other phone"),
        };
        var owner = new Wpf.Ui.Controls.FluentWindow { Width = 200, Height = 150, ShowInTaskbar = false };
        owner.Show();
        var window = (Windows.BluetoothClientBindingWindow)typeof(Windows.BluetoothClientBindingWindow)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke(
                [owner, "Selector audit", clients, "a",
                 (Func<Task<IReadOnlyList<BluetoothClientInfo>>>)(() => Task.FromResult<IReadOnlyList<BluetoothClientInfo>>(clients)),
                 (Func<string, bool>)(_ => false)]);
        try
        {
            window.Show(); DrainDispatcher();
            var list = FindVisualDescendants<ListBox>(window).Single(item => ReferenceEquals(item.ItemsSource, window.Clients));
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                SetAuditCulture(culture); DrainDispatcher();
                foreach (var client in clients.Take(2).Concat(clients.Take(2).Reverse()))
                {
                    list.SelectedItem = client; DrainDispatcher();
                    InteractionAssert(ReferenceEquals(window.SelectedClient, client) && window.CanConfirm,
                        "Bluetooth binding selection reverted or its confirmation state became stale.");
                }
                window.SelectedClient = clients[2]; DrainDispatcher();
                InteractionAssert(ReferenceEquals(list.SelectedItem, clients[2]) && !window.CanConfirm,
                    "An already bound client lost its disabled confirmation state.");
            }
        }
        finally { window.Close(); owner.Close(); }
        Console.WriteLine("PASS actual Bluetooth binding window: repeated selection, five language changes, model synchronization and confirmation availability.");
    }

    private static void TestControlPromptSelector()
    {
        var options = new[] { new ControlPromptOption("a", "Continue"), new ControlPromptOption("b", "Cancel"),
            new ControlPromptOption("disabled", "Unavailable", IsEnabled: false) };
        var service = new ControlStatusService();
        service.Begin(ControlStatusMode.Bluetooth, "Selector audit");
        var result = service.RequestPromptAsync(new(ControlPromptType.Selection, "Choose", "Choose a client",
            "Continue", Options: options));
        var vm = new Windows.ReverseControlStatusViewModel(service);
        var list = new ListBox { ItemsSource = vm.PromptOptions, DisplayMemberPath = "DisplayTitle", DataContext = vm };
        list.SetBinding(ListBox.SelectedItemProperty, new Binding(nameof(vm.SelectedPromptOption)) { Mode = BindingMode.TwoWay });
        var window = new Window { Content = list, Width = 400, Height = 220, ShowInTaskbar = false };
        try
        {
            window.Show(); DrainDispatcher();
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                SetAuditCulture(culture); DrainDispatcher();
                foreach (var option in options.Concat(options.Reverse()))
                {
                    list.SelectedItem = option; DrainDispatcher();
                    InteractionAssert(ReferenceEquals(vm.SelectedPromptOption, option) && vm.IsPromptPrimaryEnabled == option.IsEnabled,
                        "Control prompt selection reverted or retained a stale primary-button state.");
                }
            }
            list.SelectedItem = options[1]; DrainDispatcher();
            vm.ResolvePrompt(ControlPromptAction.Primary);
            InteractionAssert(result.IsCompletedSuccessfully && result.Result.Value == "b",
                "Control prompt confirmed a different choice than the visible selection.");
        }
        finally { service.ResolvePrompt(new(ControlPromptAction.Cancel)); vm.Dispose(); window.Close(); }
        Console.WriteLine("PASS control prompt: real selection binding, five languages, enabled state and exact confirmed choice.");
    }

    private sealed class SelectorSelection : INotifyPropertyChanged
    {
        private object? _value;
        public object? Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
