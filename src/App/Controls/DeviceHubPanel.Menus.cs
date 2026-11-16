using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace IPhoneMirror.App.Controls;

internal sealed partial class DeviceHubPanel
{
    internal Action<string> CopyText { get; set; } = Clipboard.SetText;

    private void InitializeMenus()
    {
        foreach (var list in new[] { AppsList, ProfilesList, FilesList })
        {
            // The menu must exist before the first ContextMenuOpening event.
            list.ContextMenu = NewMenu();
            list.PreviewMouseRightButtonDown += (_, e) => SelectContextRow(list, e.OriginalSource as DependencyObject);
            list.ContextMenuOpening += (_, _) =>
            {
                var menu = PopulateListMenu(list);
                menu.PlacementTarget = list;
                menu.Placement = PlacementMode.MousePoint;
            };
        }
        AppsList.SetResourceReference(ToolTipProperty, "DeviceHubPanelAppHint");
    }

    private void OnRowMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        e.Handled = true;
        foreach (var list in new[] { AppsList, ProfilesList, FilesList })
        {
            if (ItemsControl.ContainerFromElement(list, button) is not ListBoxItem item) continue;
            CloseMenus();
            list.SelectedItem = list.ItemContainerGenerator.ItemFromContainer(item);
            var menu = PopulateListMenu(list);
            menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            break;
        }
    }

    private static bool IsRowActionSource(DependencyObject source)
    {
        for (var current = source; current is not null && current is not ListBoxItem;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }

    private ContextMenu NewMenu()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(StyleProperty, "DeviceContextMenuStyle");
        return menu;
    }

    private void SelectContextRow(ListBox list, DependencyObject? origin)
    {
        // A right click acts on the pointed row, without an extra left click.
        // Blank space must never reuse an earlier selection for removal.
        list.SelectedItem = origin is not null && ItemsControl.ContainerFromElement(list, origin) is ListBoxItem item
            ? list.ItemContainerGenerator.ItemFromContainer(item) : null;
    }

    private ContextMenu PopulateListMenu(ListBox list)
    {
        var menu = list.ContextMenu!;
        menu.Items.Clear();
        var row = list.SelectedItem as HubRow;
        var process = Option(AppFilter) == "processes";
        var generation = _generation;
        var device = _device;
        var page = _page;
        bool Valid() => !_disposed && _active && generation == _generation && ReferenceEquals(device, _device) && page == _page &&
            (row is null || list.Items.Contains(row));
        var available = _ready && (!_busy || _quietOperation);

        void Action(string key, string action, bool enabled)
        {
            AddMenuAction(menu, key, action, available && enabled, async () =>
            {
                if (!Valid() || !_ready) return;
                if (action == "refresh") { await RefreshAsync(); return; }
                await RunAsync(async op =>
                {
                    op.Check();
                    if (Valid() && _ready) await ExecuteActionAsync(op, action, row);
                });
            });
        }
        void Copy(string key)
        {
            AddMenuAction(menu, key, "copy", row is not null, () =>
            {
                if (!Valid() || row is null) return Task.CompletedTask;
                try
                {
                    CopyText(row.Id); StatusText.Text = L("PanelCopied");
                    StatusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                }
                catch
                {
                    StatusText.Text = L("PanelCopyFailed");
                    StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
                }
                return Task.CompletedTask;
            });
        }

        if (list == AppsList)
        {
            if (row is not null)
            {
                if (!process) Action("PanelOpenApp", "app-launch", true);
                Copy(process ? "PanelCopyPid" : "PanelCopyBundleId");
                AddMenuSeparator(menu);
                Action(process ? "Terminate" : "PanelUninstall", "app-remove", true);
                AddMenuSeparator(menu);
            }
            if (!process) Action("PanelInstallApp", "app-install", true);
        }
        else if (list == ProfilesList)
        {
            if (row is not null)
            {
                Copy("PanelCopyIdentifier");
                Action("Remove", "profile-remove", true);
                AddMenuSeparator(menu);
            }
            Action("PanelInstallProfile", "profile-install", true);
        }
        else if (list == FilesList)
        {
            if (row is not null)
            {
                var kind = Text(row.Data, "type");
                Action(kind == "directory" ? "PanelOpenFolder" : "PanelSaveFile", "file-download", kind is "directory" or "file");
                Copy("PanelCopyPath");
                AddMenuSeparator(menu);
            }
            if (Option(FileRoot) == "crashes") Action("ExportCrashes", "file-export", true);
        }
        Action("Refresh", "refresh", true);
        return menu;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        CloseMenus();
        var menu = NewMenu();
        var device = _device;
        var generation = _generation;
        var page = _page;
        var available = _ready && (!_busy || _quietOperation);
        void Action(string key, string action, bool enabled)
        {
            AddMenuAction(menu, key, action, enabled, async () =>
            {
                if (_disposed || !_active || generation != _generation || !ReferenceEquals(device, _device) || page != _page) return;
                await InvokeActionAsync(action);
            });
        }
        if (_page == 1 && Option(AppFilter) != "processes") Action("PanelInstallApp", "app-install", available);
        if (_page == 2) Action("PanelInstallProfile", "profile-install", available);
        if (_page == 3 && Option(FileRoot) == "crashes") Action("ExportCrashes", "file-export", available);
        if (_page == 4) Action("SaveLog", "log-save", available && LogText.Text.Length > 0);
        if (!_ready && _device is { IsMediaCast: false })
        {
            if (menu.Items.Count > 0) AddMenuSeparator(menu);
            Action("Prepare", "prepare", !_busy && (_device.Ready || _device.IsWireless));
        }
        if (menu.Items.Count == 0) return;
        HeaderMore.ContextMenu = menu;
        menu.PlacementTarget = HeaderMore;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static void AddMenuAction(ContextMenu menu, string key, string action, bool enabled, Func<Task> invoke)
    {
        var item = new MenuItem { Tag = action, IsEnabled = enabled };
        item.SetResourceReference(StyleProperty, "DeviceMenuItemStyle");
        item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "DeviceHub" + key);
        item.Click += async (_, e) => { e.Handled = true; if (item.IsEnabled) await invoke(); };
        menu.Items.Add(item);
    }

    private void AddMenuSeparator(ContextMenu menu)
    {
        var separator = new Separator { Style = (Style)FindResource("HubMenuSeparator") };
        menu.Items.Add(separator);
    }

    private void CloseMenus()
    {
        foreach (var menu in new[] { AppsList.ContextMenu, ProfilesList.ContextMenu, FilesList.ContextMenu, HeaderMore.ContextMenu })
            if (menu is not null) menu.IsOpen = false;
    }

    private async void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _page is 1 or 4 && Pages.IsEnabled)
        {
            var search = _page == 1 ? AppSearch : LogSearch;
            search.Focus(); search.SelectAll(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && (AppSearch.IsKeyboardFocusWithin || LogSearch.IsKeyboardFocusWithin))
        {
            var search = AppSearch.IsKeyboardFocusWithin ? AppSearch : LogSearch;
            if (search.Text.Length > 0) { search.Clear(); e.Handled = true; }
        }
        else if (_page == 3 && e.Key == Key.Escape && FilePath.IsKeyboardFocusWithin)
        { FilePath.Text = _listedFilePath.Length == 0 ? "/" : "/" + _listedFilePath; FilesList.Focus(); e.Handled = true; }
        else if (_page == 3 && e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control && Pages.IsEnabled)
        { FilePath.Focus(); FilePath.SelectAll(); e.Handled = true; }
        else if (_page == 3 && _ready && !_busy && _listedFilePath.Length > 0 &&
            ((e.Key == Key.Back && FilesList.IsKeyboardFocusWithin) || (e.SystemKey == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt)))
        { e.Handled = true; await InvokeActionAsync("file-parent"); }
        else if (e.Key == Key.F5)
        {
            e.Handled = true;
            if (_ready) await InvokeActionAsync("refresh");
        }
        else if (e.Key == Key.Enter && _ready && !_busy)
        {
            if (e.OriginalSource is DependencyObject source && IsRowActionSource(source)) return;
            if (_page == 1 && AppsList.IsKeyboardFocusWithin && Option(AppFilter) != "processes")
            { e.Handled = true; await InvokeActionAsync("app-launch"); }
            else if (_page == 3 && FilesList.IsKeyboardFocusWithin)
            { e.Handled = true; await InvokeActionAsync("file-download"); }
        }
    }
}
