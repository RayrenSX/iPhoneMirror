using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Windows;
using Microsoft.Win32;

namespace IPhoneMirror.App.Controls;

internal sealed partial class DeviceHubPanel
{
    private HubRow[] _allApps = [];
    private HubRow[] _allProcesses = [];
    private bool _showingProcesses;
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingIcons = [];
    private bool _iconsQueued;
    private bool _iconRefreshPending;
    private static readonly string[] PinyinBoundaries = ["阿", "八", "擦", "大", "额", "发", "嘎", "哈", "几", "卡", "拉", "妈", "拿", "哦", "趴", "七", "然", "撒", "塌", "挖", "西", "丫", "杂"];
    private static readonly CompareInfo PinyinComparison = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

    private async Task LoadAppsAsync(Operation op)
    {
        if (Option(AppFilter) == "processes")
        {
            var result = await op.Call("list_processes");
            _allProcesses = result.GetProperty("processes").EnumerateArray().Select(p =>
            {
                var id = Text(p, "processIdentifier");
                var executable = p.TryGetProperty("executableURL", out var url) ? FindString(url, "relative", "path", "_0") : "";
                var name = Text(p, "name");
                if (name.Length == 0) name = executable.Split('/').LastOrDefault() ?? "";
                return new HubRow(id, name.Length > 0 ? name : "PID " + id, "PID " + id + (executable.Length > 0 ? " · " + executable : ""), "≡", p.Clone());
            }).OrderBy(r => r.GroupOrder).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
            ApplyAppFilter(preservePosition: true);
            StatusText.Text = "";
            return;
        }
        var hidden = IncludeHidden.IsChecked == true;
        _missingIcons.Clear();
        var apps = await op.Call("apps", new { includeHidden = hidden, includeInternal = hidden, includeClips = hidden || Option(AppFilter) == "clips" });
        _allApps = apps.GetProperty("apps").EnumerateArray().Select(a =>
        {
            var id = Text(a, "bundleId"); var name = Text(a, "name");
            var row = new HubRow(id, name.Length > 0 ? name : id,
                string.Join(" · ", new[] { id, Text(a, "version") }.Where(s => s.Length > 0)),
                InitialOf(name.Length > 0 ? name : id), a.Clone());
            if (_iconCache.TryGetValue(IconKey(op.Device!, row), out var icon)) row.Icon = icon;
            return row;
        }).OrderBy(r => r.GroupOrder).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ApplyAppFilter(preservePosition: true);
        StatusText.Text = "";
    }

    private void ApplyAppFilter(bool preservePosition = false)
    {
        if (!_initialized) return;
        var search = AppSearch.Text.Trim();
        var filter = Option(AppFilter);
        var processes = filter == "processes";
        var source = processes ? _allProcesses : _allApps;
        var rows = source.Where(app =>
            (search.Length == 0 || app.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) || app.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (processes && app.Detail.Contains(search, StringComparison.OrdinalIgnoreCase))) &&
            (filter switch
            {
                "user" => !Flag(app.Data, "isFirstParty"), "system" => Flag(app.Data, "isFirstParty"),
                "developer" => Flag(app.Data, "isDeveloper"), "clips" => Flag(app.Data, "isAppClip"), _ => true
            })).ToArray();
        ReplaceRows(AppsList, rows, preservePosition: preservePosition);
        AppCount.Text = processes ? F("PanelFilteredProcessCount", rows.Length, source.Length) : F("PanelAppCount", rows.Length, source.Length);
        AppsEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsEmptyTitle.SetResourceReference(TextBlock.TextProperty, processes ? "DeviceHubPanelNoProcesses" : "DeviceHubPanelNoResults");
        AppsEmptyHint.SetResourceReference(TextBlock.TextProperty, "DeviceHubPanelSearchEmptyHint");
        ResetAppFilters.Visibility = search.Length > 0 || filter is not ("all" or "processes") ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommands();
        if (!_busy) QueueIcons();
    }

    private void ReplaceRows(ListBox list, HubRow[] rows, string? preferredId = null, bool preservePosition = true)
    {
        if (list == AppsList && list.Items.Count == rows.Length)
        {
            var current = list.Items.Cast<HubRow>().ToArray();
            if (current.Zip(rows).All(pair => pair.First.Id == pair.Second.Id && pair.First.Initial == pair.Second.Initial))
            {
                // A refresh usually returns the same order. Keep its realized
                // groups and scroll geometry while replacing current metadata.
                for (var i = 0; i < rows.Length; i++) current[i].UpdateFrom(rows[i]);
                if (!preservePosition) FindScrollViewer(list)?.ScrollToTop();
                return;
            }
        }
        var selectedId = preferredId ?? (preservePosition ? (list.SelectedItem as HubRow)?.Id : null);
        var scroll = FindScrollViewer(list);
        var offset = preservePosition ? scroll?.VerticalOffset ?? 0 : 0;
        var anchor = preservePosition && list == AppsList ? list.Items.Cast<HubRow>().Select(row =>
            (Row: row, Item: list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem))
            .Where(pair => pair.Item is not null && pair.Item.IsVisible)
            .Select(pair => (pair.Row.Id, Top: pair.Item!.TranslatePoint(new Point(), list).Y))
            .Where(pair => pair.Top >= -60 && pair.Top < list.ActualHeight).OrderBy(pair => pair.Top).FirstOrDefault() : default;
        var generation = _generation;
        IEnumerable source = list == AppsList ? CreateGroupedRows(rows) : rows;
        list.ItemsSource = source;
        list.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId);
        // Item containers are regenerated asynchronously. Restore only this result,
        // never the list from a later search, page or device.
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || generation != _generation || !ReferenceEquals(list.ItemsSource, source)) return;
            list.UpdateLayout();
            var viewer = FindScrollViewer(list);
            viewer?.ScrollToVerticalOffset(offset);
            if (preservePosition && anchor.Id is not null && rows.FirstOrDefault(row => row.Id == anchor.Id) is { } anchorRow)
            {
                list.ScrollIntoView(anchorRow); list.UpdateLayout();
                if (viewer is not null && list.ItemContainerGenerator.ContainerFromItem(anchorRow) is ListBoxItem item)
                {
                    var currentTop = item.TranslatePoint(new Point(), list).Y;
                    viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset + currentTop - anchor.Top));
                }
            }
            if (!preservePosition && list.SelectedItem is { } selected) list.ScrollIntoView(selected);
        }, DispatcherPriority.ContextIdle);
    }
    private static ICollectionView CreateGroupedRows(HubRow[] rows)
    {
        var view = new ListCollectionView((IList)rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HubRow.Initial)));
        return view;
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private void OnAppSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        AppSearchHint.Visibility = AppSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppSearchClear.Visibility = AppSearch.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyAppFilter();
    }
    private async void OnAppFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        var processes = Option(AppFilter) == "processes";
        IncludeHidden.Visibility = processes ? Visibility.Collapsed : Visibility.Visible;
        var searchKey = processes ? "DeviceHubPanelSearchProcesses" : "DeviceHubPanelSearchApps";
        AppSearchHint.SetResourceReference(TextBlock.TextProperty, searchKey);
        AppSearch.SetResourceReference(ToolTipProperty, searchKey);
        AppSearch.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, searchKey);
        var changedKind = _showingProcesses != processes;
        _showingProcesses = processes;
        _icons?.Cancel();
        RenewContext();
        StatusText.Text = "";
        // Never present an application row as a process while its new list loads.
        if (changedKind)
        {
            if (processes) _allProcesses = [];
            AppSearch.Clear(); AppsList.ItemsSource = null; AppCount.Text = ""; AppsEmpty.Visibility = Visibility.Collapsed;
        }
        UpdateConnection();
        if ((processes || Option(AppFilter) == "clips") && _active && _ready) await RefreshAsync();
        else if (_allApps.Length == 0 && _active && _ready) await RefreshAsync();
        else ApplyAppFilter();
    }
    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        var search = sender is Button { Tag: "logs" } ? LogSearch : AppSearch;
        search.Clear(); search.Focus(); e.Handled = true;
    }
    private void OnResetAppFiltersClick(object sender, RoutedEventArgs e)
    {
        AppSearch.Clear();
        if (Option(AppFilter) != "processes") AppFilter.SelectedIndex = 0;
        AppSearch.Focus();
    }
    private async void OnIncludeHiddenChanged(object sender, RoutedEventArgs e)
    { if (_initialized && _active && _ready) await RefreshAsync(); }
    private async void OnAppDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Option(AppFilter) == "processes" || e.OriginalSource is not DependencyObject origin || IsRowActionSource(origin) ||
            ItemsControl.ContainerFromElement(AppsList, origin) is not ListBoxItem) return;
        e.Handled = true;
        await InvokeActionAsync("app-launch");
    }
    private async Task LaunchSelectedAsync(Operation op, HubRow? app)
    {
        if (app is null || !_ready || Option(AppFilter) == "processes") return;
        await op.Call("launch", new { bundleId = app.Id });
        StatusText.Text = F("PanelLaunched", app.Title);
    }
    private async Task RemoveAppAsync(Operation op, HubRow? app)
    {
        if (app is null || !_ready) return;
        var process = Option(AppFilter) == "processes";
        if (!Confirm((process ? L("Terminate") : L("UninstallWarning")) + "\n\n" + app.Title + "\n" + app.Id))
        { StatusText.Text = ""; return; }
        op.Check();
        if (process) await op.Call("terminate_app", new { pid = int.Parse(app.Id, CultureInfo.InvariantCulture), signal = 15 });
        else await op.Call("uninstall_app", new { bundleId = app.Id });
        await LoadAppsAsync(op);
    }
    private async Task InstallAsync(Operation op, bool profile)
    {
        var dialog = new OpenFileDialog
        {
            Filter = profile ? "iOS profile|*.mobileprovision;*.mobileconfig" : "iOS app (*.ipa)|*.ipa", CheckFileExists = true
        };
        if (dialog.ShowDialog(Owner) != true) { StatusText.Text = ""; return; }
        op.Check();
        await op.Call(profile ? "install_profile" : "install_app", new { path = dialog.FileName });
        if (profile) await LoadProfilesAsync(op); else await LoadAppsAsync(op);
    }

    private static string IconKey(DeviceViewModel device, HubRow app) => device.Udid + ":" + app.Id + ":" + Text(app.Data, "version");
    private void QueueIcons()
    {
        if (_disposed || !_active || !_ready || _busy || _iconsQueued || _page != 1 || Option(AppFilter) == "processes" || _device is null) return;
        if (!_iconOperation.IsCompleted) { _iconRefreshPending = true; return; }
        _iconRefreshPending = false;
        var device = _device; var generation = _generation;
        _icons?.Dispose(); _icons = CancellationTokenSource.CreateLinkedTokenSource(_context.Token);
        var token = _icons.Token;
        // Only fetch visible rows. Scrolling schedules another bounded batch.
        _iconsQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _iconsQueued = false;
            if (token.IsCancellationRequested || _busy || generation != _generation) return;
            var visible = AppsList.Items.Cast<HubRow>().Where(row =>
                AppsList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item && item.IsVisible &&
                item.TranslatePoint(new Point(), AppsList).Y is var y && y >= -60 && y < AppsList.ActualHeight).Take(16).ToArray();
            if (visible.Length == 0) visible = AppsList.Items.Cast<HubRow>().Take(10).ToArray();
            _iconOperation = FetchIconsAsync(visible);
            _ = ResumeIconsAsync(_iconOperation);
        }, DispatcherPriority.Background);

        async Task ResumeIconsAsync(Task pending)
        {
            await pending;
            if (_iconRefreshPending && !_disposed)
                _ = Dispatcher.BeginInvoke(QueueIcons, DispatcherPriority.Background);
        }

        async Task FetchIconsAsync(HubRow[] rows)
        {
            using var concurrency = new SemaphoreSlim(4, 4);
            using var remote = new SemaphoreSlim(1, 1);
            await Task.WhenAll(rows.Select(async row =>
            {
                var entered = false;
                try
                {
                    await concurrency.WaitAsync(token); entered = true;
                    await FetchIconAsync(row);
                }
                catch (OperationCanceledException) { }
                finally { if (entered) concurrency.Release(); }
            }));

            async Task FetchIconAsync(HubRow row)
            {
                if (token.IsCancellationRequested || _busy || generation != _generation) return;
                var key = IconKey(device, row);
                if (row.Icon is not null || _missingIcons.Contains(key)) return;
                try
                {
                    var bitmap = await TryLoadCachedIconAsync(key, token);
                    if (bitmap is null)
                    {
                        // The management bridge admits one request at a time.
                        // Disk reads may overlap; phone requests keep that contract.
                        await remote.WaitAsync(token);
                        try
                        {
                            var response = await _request(device, "get_app_icon", new { bundleId = row.Id }, token);
                            token.ThrowIfCancellationRequested();
                            var encoded = Text(response, "data");
                            if (encoded.Length > 3 * 1024 * 1024) throw new InvalidDataException("Oversized app icon.");
                            bitmap = await Task.Run(() => DecodeIcon(Convert.FromBase64String(encoded)), token);
                        }
                        finally { remote.Release(); }
                        await SaveCachedIconAsync(key, bitmap, token);
                    }
                    token.ThrowIfCancellationRequested();
                    if (generation != _generation) return;
                    if (_iconCache.Count >= 256) _iconCache.Remove(_iconCache.Keys.First());
                    _iconCache[key] = bitmap;
                    await Dispatcher.InvokeAsync(() => { if (generation == _generation) row.Icon = bitmap; });
                }
                catch (OperationCanceledException) { }
                catch { if (!token.IsCancellationRequested) _missingIcons.Add(key); }
            }
        }
    }

    private async Task<ImageSource?> TryLoadCachedIconAsync(string key, CancellationToken token)
    {
        var path = IconCachePath(key);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, token);
            if (bytes.Length == 0 || bytes.Length > 512 * 1024) return null;
            return await Task.Run(() => DecodeIcon(bytes), token);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task SaveCachedIconAsync(string key, ImageSource bitmap, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_iconCacheDirectory);
            var bytes = await Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create((BitmapSource)bitmap));
                using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
            }, token);
            var path = IconCachePath(key); temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* A read-only cache must never delay the app list. */ }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch { } }
    }

    private string IconCachePath(string key) => Path.Combine(_iconCacheDirectory,
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".png");

    private static ImageSource DecodeIcon(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 64; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }

    private static string InitialOf(string value)
    {
        var first = value.Normalize(System.Text.NormalizationForm.FormKD).Trim().FirstOrDefault();
        if (first == '\0') return "#";
        if (char.IsLetter(first) && first <= 0x7F) return char.ToUpperInvariant(first).ToString();
        if (char.IsDigit(first)) return "#";
        if (first is < '\u3400' or > '\u9FFF') return "#";
        var initials = "ABCDEFGHJKLMNOPQRSTWXYZ";
        var index = 0;
        for (var i = 1; i < PinyinBoundaries.Length; i++)
        {
            if (PinyinComparison.Compare(first.ToString(), PinyinBoundaries[i], CompareOptions.IgnoreCase) < 0) break;
            index = i;
        }
        return initials[index].ToString();
    }

    private async Task LoadProfilesAsync(Operation op)
    {
        var result = await op.Call("list_profiles");
        var rows = result.GetProperty("profiles").EnumerateArray().Select(p =>
            new HubRow(Text(p, "id"), Text(p, "name"), string.Join(" · ", new[] {
                Text(p, "kind") == "provisioning" ? L("PanelProvisioning") : L("PanelConfiguration"), Text(p, "organization"), Text(p, "expires"), Text(p, "id")
            }.Where(s => s.Length > 0)), "▤", p.Clone())).ToArray();
        ReplaceRows(ProfilesList, rows);
        ProfilesEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var partial = result.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object && errors.EnumerateObject().Any();
        ProfilesEmptyTitle.SetResourceReference(TextBlock.TextProperty, "DeviceHub" + (partial ? "PanelProfileReadFailed" : "PanelNoProfiles"));
        ProfilesEmptyHint.SetResourceReference(TextBlock.TextProperty, "DeviceHub" + (partial ? "PanelRefreshHint" : "PanelProfilesEmptyHint"));
        ProfileEmptyInstall.Visibility = partial ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = partial ? L("PanelPartialProfiles") : F("PanelProfileCount", ProfilesList.Items.Count);
    }
    private async Task RemoveProfileAsync(Operation op, HubRow? row)
    {
        if (row is null || !_ready) return;
        if (!Confirm(L("Remove") + "\n\n" + row.Title + "\n" + row.Id)) { StatusText.Text = ""; return; }
        op.Check();
        await op.Call("remove_profile", new { kind = Text(row.Data, "kind"), id = row.Id });
        await LoadProfilesAsync(op);
    }

    private async Task LoadInfoAsync(Operation op)
    {
        var result = await op.Call("device_info");
        InfoRows.Children.Clear();
        foreach (var (domain, title, fields) in new[]
        {
            ("device", "PanelDeviceDetails", new[] { "DeviceName", "ProductType", "ProductVersion", "BuildVersion", "SerialNumber", "UniqueDeviceID" }),
            ("battery", "PanelBattery", new[] { "BatteryCurrentCapacity", "BatteryIsCharging", "BatteryIsFullyCharged", "ExternalConnected" }),
            ("disk", "PanelStorage", new[] { "TotalDiskCapacity", "TotalDataCapacity", "TotalDataAvailable" })
        })
        {
            InfoRows.Children.Add(Label(title, "HubHeading"));
            if (!result.TryGetProperty(domain, out var group) || !Flag(group, "available") || !group.TryGetProperty("values", out var values))
            { InfoRows.Children.Add(Label("PanelUnavailable")); continue; }
            foreach (var key in fields)
            {
                if (!values.TryGetProperty(key, out var value)) continue;
                var display = FormatValue(value);
                if (key is "TotalDiskCapacity" or "TotalDataCapacity" or "TotalDataAvailable" && value.TryGetInt64(out var bytes)) display = FormatBytes(bytes);
                if (key == "BatteryCurrentCapacity") display += "%";
                AddDetail(InfoRows, "PanelField_" + key, display, localized: true);
            }
        }
        StatusText.Text = L("PanelInfoHint");
    }
    private static string FormatBytes(long size) => size >= 1024L * 1024 * 1024 ? (size / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " GB" :
        size >= 1024 * 1024 ? (size / (1024d * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB" :
        size >= 1024 ? (size / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " KB" : size + " B";
    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => L("PanelYes"), JsonValueKind.False => L("PanelNo"),
        JsonValueKind.Null or JsonValueKind.Undefined => "—", JsonValueKind.Object or JsonValueKind.Array => "…", _ => value.ToString()
    };
    private static string FindString(JsonElement value, params string[] keys)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var key in keys) if (value.TryGetProperty(key, out var child))
            { var found = FindString(child, keys); if (found.Length > 0) return found; }
        return "";
    }
    private static void AddDetail(StackPanel panel, string key, string value, bool localized = false)
    {
        var grid = new Grid { Margin = new(0, 0, 0, 9) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(128) }); grid.ColumnDefinitions.Add(new());
        var label = localized ? Label(key) : new TextBlock { Text = key, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var text = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new(0), Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap, Padding = new(0), MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Top };
        text.SetResourceReference(ForegroundProperty, "TextBrush"); Grid.SetColumn(text, 1);
        grid.Children.Add(label); grid.Children.Add(text); panel.Children.Add(grid);
    }
    private static void RenderDetails(StackPanel panel, JsonElement result)
    {
        panel.Children.Clear();
        var count = 0;
        void Add(JsonElement value, string prefix, int depth)
        {
            if (count >= 80) return;
            if (value.ValueKind == JsonValueKind.Object && depth < 4)
                foreach (var p in value.EnumerateObject()) Add(p.Value, prefix.Length == 0 ? p.Name : prefix + " · " + p.Name, depth + 1);
            else if (value.ValueKind == JsonValueKind.Array && depth < 4)
                foreach (var p in value.EnumerateArray().Take(12)) Add(p, prefix, depth + 1);
            else { AddDetail(panel, prefix, FormatValue(value)); count++; }
        }
        Add(result, "", 0);
        if (count == 0) panel.Children.Add(Label("PanelUnavailable"));
    }

    private int? _fileNext;
    private string _listedFilePath = "";
    private async void OnFileRootChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _disposed || _loadingDocumentScopes) return;
        RenewContext();
        var generation = _generation;
        FilePath.Text = "/"; _listedFilePath = ""; _fileNext = null; FilesList.ItemsSource = null;
        FilesEmpty.Visibility = Visibility.Collapsed;
        _busy = true;
        UpdateConnection();
        await StopDocumentMappingAsync();
        if (_disposed || generation != _generation) return;
        _busy = false;
        UpdateConnection();
        if (_active && _ready) await RefreshAsync();
    }
    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await RunAsync(op => ExecuteActionAsync(op, "file-browse")); } }
    private async void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin || IsRowActionSource(origin) || ItemsControl.ContainerFromElement(FilesList, origin) is not ListBoxItem) return;
        e.Handled = true; await InvokeActionAsync("file-download");
    }
    private async Task BrowseAsync(Operation op, bool append = false, string? targetPath = null)
    {
        var root = Option(FileRoot); var path = (targetPath ?? FilePath.Text).Trim().Trim('/');
        var sameFolder = path == _listedFilePath;
        var offset = append && sameFolder && _fileNext is int next ? next : 0;
        var retainCount = !append && sameFolder ? FilesList.Items.Count : 0;
        var entries = new List<JsonElement>();
        JsonElement result;
        int? following;
        do
        {
            result = await op.Call("list_dir", new { root, path, offset, limit = 200, refresh = root == "crashes" && offset == 0 });
            entries.AddRange(result.GetProperty("entries").EnumerateArray().Select(entry => entry.Clone()));
            following = result.GetProperty("nextOffset").ValueKind == JsonValueKind.Number ? result.GetProperty("nextOffset").GetInt32() : null;
            if (following.HasValue && following.Value <= offset) throw new IOException("Folder listing made no progress.");
            if (append || entries.Count >= retainCount || !following.HasValue) break;
            offset = following.Value;
        } while (true);
        var rows = entries
            .OrderBy(e => Text(e, "type") != "directory").ThenBy(e => Text(e, "name"), StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new HubRow(Text(p, "path"), Text(p, "name"), Text(p, "type") == "directory" ? L("PanelFolder") :
                (p.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? FormatBytes(bytes) : "—"),
                Text(p, "type") == "directory" ? "▸" : "≡", p.Clone())).ToArray();
        if (append && offset > 0)
            rows = FilesList.Items.Cast<HubRow>().Concat(rows).DistinctBy(row => row.Id).ToArray();
        var child = !sameFolder && _listedFilePath.StartsWith(path.Length == 0 ? "" : path + "/", StringComparison.Ordinal)
            ? _listedFilePath : null;
        ReplaceRows(FilesList, rows, preferredId: child, preservePosition: sameFolder);
        _fileNext = following;
        _listedFilePath = path;
        FilePath.Text = path.Length == 0 ? "/" : "/" + path;
        FilesEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = rows.Length == 0 ? "" : F("PanelFileCount", 1, rows.Length, Text(result, "total"));
    }
    private async Task OpenFileAsync(Operation op, HubRow? row)
    {
        if (row is null || !_ready) return;
        if (Text(row.Data, "type") == "directory")
        { await BrowseAsync(op, targetPath: row.Id); return; }
        if (Text(row.Data, "type") != "file") { StatusText.Text = L("PanelUnsupportedFile"); return; }
        var root = Option(FileRoot);
        var dialog = new SaveFileDialog { FileName = Path.GetFileName(row.Title), OverwritePrompt = true };
        if (dialog.ShowDialog(Owner) != true) { StatusText.Text = ""; return; }
        op.Check();
        await DownloadAsync(op, row.Id, root, dialog.FileName);
        StatusText.Text = F("PanelSaved", dialog.FileName);
    }
    internal Func<Window?, string, bool> ConfirmFileDeletion { private get; set; } =
        (owner, body) => owner is not null && AppPromptWindow.ConfirmDestructive(L("PanelDeleteFile"), body, L("PanelDeleteFile"), owner);
    private async Task DeleteFileAsync(Operation op, HubRow? row)
    {
        if (!_ready || Option(FileRoot) == "crashes" || row is null || Text(row.Data, "type") != "file" || !FilesList.Items.Contains(row)) return;
        var directory = _listedFilePath;
        var confirmed = ConfirmFileDeletion(Owner, L("PanelDeleteFileWarning") + "\n\n" + row.Title + "\n/" + row.Id);
        op.Check();
        if (!confirmed) { StatusText.Text = ""; return; }
        await op.Call("delete_file", new { root = "media", path = row.Id, expected = row.Data });
        try { await BrowseAsync(op, targetPath: directory); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            StatusText.Text = F("PanelFileDeleteRefreshFailed", row.Title);
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            _viewModel.AddDiagnosticLog("Device management delete refresh: " + error);
            return;
        }
        StatusText.Text = F("PanelFileDeleted", row.Title);
    }
    internal Func<Window?, string[]?> PickImportFiles { private get; set; } = owner =>
    {
        var dialog = new OpenFileDialog { Title = L("PanelImportFiles"), Multiselect = true, CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileNames : null;
    };
    private async Task ImportFilesAsync(Operation op)
    {
        if (!_ready || Option(FileRoot) == "crashes") return;
        // The open folder is the destination, even if the address has an
        // unfinished edit or a different row is selected.
        var directory = _listedFilePath;
        var sources = PickImportFiles(Owner);
        op.Check();
        if (sources is not { Length: > 0 }) { StatusText.Text = ""; return; }
        var imported = 0;
        Exception? failure = null;
        foreach (var source in sources)
        {
            StatusText.Text = F("PanelImporting", imported + 1, sources.Length, Path.GetFileName(source));
            try
            {
                await op.Call("upload_file", new { root = "media", path = directory, sourcePath = source });
                imported++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { failure = error; break; }
        }
        var destination = directory.Length == 0 ? "/" : "/" + directory;
        if (imported > 0)
        {
            try { await BrowseAsync(op, targetPath: directory); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                if (failure is null)
                {
                    StatusText.Text = F("PanelImportRefreshFailed", imported, destination);
                    StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
                    _viewModel.AddDiagnosticLog("Device management import refresh: " + error);
                    return;
                }
            }
        }
        StatusText.Text = failure is null ? F("PanelImported", imported, destination) :
            F("PanelImportFailed", imported, sources.Length, failure is IOException or UnauthorizedAccessException ? L("PanelFileImportFailed") : FriendlyError(failure));
        if (failure is not null)
        {
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            _viewModel.AddDiagnosticLog("Device management import: " + failure);
        }
    }
    private async Task<long> DownloadAsync(Operation op, string path, string root, string destination, long maximum = 1024L * 1024 * 1024)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        long offset = 0; string? identity = null;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                while (true)
                {
                    var part = await op.Call("read_file", new { root, path, offset, length = 192 * 1024 });
                    var size = part.GetProperty("size").GetInt64();
                    var stamp = size + ":" + Text(part, "modified"); identity ??= stamp;
                    if (stamp != identity || part.GetProperty("offset").GetInt64() != offset || size < 0 || size > maximum)
                        throw new IOException("The source changed or exceeds the export limit.");
                    var bytes = Convert.FromBase64String(Text(part, "data"));
                    if (bytes.Length > 192 * 1024 || offset + bytes.Length > size) throw new IOException("Invalid file chunk.");
                    await output.WriteAsync(bytes, op.Token); offset += bytes.Length;
                    if (part.GetProperty("eof").GetBoolean())
                    { if (offset != size) throw new IOException("Incomplete file."); break; }
                    if (bytes.Length == 0) throw new IOException("File download made no progress.");
                }
            }
            op.Check(); File.Move(temporary, destination, overwrite: true); return offset;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task ExportCrashesAsync(Operation op)
    {
        var dialog = new OpenFolderDialog { Title = L("ExportCrashes") };
        if (dialog.ShowDialog(Owner) != true) { StatusText.Text = ""; return; }
        op.Check();
        var target = Path.Combine(dialog.FolderName, "iPhone-crashes-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(target);
        var queue = new Queue<(string Remote, string Local, int Depth)>(); queue.Enqueue(("", target, 0));
        long total = 0; var files = 0; var directories = 1;
        while (queue.TryDequeue(out var folder))
        {
            op.Check();
            if (folder.Depth > 16) throw new IOException("Crash report nesting exceeds 16 directories.");
            int? cursor = 0;
            while (cursor is int offset)
            {
                var page = await op.Call("list_dir", new { root = "crashes", path = folder.Remote, offset, limit = 200, refresh = folder.Remote == "" && offset == 0 });
                foreach (var entry in page.GetProperty("entries").EnumerateArray())
                {
                    var name = Text(entry, "name");
                    if (name.Length == 0 || name is "." or ".." || name != name.TrimEnd(' ', '.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                        System.Text.RegularExpressions.Regex.IsMatch(name, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        throw new IOException("Unsupported device file name.");
                    var child = Path.Combine(folder.Local, name);
                    var remote = string.Join("/", new[] { folder.Remote, name }.Where(p => p.Length > 0));
                    if (Text(entry, "path") != remote) throw new IOException("Unexpected file path.");
                    if (Text(entry, "type") == "directory")
                    {
                        if (++directories > 5000) throw new IOException("Too many report directories.");
                        Directory.CreateDirectory(child); queue.Enqueue((remote, child, folder.Depth + 1));
                    }
                    else if (Text(entry, "type") == "file")
                    {
                        if (++files > 5000) throw new IOException("Too many reports.");
                        if (File.Exists(child)) throw new IOException("Duplicate report file name.");
                        total += await DownloadAsync(op, remote, "crashes", child, 1024L * 1024 * 1024 - total);
                        StatusText.Text = F("PanelExporting", files, FormatBytes(total));
                    }
                }
                var next = page.GetProperty("nextOffset");
                cursor = next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null;
                if (cursor is int value && value <= offset) throw new IOException("Report listing made no progress.");
            }
        }
        StatusText.Text = F("PanelExported", files, target);
    }

    private readonly Queue<string> _logLines = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DeviceViewModel? _logDevice;
    private string? _logStream;
    private long _logCursor;
    private bool _polling;
    private Task _stoppingLogs = Task.CompletedTask;

    private async Task StartLogsAsync(Operation op)
    {
        await StopLogsAsync(); op.Check();
        var result = await op.Call("syslog_start");
        _logDevice = op.Device; _logStream = Text(result, "streamId");
        _logCursor = 0; _logLines.Clear(); LogText.Clear(); _logTimer.Start();
        UpdateLogEmptyState();
        StatusText.Text = L("PanelLogRunning");
    }
    private async void OnLogTick(object? sender, EventArgs e)
    {
        if (_polling || _busy || _logStream is null || !_active || _page != 4) return;
        _polling = true;
        try
        {
            await RunAsync(async op =>
            {
                var result = await op.Call("syslog_poll", new { streamId = _logStream, cursor = _logCursor });
                var dropped = result.GetProperty("dropped").GetInt32();
                if (dropped > 0) _logLines.Enqueue(F("PanelLogDropped", dropped));
                foreach (var line in result.GetProperty("lines").EnumerateArray()) _logLines.Enqueue(Text(line, "text"));
                while (_logLines.Count > 2000) _logLines.Dequeue();
                var length = _logLines.Sum(line => line.Length);
                while (length > 512 * 1024 && _logLines.Count > 1) length -= _logLines.Dequeue().Length;
                RenderLogs();
                _logCursor = result.GetProperty("cursor").GetInt64();
                if (!result.GetProperty("running").GetBoolean())
                { _logTimer.Stop(); _logStream = null; _logDevice = null; StatusText.Text = L("PanelLogStopped"); }
            }, quiet: true);
        }
        finally { _polling = false; }
    }
    private void OnLogSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        LogSearchHint.Visibility = LogSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LogSearchClear.Visibility = LogSearch.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderLogs();
    }
    private void RenderLogs()
    {
        var filter = LogSearch.Text.Trim();
        LogText.Text = string.Join(Environment.NewLine, _logLines.Where(line => filter.Length == 0 || line.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        UpdateCommands();
        if (LogFollow.IsChecked == true) LogText.ScrollToEnd();
    }
    private void UpdateLogEmptyState()
    {
        if (!_initialized) return;
        LogsEmpty.Visibility = LogText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var filtered = _logLines.Count > 0 && LogSearch.Text.Trim().Length > 0;
        LogsEmptyTitle.SetResourceReference(TextBlock.TextProperty, "DeviceHub" + (filtered ? "PanelNoLogMatches" : _logStream is null ? "PanelLogsIdle" : "PanelLogsWaiting"));
        LogsEmptyHint.SetResourceReference(TextBlock.TextProperty, "DeviceHub" + (filtered ? "PanelLogFilterEmptyHint" : _logStream is null ? "PanelLogHint" : "PanelLogsWaitingHint"));
    }
    private async Task SaveLogsAsync(Operation op)
    {
        if (LogText.Text.Length == 0) { StatusText.Text = L("PanelNoLogMatches"); return; }
        var dialog = new SaveFileDialog { FileName = "iphone-syslog.txt", Filter = "Text|*.txt", OverwritePrompt = true };
        if (dialog.ShowDialog(Owner) != true) { StatusText.Text = ""; return; }
        op.Check(); await File.WriteAllTextAsync(dialog.FileName, LogText.Text, op.Token);
        StatusText.Text = F("PanelSaved", dialog.FileName);
    }
    private Task StopLogsAsync(bool waitForOperation = false)
    {
        _logTimer.Stop();
        var stream = _logStream; var device = _logDevice; _logStream = null; _logDevice = null;
        if (stream is null || device is null) return _stoppingLogs;
        var pending = waitForOperation ? _operation : Task.CompletedTask;
        return _stoppingLogs = StopAsync();
        async Task StopAsync()
        {
            // Context cancellation releases a pending poll before stopping its stream.
            try { await pending; }
            catch (Exception) { /* Context changes intentionally cancel a pending poll. */ }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _request(device, "syslog_stop", new { streamId = stream }, timeout.Token); }
            catch (Exception) { /* The bridge also expires idle streams after 60 seconds. */ }
        }
    }
}
