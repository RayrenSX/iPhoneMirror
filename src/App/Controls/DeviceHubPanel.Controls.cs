using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App.Controls;

internal sealed partial class DeviceHubPanel
{
    private sealed record SettingEditor(Grid Row, FrameworkElement Input, TextBlock State, Button Apply);
    private readonly Dictionary<string, SettingEditor> _settings = [];
    private readonly TextBox _latitude = new() { Text = "0", Margin = new(0, 0, 8, 8) };
    private readonly TextBox _longitude = new() { Text = "0", Margin = new(0, 0, 0, 8) };
    private readonly StackPanel _settingDetails = new();

    private Button ActionButton(Panel parent, string key, string action)
    {
        var button = new Button { Tag = action };
        button.SetResourceReference(ContentControl.ContentProperty, "DeviceHub" + key);
        button.SetResourceReference(StyleProperty, "HubAction");
        button.Click += OnActionClick; parent.Children.Add(button); return button;
    }

    private void CreateControlGroups()
    {
        ControlGroups.Children.Add(Label("PanelPhoneButtons", "HubHeading"));
        var buttons = new WrapPanel(); ControlGroups.Children.Add(buttons);
        foreach (var (key, action) in new[] { ("Home", "home"), ("AppSwitcher", "app_switcher"), ("Lock", "lock"),
            ("VolumeUp", "volume_up"), ("VolumeDown", "volume_down") }) ActionButton(buttons, key, "button:" + action);

        ControlGroups.Children.Add(Label("PhoneSettings", "HubHeading"));
        ControlGroups.Children.Add(Label("SettingsHint"));
        var settings = new WrapPanel { Margin = new(0, 8, 0, 4) }; ControlGroups.Children.Add(settings);
        ActionButton(settings, "ReadSettings", "settings-read");
        foreach (var key in new[] { "appearance", "textSize", "showBorders", "increaseContrast", "reduceMotion", "reduceTransparency",
            "voiceOver", "colorFilter", "liquidGlass", "largerAccessibilitySizes" }) CreateSettingRow(key);
        ControlGroups.Children.Add(_settingDetails);

        ControlGroups.Children.Add(Label("PanelLocation", "HubHeading"));
        ControlGroups.Children.Add(Label("LocationHint"));
        var coordinates = new Grid { Margin = new(0, 8, 0, 0) };
        coordinates.ColumnDefinitions.Add(new()); coordinates.ColumnDefinitions.Add(new());
        var lat = new StackPanel(); lat.Children.Add(Label("Latitude")); lat.Children.Add(_latitude);
        var lon = new StackPanel(); lon.Children.Add(Label("Longitude")); lon.Children.Add(_longitude); Grid.SetColumn(lon, 1);
        coordinates.Children.Add(lat); coordinates.Children.Add(lon); ControlGroups.Children.Add(coordinates);
        var location = new WrapPanel(); ControlGroups.Children.Add(location);
        ActionButton(location, "SetLocation", "location-set"); ActionButton(location, "ClearLocation", "location-clear");

        ControlGroups.Children.Add(Label("PanelPower", "HubHeading"));
        var power = new WrapPanel(); ControlGroups.Children.Add(power);
        ActionButton(power, "Sleep", "power:sleep"); ActionButton(power, "Restart", "power:restart"); ActionButton(power, "Shutdown", "power:shutdown");
    }

    private void CreateSettingRow(string key)
    {
        var row = new Grid { Margin = new(0, 6, 0, 7), Visibility = Visibility.Collapsed };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(132) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Label("Setting_" + key));
        var state = Label("PanelNotRead"); state.FontSize = 10; state.Margin = new(0, 3, 0, 0); labels.Children.Add(state);
        row.Children.Add(labels);
        FrameworkElement input;
        if (key is "appearance" or "textSize")
        {
            var combo = new ComboBox { VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            var options = key == "appearance" ? new[] { "light", "dark" } : new[] { "extraSmall", "small", "medium", "large", "extraLarge", "extraExtraLarge", "extraExtraExtraLarge",
                "accessibilityMedium", "accessibilityLarge", "accessibilityExtraLarge", "accessibilityExtraExtraLarge", "accessibilityExtraExtraExtraLarge" };
            foreach (var value in options)
            {
                var item = new ComboBoxItem { Tag = value };
                item.SetResourceReference(ContentControl.ContentProperty, "DeviceHubPanelValue_" + value); combo.Items.Add(item);
            }
            combo.SelectedIndex = -1; input = combo;
        }
        else if (key == "liquidGlass") input = new Slider { Minimum = 0, Maximum = 1, Value = .5, TickFrequency = .1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        else input = new CheckBox { IsThreeState = true, IsChecked = null, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        input.SetResourceReference(ToolTipProperty, "DeviceHubSetting_" + key);
        System.Windows.Automation.AutomationProperties.SetName(input, L("Setting_" + key));
        Grid.SetColumn(input, 1); row.Children.Add(input);
        var apply = new Button { Tag = "setting:" + key, Margin = new(8, 0, 0, 0), Padding = new(8, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center };
        apply.SetResourceReference(ContentControl.ContentProperty, "DeviceHubApply"); apply.Click += OnActionClick;
        Grid.SetColumn(apply, 2); row.Children.Add(apply);
        _settings.Add(key, new(row, input, state, apply)); ControlGroups.Children.Add(row);
    }

    private void ResetSettingValues()
    {
        foreach (var editor in _settings.Values)
        {
            editor.Row.Visibility = Visibility.Collapsed;
            editor.State.SetResourceReference(TextBlock.TextProperty, "DeviceHubPanelNotRead");
            editor.Input.IsEnabled = editor.Apply.IsEnabled = true;
            if (editor.Input is CheckBox checkbox) checkbox.IsChecked = null;
            else if (editor.Input is ComboBox combo) combo.SelectedIndex = -1;
            else if (editor.Input is Slider slider) slider.Value = .5;
        }
        _settingDetails.Children.Clear();
    }

    private async Task ControlActionAsync(Operation op, string action)
    {
        if (action.StartsWith("button:", StringComparison.Ordinal))
        { await op.Call("press_button", new { button = action[7..] }); return; }
        if (action.StartsWith("power:", StringComparison.Ordinal))
        {
            var command = action[6..];
            if (command != "sleep" && !Confirm(L(command == "restart" ? "Restart" : "Shutdown") + "\n\n" + op.Device!.DisplayName))
            { StatusText.Text = ""; return; }
            op.Check(); await op.Call("device_action", new { action = command }); return;
        }
        if (action.StartsWith("setting:", StringComparison.Ordinal))
        {
            var key = action[8..]; var editor = _settings[key];
            object? value = editor.Input switch
            {
                CheckBox check => check.IsChecked,
                ComboBox combo => combo.SelectedIndex >= 0 ? Option(combo) : null,
                Slider slider => slider.Value, _ => null
            };
            if (value is null) { StatusText.Text = L("PanelChooseValue"); return; }
            await op.Call("set_setting", new { key, value });
            try
            {
                var result = await op.Call("get_setting", new { key });
                SetSettingValue(key, result.GetProperty("value"));
                StatusText.Text = F("PanelSettingApplied", L("Setting_" + key));
            }
            catch (OperationCanceledException) { throw; }
            catch { editor.State.Text = L("PanelAppliedUnverified"); StatusText.Text = L("PanelAppliedUnverified"); }
            return;
        }
        switch (action)
        {
            case "settings-read":
                var all = await op.Call("get_settings");
                var settings = all.GetProperty("settings"); var available = 0;
                foreach (var (key, editor) in _settings)
                {
                    if (settings.TryGetProperty(key, out var setting) && Flag(setting, "available") && setting.TryGetProperty("value", out var value))
                    { SetSettingValue(key, value); available++; }
                    else
                    { editor.Row.Visibility = Visibility.Collapsed; editor.Input.IsEnabled = editor.Apply.IsEnabled = false; }
                }
                _settingDetails.Children.Clear();
                foreach (var key in new[] { "lookAndFeel", "supportedLooksAndFeels" })
                    if (settings.TryGetProperty(key, out var setting) && Flag(setting, "available") && setting.TryGetProperty("value", out var value))
                        AddDetail(_settingDetails, key, string.Join(" · ", LeafValues(value).Take(12)));
                StatusText.Text = F("PanelSettingsRead", available, _settings.Count); break;
            case "location-set":
                if (!double.TryParse(_latitude.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var lat) ||
                    !double.TryParse(_longitude.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var lon) ||
                    !double.IsFinite(lat) || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
                { StatusText.Text = L("PanelInvalidLocation"); return; }
                await op.Call("set_location", new { latitude = lat, longitude = lon }); StatusText.Text = L("PanelLocationSet"); break;
            case "location-clear": await op.Call("clear_location"); StatusText.Text = L("PanelLocationCleared"); break;
        }
    }

    private static IEnumerable<string> LeafValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object && !property.Value.EnumerateObject().Any()) yield return property.Name;
                else foreach (var leaf in LeafValues(property.Value)) yield return leaf;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) foreach (var leaf in LeafValues(child)) yield return leaf;
        else yield return value.ToString();
    }
    private void SetSettingValue(string key, JsonElement value)
    {
        var editor = _settings[key]; var leaves = LeafValues(value).ToArray();
        editor.Row.Visibility = Visibility.Visible;
        if (editor.Input is CheckBox checkbox)
            checkbox.IsChecked = leaves.FirstOrDefault(s => bool.TryParse(s, out _)) is { } boolean ? bool.Parse(boolean) : null;
        else if (editor.Input is ComboBox combo)
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => leaves.Contains(item.Tag?.ToString(), StringComparer.OrdinalIgnoreCase));
        else if (editor.Input is Slider slider && leaves.FirstOrDefault(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) is { } number)
            slider.Value = double.Parse(number, CultureInfo.InvariantCulture);
        editor.State.SetResourceReference(TextBlock.TextProperty, "DeviceHubPanelReadCurrent");
        editor.Input.IsEnabled = editor.Apply.IsEnabled = true;
    }

}
