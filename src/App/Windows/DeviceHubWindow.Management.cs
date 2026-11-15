using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Windows;

internal sealed partial class DeviceHubWindow
{
    private readonly ListBox _processes = new() { DisplayMemberPath = "Label" };
    private readonly ListBox _profiles = new() { DisplayMemberPath = "Label" };
    private readonly ListBox _files = new() { DisplayMemberPath = "Label" };
    private readonly TextBox _filePath = new() { MinWidth = 200 };
    private readonly ComboBox _fileRoot = new() { ItemsSource = new[] { "media", "crashes" }, SelectedIndex = 0, Width = 100 };
    private readonly TextBox _logs = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Queue<string> _logLines = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DeviceViewModel? _logDevice;
    private string? _logStream;
    private long _logCursor;
    private bool _polling;
    private int _fileOffset;
    private int? _fileNext;
    private sealed record Row(string Label, JsonElement Data);
    private static string L(string key) => LocalizationService.Get("DeviceHub" + key);
    private Task<JsonElement> Call(string action, object? arguments = null) =>
        _viewModel.DeviceHubUiAsync(Device(), action, arguments, _stop.Token);
    private void Action(Panel panel, string key, Func<Task> action) => Add(panel, "DeviceHub" + key, action);
    private static StackPanel Body() => new() { Margin = new(4, 10, 4, 4) };
    private static TextBlock Caption(string key)
    {
        var label = new TextBlock { Margin = new(0, 8, 0, 6), TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.TextProperty, "DeviceHub" + key); return label;
    }
    private static ComboBoxItem Option(string value, string label)
    {
        var item = new ComboBoxItem { Tag = value }; item.SetResourceReference(ContentProperty, "DeviceHub" + label); return item;
    }
    private static string Value(ComboBox control) => (control.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private static DockPanel Page(Panel actions, UIElement content)
    {
        var page = new DockPanel();
        DockPanel.SetDock(actions, Dock.Top); page.Children.Add(actions); page.Children.Add(content);
        return page;
    }
    private static void Rows(ListBox list, JsonElement result, string property, Func<JsonElement, string> label) =>
        list.ItemsSource = result.GetProperty(property).EnumerateArray().Select(p => new Row(label(p), p.Clone())).ToArray();
    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString() : "";
    private static JsonElement Selected(ListBox list) =>
        list.SelectedItem is Row row ? row.Data : throw new InvalidOperationException(L("ChooseItem"));
    private bool Confirm(string detail) => MessageBox.Show(this, detail, L("Confirm"), MessageBoxButton.OKCancel,
        MessageBoxImage.Warning) == MessageBoxResult.OK;

    private TabControl CreateManagementTabs(Panel mediaOptions, Panel mediaActions)
    {
        var tabs = new TabControl();
        void Tab(string key, UIElement content)
        {
            var item = new TabItem { Content = key is "Applications" or "Media" or "Device" ?
                new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } : content };
            item.SetResourceReference(HeaderedContentControl.HeaderProperty, "DeviceHub" + key); tabs.Items.Add(item);
        }
        var apps = Body(); var appButtons = new WrapPanel(); apps.Children.Add(appButtons);
        _apps.MaxHeight = 400;
        var include = new CheckBox { Margin = new(0, 6, 8, 6) }; include.SetResourceReference(ContentProperty, "DeviceHubIncludeAll"); apps.Children.Add(include);
        var filter = new ComboBox { ItemsSource = new[] { Option("all", "AllApps"), Option("user", "UserApps"), Option("system", "SystemApps"),
            Option("developer", "DeveloperApps"), Option("clips", "ClipApps") }, SelectedIndex = 0, Margin = new(0, 0, 0, 8) };
        filter.SelectionChanged += (_, _) => ApplyAppFilter(Value(filter)); apps.Children.Add(filter);
        Action(appButtons, "Apps", async () =>
        {
            var result = await Call("apps", new { includeHidden = include.IsChecked == true, includeInternal = include.IsChecked == true,
                includeClips = include.IsChecked == true });
            _allApps = result.GetProperty("apps").EnumerateArray().Select(a => new AppItem(Text(a, "bundleId"), Text(a, "name"),
                a.GetProperty("isFirstParty").GetBoolean(), a.GetProperty("isDeveloper").GetBoolean(), a.GetProperty("isAppClip").GetBoolean())).ToArray();
            ApplyAppFilter(Value(filter));
            _status.Text = _apps.Items.Count.ToString(CultureInfo.InvariantCulture) + " " + L("Apps");
        });
        Action(appButtons, "Install", async () =>
        {
            var dialog = new OpenFileDialog { Filter = "iOS app (*.ipa)|*.ipa", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) Show(await Call("install_app", new { path = dialog.FileName }));
        });
        Action(appButtons, "Launch", async () =>
        {
            if (_apps.SelectedItem is not AppItem app) throw new InvalidOperationException(L("ChooseApp"));
            Show(await Call("launch", new { bundleId = app.BundleId }));
        });
        Action(appButtons, "Uninstall", async () =>
        {
            if (_apps.SelectedItem is not AppItem app) throw new InvalidOperationException(L("ChooseApp"));
            if (Confirm(L("UninstallWarning") + "\n" + app.Name + "\n" + app.BundleId))
                Show(await Call("uninstall_app", new { bundleId = app.BundleId }));
        });
        var icon = new Image { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Left };
        Action(appButtons, "Icon", async () =>
        {
            if (_apps.SelectedItem is not AppItem app) throw new InvalidOperationException(L("ChooseApp"));
            var result = await Call("get_app_icon", new { bundleId = app.BundleId });
            using var memory = new MemoryStream(Convert.FromBase64String(Text(result, "data")));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory; bitmap.EndInit(); bitmap.Freeze(); icon.Source = bitmap;
        });
        apps.Children.Add(icon);
        Tab("Applications", Page(apps, _apps));

        var processes = new WrapPanel();
        Action(processes, "Refresh", async () => Rows(_processes, await Call("list_processes"), "processes",
            p => Text(p, "processIdentifier") + "  " + (Text(p, "executableURL") is { Length: > 0 } path ? path : p.ToString())));
        Action(processes, "Terminate", async () =>
        {
            var process = Selected(_processes);
            var pid = process.TryGetProperty("processIdentifier", out var number) ? number.GetInt32() :
                process.GetProperty("pid").GetInt32();
            if (Confirm(L("Terminate") + " · PID " + pid + "?")) Show(await Call("terminate_app", new { pid, signal = 15 }));
        });
        Tab("Processes", Page(processes, _processes));

        var profiles = new WrapPanel();
        Action(profiles, "Refresh", async () =>
        {
            var result = await Call("list_profiles");
            Rows(_profiles, result, "profiles", p => Text(p, "name") + " · " + Text(p, "kind") + " · " + Text(p, "id"));
            Show(result.GetProperty("errors"));
        });
        Action(profiles, "Install", async () =>
        {
            var dialog = new OpenFileDialog { Filter = "iOS profile|*.mobileprovision;*.mobileconfig", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) Show(await Call("install_profile", new { path = dialog.FileName }));
        });
        Action(profiles, "Remove", async () =>
        {
            var row = Selected(_profiles);
            if (Confirm(L("Remove") + "\n" + Text(row, "name") + "\n" + Text(row, "id")))
                Show(await Call("remove_profile", new { kind = Text(row, "kind"), id = Text(row, "id") }));
        });
        Tab("Profiles", Page(profiles, _profiles));

        var files = Body();
        var location = new DockPanel { LastChildFill = true }; location.Children.Add(_fileRoot); location.Children.Add(_filePath); files.Children.Add(location);
        var fileButtons = new WrapPanel(); files.Children.Add(fileButtons);
        Action(fileButtons, "Browse", async () => { _fileOffset = 0; await BrowseAsync(); });
        Action(fileButtons, "Parent", async () =>
        { var path = _filePath.Text.TrimEnd('/'); _filePath.Text = path.Contains('/') ? path[..path.LastIndexOf('/')] : ""; _fileOffset = 0; await BrowseAsync(); });
        Action(fileButtons, "Next", async () => { if (_fileNext is int next) { _fileOffset = next; await BrowseAsync(); } });
        Action(fileButtons, "Download", async () =>
        {
            var file = Selected(_files);
            if (Text(file, "type") == "directory")
            { _filePath.Text = Text(file, "path"); _fileOffset = 0; await BrowseAsync(); return; }
            if (Text(file, "type") != "file") throw new InvalidOperationException(L("ChooseItem"));
            var dialog = new SaveFileDialog { FileName = Path.GetFileName(Text(file, "name")), OverwritePrompt = true };
            if (dialog.ShowDialog(this) == true)
            {
                await DownloadAsync(Text(file, "path"), _fileRoot.SelectedItem?.ToString() ?? "media", dialog.FileName);
                _status.Text = dialog.FileName;
            }
        });
        Action(fileButtons, "ExportCrashes", ExportCrashesAsync);
        Tab("Files", Page(files, _files));

        var logActions = new WrapPanel();
        Action(logActions, "StartLog", async () =>
        {
            await StopLogsAsync();
            var result = await Call("syslog_start"); _logDevice = Device(); _logStream = Text(result, "streamId");
            _logCursor = 0; _logLines.Clear(); _logs.Clear(); _logTimer.Start();
        });
        Action(logActions, "StopLog", StopLogsAsync);
        Action(logActions, "SaveLog", async () =>
        {
            var dialog = new SaveFileDialog { FileName = "iphone-syslog.txt", Filter = "Text|*.txt", OverwritePrompt = true };
            if (dialog.ShowDialog(this) == true) await File.WriteAllTextAsync(dialog.FileName, _logs.Text, _stop.Token);
        });
        _logTimer.Tick += async (_, _) => await PollLogsAsync();
        Tab("Logs", Page(logActions, _logs));

        var media = Body();
        media.Children.Add(mediaOptions); media.Children.Add(mediaActions);
        media.Children.Add(Caption("RecordingHint"));
        var recording = new WrapPanel();
        Action(recording, "RecordHevc", async () => Show(await Call("record_start", new { format = "h265" })));
        Action(recording, "RecordPackets", async () => Show(await Call("record_start", new { format = "imrtp" })));
        Action(recording, "StopRecord", async () => Show(await Call("record_stop")));
        media.Children.Add(recording); Tab("Media", media);
        Tab("PhoneSettings", CreatePhoneSettings());

        var info = Body(); var infoActions = new WrapPanel(); info.Children.Add(infoActions);
        Action(infoActions, "Info", async () => Show(await Call("device_info")));
        Action(infoActions, "Capabilities", async () => Show(await Call("capabilities")));
        Action(infoActions, "Tunnel", async () => Show(await Call("tunnel_info")));
        foreach (var (key, action) in new[] { ("Restart", "restart"), ("Shutdown", "shutdown"), ("Sleep", "sleep") })
            Action(infoActions, key, async () => { if (Confirm(L(key) + "?")) Show(await Call("device_action", new { action })); });
        info.Children.Add(Caption("DeviceHint")); Tab("Device", info);
        var buttons = new WrapPanel(); info.Children.Add(buttons);
        foreach (var (key, button) in new[] { ("Home", "home"), ("AppSwitcher", "app_switcher"), ("Lock", "lock"),
            ("VolumeUp", "volume_up"), ("VolumeDown", "volume_down") })
            Action(buttons, key, async () => Show(await Call("press_button", new { button })));
        return tabs;
    }

    private UIElement CreatePhoneSettings()
    {
        var panel = Body();
        panel.Children.Add(Caption("SettingsHint"));
        var key = new ComboBox { ItemsSource = new[] { "showBorders", "increaseContrast", "reduceMotion", "reduceTransparency",
            "voiceOver", "colorFilter", "liquidGlass", "textSize", "appearance", "largerAccessibilitySizes" }
            .Select(value => Option(value, "Setting_" + value)).ToArray(), SelectedIndex = 0 };
        panel.Children.Add(key);
        var boolean = new CheckBox { Margin = new(0, 8, 0, 8) }; boolean.SetResourceReference(ContentProperty, "DeviceHubEnabled"); panel.Children.Add(boolean);
        var opacity = new Slider { Minimum = 0, Maximum = 1, Value = 1, TickFrequency = .05, IsSnapToTickEnabled = true };
        var numeric = new TextBlock(); numeric.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value") { Source = opacity, StringFormat = "0.00" });
        panel.Children.Add(opacity); panel.Children.Add(numeric);
        var choice = new ComboBox(); panel.Children.Add(choice);
        void Select()
        {
            var setting = Value(key);
            boolean.Visibility = setting is "liquidGlass" or "textSize" or "appearance" ? Visibility.Collapsed : Visibility.Visible;
            opacity.Visibility = numeric.Visibility = setting == "liquidGlass" ? Visibility.Visible : Visibility.Collapsed;
            choice.Visibility = setting is "textSize" or "appearance" ? Visibility.Visible : Visibility.Collapsed;
            choice.ItemsSource = setting == "appearance" ? new[] { "light", "dark" } : new[] { "extraSmall", "small", "medium",
                "large", "extraLarge", "extraExtraLarge", "extraExtraExtraLarge", "accessibilityMedium", "accessibilityLarge",
                "accessibilityExtraLarge", "accessibilityExtraExtraLarge", "accessibilityExtraExtraExtraLarge" };
            choice.SelectedIndex = setting == "textSize" ? 3 : 0;
        }
        key.SelectionChanged += (_, _) => Select(); Select();
        var settings = new WrapPanel();
        Action(settings, "ReadSettings", async () => Show(await Call("get_settings")));
        Action(settings, "Read", async () => Show(await Call("get_setting", new { key = Value(key) })));
        Action(settings, "Apply", async () =>
        {
            var setting = Value(key);
            object value = setting switch { "liquidGlass" => opacity.Value, "textSize" or "appearance" => choice.SelectedItem!.ToString()!, _ => boolean.IsChecked == true };
            Show(await Call("set_setting", new { key = setting, value }));
        });
        panel.Children.Add(settings);
        panel.Children.Add(Caption("LocationHint"));
        var coordinates = new Grid(); coordinates.ColumnDefinitions.Add(new()); coordinates.ColumnDefinitions.Add(new());
        var lat = new TextBox { Text = "0", Margin = new(0, 0, 8, 8), ToolTip = L("Latitude") };
        var lon = new TextBox { Text = "0", Margin = new(0, 0, 0, 8), ToolTip = L("Longitude") }; Grid.SetColumn(lon, 1);
        var latLabel = new StackPanel(); latLabel.Children.Add(Caption("Latitude")); latLabel.Children.Add(lat);
        var lonLabel = new StackPanel(); lonLabel.Children.Add(Caption("Longitude")); lonLabel.Children.Add(lon); Grid.SetColumn(lonLabel, 1);
        coordinates.Children.Add(latLabel); coordinates.Children.Add(lonLabel); panel.Children.Add(coordinates);
        var location = new WrapPanel();
        Action(location, "SetLocation", async () => Show(await Call("set_location", new {
            latitude = double.Parse(lat.Text, CultureInfo.InvariantCulture), longitude = double.Parse(lon.Text, CultureInfo.InvariantCulture) })));
        Action(location, "ClearLocation", async () => Show(await Call("clear_location")));
        panel.Children.Add(location);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void ClearManagementLists()
    { _allApps = []; _processes.ItemsSource = _profiles.ItemsSource = _files.ItemsSource = null; }

    private AppItem[] _allApps = [];
    private void ApplyAppFilter(string value) => _apps.ItemsSource = _allApps.Where(app => value switch
    { "user" => !app.FirstParty, "system" => app.FirstParty, "developer" => app.Developer, "clips" => app.Clip, _ => true }).ToArray();

    private async Task BrowseAsync()
    {
        var result = await Call("list_dir", new { root = _fileRoot.SelectedItem?.ToString(), path = _filePath.Text, offset = _fileOffset,
            limit = 200, refresh = _fileRoot.SelectedItem?.ToString() == "crashes" && _fileOffset == 0 });
        Rows(_files, result, "entries", p => (Text(p, "type") == "directory" ? "▸ " : "") + Text(p, "name") + "  ·  " + Text(p, "size"));
        _fileNext = result.GetProperty("nextOffset").ValueKind == JsonValueKind.Number ? result.GetProperty("nextOffset").GetInt32() : null;
        _status.Text = (_fileOffset + 1) + "–" + (_fileOffset + _files.Items.Count) + " / " + Text(result, "total");
    }

    private async Task<long> DownloadAsync(string path, string root, string destination, long maximum = 1024L * 1024 * 1024)
    {
        // Save dialogs authorize replacement, but only replace after a complete validated download.
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        long offset = 0; string? identity = null;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                while (true)
                {
                    var part = await Call("read_file", new { root, path, offset, length = 192 * 1024 });
                    var size = part.GetProperty("size").GetInt64();
                    var stamp = size + ":" + Text(part, "modified"); identity ??= stamp;
                    if (stamp != identity || part.GetProperty("offset").GetInt64() != offset || size < 0 || size > maximum)
                        throw new IOException("The source changed or exceeds the export limit.");
                    var bytes = Convert.FromBase64String(Text(part, "data"));
                    if (bytes.Length > 192 * 1024 || offset + bytes.Length > size) throw new IOException("Invalid file chunk.");
                    await output.WriteAsync(bytes, _stop.Token); offset += bytes.Length;
                    if (part.GetProperty("eof").GetBoolean())
                    { if (offset != size) throw new IOException("Incomplete file."); break; }
                    if (bytes.Length == 0) throw new IOException("File download made no progress.");
                }
            }
            File.Move(temporary, destination, overwrite: true); return offset;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task ExportCrashesAsync()
    {
        var dialog = new OpenFolderDialog { Title = L("ExportCrashes") };
        if (dialog.ShowDialog(this) != true) return;
        var target = Path.Combine(dialog.FolderName, "iPhone-crashes-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(target);
        var queue = new Queue<(string Remote, string Local, int Depth)>(); queue.Enqueue(("", target, 0));
        long total = 0; var files = 0; var directories = 1;
        while (queue.TryDequeue(out var folder))
        {
            if (folder.Depth > 16) throw new IOException("Crash report nesting exceeds 16 directories.");
            int? cursor = 0;
            while (cursor is int offset)
            {
                var page = await Call("list_dir", new { root = "crashes", path = folder.Remote, offset, limit = 200, refresh = folder.Remote == "" && offset == 0 });
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
                        total += await DownloadAsync(remote, "crashes", child, 1024L * 1024 * 1024 - total);
                    }
                }
                cursor = page.GetProperty("nextOffset").ValueKind == JsonValueKind.Number ? page.GetProperty("nextOffset").GetInt32() : null;
            }
        }
        _status.Text = target + "\n" + files + " files · " + total + " bytes";
    }

    private async Task PollLogsAsync()
    {
        if (_busy || _polling || _logStream is null || _logDevice is null) return;
        _polling = true;
        try
        {
            var result = await _viewModel.DeviceHubUiAsync(_logDevice, "syslog_poll", new { streamId = _logStream, cursor = _logCursor }, _stop.Token);
            if (result.GetProperty("dropped").GetInt32() > 0) _logLines.Enqueue("[dropped: " + Text(result, "dropped") + "]");
            foreach (var line in result.GetProperty("lines").EnumerateArray()) _logLines.Enqueue(Text(line, "text"));
            while (_logLines.Count > 2000) _logLines.Dequeue();
            var length = _logLines.Sum(line => line.Length);
            while (length > 512 * 1024 && _logLines.Count > 1) length -= _logLines.Dequeue().Length;
            _logs.Text = string.Join(Environment.NewLine, _logLines); _logs.ScrollToEnd();
            _logCursor = result.GetProperty("cursor").GetInt64();
            if (!result.GetProperty("running").GetBoolean()) { _logTimer.Stop(); Show(result); }
        }
        catch (Exception error) { _logTimer.Stop(); _status.Text = error.Message; }
        finally { _polling = false; }
    }

    private async Task StopLogsAsync()
    {
        _logTimer.Stop();
        var stream = _logStream; var device = _logDevice; _logStream = null; _logDevice = null;
        if (stream is null || device is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _viewModel.DeviceHubUiAsync(device, "syslog_stop", new { streamId = stream }, timeout.Token); }
        catch (Exception) { }
    }
}
