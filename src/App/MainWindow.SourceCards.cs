using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private void OnSourceCardOptionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { Placement = PlacementMode.Bottom, PlacementTarget = button };
        menu.SetResourceReference(StyleProperty, "DeviceContextMenuStyle");
        menu.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        var reset = new MenuItem();
        AddField(SourceCardFields.Icon, "SourceCardOptionIcon");
        AddField(SourceCardFields.Name, "SourceCardOptionName");
        AddField(SourceCardFields.Model, "SourceCardOptionModel");
        AddField(SourceCardFields.SystemVersion, "SourceCardOptionSystemVersion");
        AddField(SourceCardFields.Status, "SourceCardOptionStatus");
        AddField(SourceCardFields.Transport, "SourceCardOptionTransport");
        menu.Items.Add(new Separator());

        reset.SetResourceReference(HeaderedItemsControl.HeaderProperty, "ToolbarReset");
        reset.SetResourceReference(StyleProperty, "DeviceMenuItemStyle");
        reset.Click += (_, _) => ApplyFields(SourceCardFields.Default);
        menu.Items.Add(reset);
        UpdateMenu();
        button.ContextMenu = menu;
        menu.IsOpen = true;

        void AddField(SourceCardFields field, string label)
        {
            var item = new MenuItem { Tag = field, IsCheckable = true, StaysOpenOnClick = true };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, label);
            item.Style = (Style)button.FindResource("SourceCardOptionMenuItem");
            ToolTipService.SetShowOnDisabled(item, true);
            item.Click += (_, _) =>
            {
                if (!item.IsEnabled) return;
                ApplyFields(_viewModel.VisibleSourceCardFields ^ field);
            };
            menu.Items.Add(item);
        }

        void UpdateMenu()
        {
            var fields = _viewModel.VisibleSourceCardFields;
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (item.Tag is not SourceCardFields field) continue;
                item.IsChecked = fields.HasFlag(field);
                item.IsEnabled = fields != field;
                item.ToolTip = fields == field ? LocalizationService.Get("SourceCardKeepOneOption") : null;
            }
            reset.IsEnabled = fields != SourceCardFields.Default;
        }

        void ApplyFields(SourceCardFields fields)
        {
            var saved = _viewModel.TrySetSourceCardFields(fields);
            UpdateMenu();
            if (saved) return;
            menu.IsOpen = false;
            AppPromptWindow.Inform(LocalizationService.Get("SourceCardOptionsTitle"), LocalizationService.Get("SetupSaveFailed"), this);
        }
    }
}
