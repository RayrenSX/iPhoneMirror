using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Windows;

public partial class FirstRunSetupWindow
{
    private void Preferences()
    {
        var row = CardRow();
        foreach (var (value, label) in new[] { ("system", "LanguageSystem"), ("zh-CN", "LanguageChinese"),
            ("zh-TW", "LanguageTraditionalChineseTaiwan"), ("zh-HK", "LanguageTraditionalChineseHongKong"), ("en-US", "LanguageEnglish") })
        {
            var card = ChoiceCard("language", label, null, LocalizationService.SelectedLanguage == value);
            card.Checked += (_, _) => _main.SelectedLanguage = value;
            row.Children.Add(card);
        }
        Body.Children.Add(row);
    }

    private void ApplicationModeChoices()
    {
        if (!_state.PreferencesApplied)
        {
            _main.SelectedApplicationDisplayMode = ApplicationDisplayMode.Lightweight;
            _state.PreferencesApplied = true;
        }
        var row = CardRow();
        foreach (var mode in Enum.GetValues<ApplicationDisplayMode>())
        {
            var card = ChoiceCard("mode", "ApplicationMode" + mode, "SetupMode" + mode, _main.SelectedApplicationDisplayMode == mode);
            card.Tag = Symbol(mode switch { ApplicationDisplayMode.Complete => "monitor", ApplicationDisplayMode.Lightweight => "spark", _ => "pin" });
            card.Checked += (_, _) => _main.SelectedApplicationDisplayMode = mode;
            row.Children.Add(card);
        }
        Body.Children.Add(row);
    }

    private void AppearanceChoices()
    {
        var row = CardRow();
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            var card = ChoiceCard("theme", "SetupTheme" + theme, null, ThemeService.Preference == theme);
            card.Tag = Symbol(theme switch { AppTheme.Light => "sun", AppTheme.Dark => "moon", _ => "monitor" });
            var content = (StackPanel)card.Content;
            content.Children.Insert(0, ThemePreview(theme));
            card.Checked += (_, _) =>
            {
                ThemeService.Apply(theme);
                if (Application.Current is App app)
                {
                    app.UpdateSettings.Theme = theme;
                    if (!app.SaveUpdateSettings()) Failure(new System.IO.IOException(L("SetupSaveFailed")));
                }
            };
            row.Children.Add(card);
        }
        Body.Children.Add(row);
    }

    private static Border ThemePreview(AppTheme theme)
    {
        var background = theme == AppTheme.Dark ? new SolidColorBrush(Color.FromRgb(40, 40, 40)) : Brushes.WhiteSmoke;
        var preview = new Grid { Height = 62 };
        preview.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        preview.ColumnDefinitions.Add(new());
        var rail = new Border { Background = theme == AppTheme.Dark ? Brushes.DimGray : Brushes.LightGray, CornerRadius = new(5, 0, 0, 5) };
        preview.Children.Add(rail);
        var lines = new StackPanel { Margin = new(10, 10, 12, 8) };
        for (var i = 0; i < 3; i++)
        {
            var line = new Border { Height = i == 0 ? 7 : 4, Margin = new(0, 0, i * 10, 6), CornerRadius = new(2), Opacity = i == 0 ? 1 : .3 };
            line.Background = theme == AppTheme.Dark ? Brushes.WhiteSmoke : Brushes.DimGray; lines.Children.Add(line);
        }
        Grid.SetColumn(lines, 1); preview.Children.Add(lines);
        if (theme == AppTheme.System)
        {
            var half = new Border { Background = new SolidColorBrush(Color.FromArgb(100, 20, 20, 20)), Width = 60, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(half, 1); preview.Children.Add(half);
        }
        return new Border { Background = background, CornerRadius = new(6), Child = preview, Margin = new(0, 0, 0, 14), IsHitTestVisible = false };
    }

    private WrapPanel CardRow()
    {
        var row = new WrapPanel { Margin = new(0, 4, -12, 0) };
        row.SizeChanged += (_, _) =>
        {
            var columns = row.ActualWidth >= 570 ? (row.Children.Count == 4 ? 2 : Math.Min(3, Math.Max(1, row.Children.Count))) : row.ActualWidth >= 340 ? 2 : 1;
            row.ItemWidth = Math.Max(120, row.ActualWidth / columns);
        };
        return row;
    }

    private RadioButton ChoiceCard(string group, string title, string? description, bool selected)
    {
        var card = ValueCard(group, L(title), description is null ? null : L(description), selected);
        var panel = (StackPanel)card.Content;
        ((TextBlock)panel.Children[0]).SetResourceReference(TextBlock.TextProperty, title);
        if (description is not null) ((TextBlock)panel.Children[1]).SetResourceReference(TextBlock.TextProperty, description);
        System.Windows.Automation.AutomationProperties.SetName(card, L(title));
        return card;
    }

    private RadioButton ValueCard(string group, string title, string? description, bool selected)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, description is null ? 0 : 10) });
        if (description is not null)
        {
            var detail = new TextBlock { Text = description, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); panel.Children.Add(detail);
        }
        var glyph = group switch { "language" => "language", "theme" => "sun", "usage" => "phone", "fps" => "bolt", "decoder" => "settings", "backend" => "wireless", "display" => "spark", _ => "image" };
        var card = new RadioButton { GroupName = group, Content = panel, IsChecked = selected, Tag = Symbol(glyph),
            MinHeight = description is null ? 112 : 192, RenderTransformOrigin = new(.5, .5) };
        if (group == "language") card.Height = 120;
        card.SetResourceReference(StyleProperty, "SetupChoiceCard");
        System.Windows.Automation.AutomationProperties.SetName(card, title);
        AnimateCard(card);
        return card;
    }

    private void AnimateCard(RadioButton card)
    {
        if (_previewOnly) return;
        var scale = new ScaleTransform(1, 1);
        var offset = new TranslateTransform();
        card.RenderTransform = new TransformGroup { Children = { scale, offset } };
        void Move(double y, double size)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            DoubleAnimation Motion(double to) => new(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            offset.BeginAnimation(TranslateTransform.YProperty, Motion(y));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion(size));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion(size));
        }
        card.MouseEnter += (_, _) => Move(-3, 1);
        card.MouseLeave += (_, _) => Move(0, 1);
        card.PreviewMouseLeftButtonDown += (_, _) => Move(0, .98);
        card.PreviewMouseLeftButtonUp += (_, _) => Move(card.IsMouseOver ? -3 : 0, 1);
        card.Checked += (_, _) =>
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            card.ApplyTemplate();
            if (card.Template.FindName("SelectionMark", card) is FrameworkElement mark)
            {
                var pop = new ScaleTransform(); mark.RenderTransformOrigin = new(.5, .5); mark.RenderTransform = pop;
                var animation = new DoubleAnimation(.6, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = .3, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
                pop.BeginAnimation(ScaleTransform.ScaleXProperty, animation); pop.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            }
        };
    }

    private void DisplaySettings()
    {
        var row = CardRow();
        var current = ChoiceCard("display", "SetupKeepSettings", "SetupKeepSettingsHint", !_state.CustomizeDisplay);
        var custom = ChoiceCard("display", "SetupCustomize", "SetupCustomizeHint", _state.CustomizeDisplay);
        custom.Tag = Symbol("settings");
        current.Checked += (_, _) => { _state.CustomizeDisplay = false; Save(); };
        custom.Checked += (_, _) => { _state.CustomizeDisplay = true; Save(); };
        row.Children.Add(current); row.Children.Add(custom); Body.Children.Add(row);
    }

    private void DisplayChoices()
    {
        if (_state.IsWiredDisplayStep && _state.Devices.ElementAtOrDefault(_state.DisplayDeviceIndex) is { } device)
            _main.SetupSelect(device.Id);
        void Pick<T>(string group, IReadOnlyList<T> values, T selected, Func<T, string> label, Action<T> choose)
        {
            var options = new List<RadioButton>();
            var cards = values.Select(value =>
            {
                var card = ValueCard(group, label(value), null, EqualityComparer<T>.Default.Equals(selected, value));
                card.Checked += (_, _) =>
                {
                    // Off-page options are detached from WPF's radio group.
                    foreach (var other in options.Where(other => other != card)) other.IsChecked = false;
                    choose(value);
                };
                options.Add(card); return (UIElement)card;
            }).ToArray();
            Body.Children.Add(Paginate(cards, 6, cards.Length > 0 ? Math.Max(0, values.ToList().IndexOf(selected)) / 6 : 0));
        }
        switch (_state.Step)
        {
            case SetupStep.WirelessBackend:
                Pick("backend", _main.WirelessReceiverBackends, _main.SelectedWirelessReceiverBackend, v => v.Label, v =>
                {
                    _main.SelectedWirelessReceiverBackend = v;
                    Save();
                });
                Body.Children.Add(Text("SetupReceiverDownloadHint", 13)); break;
            case SetupStep.WirelessDisplay: Pick("wireless", _main.WirelessDisplayProfiles, _main.SelectedWirelessDisplayProfile, v => v.Label, v => { _main.SelectedWirelessDisplayProfile = v; Save(); }); break;
            case SetupStep.WiredDisplay: Pick("resolution", _main.ResolutionPresets, _main.SelectedResolutionPreset, v => v.Label, v => _main.SelectedResolutionPreset = v); break;
            case SetupStep.WiredFrameRate: Pick("fps", _main.FrameRates, _main.SelectedFrameRate, v => $"{v} FPS", v => _main.SelectedFrameRate = v); break;
            case SetupStep.WiredDecoder: Pick("decoder", _main.DecoderPreferences, _main.SelectedDecoderPreference ?? _main.DecoderPreferences[0], v => v.Label, v => _main.SelectedDecoderPreference = v); break;
        }
    }

    // Keep long inventories and option sets on bounded pages, including all devices.
    private FrameworkElement Paginate(IReadOnlyList<UIElement> items, int pageSize, int initialPage = 0, bool cards = true)
    {
        var host = new StackPanel();
        Panel content = cards ? CardRow() : new StackPanel();
        host.Children.Add(content);
        var pageCount = Math.Max(1, (items.Count + pageSize - 1) / pageSize);
        var page = Math.Clamp(initialPage, 0, pageCount - 1);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 8, 0, 0) };
        var previous = new Button { Content = "‹", Width = 36, Padding = new(0), FontSize = 20, Margin = new(0, 0, 12, 0) };
        var next = new Button { Content = "›", Width = 36, Padding = new(0), FontSize = 20, Margin = new(12, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(previous, L("WizardPrevious"));
        System.Windows.Automation.AutomationProperties.SetName(next, L("WizardNext"));
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        navigation.Children.Add(previous); navigation.Children.Add(label); navigation.Children.Add(next);
        if (pageCount > 1) host.Children.Add(navigation);
        void Show()
        {
            content.Children.Clear();
            foreach (var item in items.Skip(page * pageSize).Take(pageSize)) content.Children.Add(item);
            label.Text = LocalizationService.Format("SetupPageFormat", page + 1, pageCount);
            previous.IsEnabled = page > 0; next.IsEnabled = page < pageCount - 1;
            content.InvalidateMeasure();
        }
        previous.Click += (_, _) => { page--; Show(); };
        next.Click += (_, _) => { page++; Show(); };
        Show(); return host;
    }

    private void DeviceInventory()
    {
        var items = _state.Devices.Select((device, index) => (UIElement)new TextBlock
        { Text = $"{index + 1:00}   {device.Name}", FontSize = 17, Margin = new(12, 12, 12, 12), TextWrapping = TextWrapping.Wrap }).ToArray();
        Body.Children.Add(Paginate(items, 4, cards: false));
        Action("SetupScanAgain", () => { StopDiscovery(); StartDiscovery(); return Task.CompletedTask; });
    }

    private void Summary()
    {
        var devices = _state.Step == SetupStep.DeviceComplete ? new[] { _state.CurrentDevice! } : _state.Devices.ToArray();
        var items = devices.Select(device =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = device.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap });
            foreach (var pair in device.Outcomes.Where(p => p.Key is SetupStep.Profile or SetupStep.Wireless or SetupStep.Bluetooth))
                panel.Children.Add(new TextBlock { Text = (pair.Value == SetupOutcome.Verified ? "✓ " : "— ") + L("SetupTitle" + pair.Key) +
                    (pair.Value == SetupOutcome.Skipped ? " · " + L("SetupSkipped") : ""), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 4) });
            if (_state.Usage == SetupUsage.WirelessOnly) panel.Children.Add(Text(_state.HasSavedDevices ? "SetupWirelessVerified" : "SetupSkipped"));
            var border = new Border { Child = panel, Padding = new(20), CornerRadius = new(14), BorderThickness = new(1) };
            border.SetResourceReference(Border.BackgroundProperty, "CardBrush"); border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
            return (UIElement)border;
        }).ToArray();
        Body.Children.Add(Paginate(items, 1, cards: false));
        if (devices.Length == 0) Body.Children.Add(Text("SetupNoDeviceConfigured"));
        if (_state.HasSkippedSteps) Body.Children.Add(Text("SetupSkippedSummary", 13));
        if (_state.Step == SetupStep.Completed && _state.WirelessRestarted) Body.Children.Add(Text("SetupWirelessReconnectNote", 13));
    }
}
