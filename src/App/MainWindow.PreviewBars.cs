using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private void UpdatePreviewStatusPresentation()
    {
        foreach (var item in StatisticsItems.Children.OfType<StackPanel>())
        {
            var compact = _viewModel.HideInterfaceDescriptions;
            item.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
            item.Margin = compact ? new Thickness(10, 9, 10, 9) : new Thickness(13);
            item.MinWidth = compact ? 0 : 125;
            var texts = item.Children.OfType<TextBlock>().ToArray();
            if (texts.Length < 2) continue;
            var label = texts[0];
            var value = texts[1];
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.None;
            value.VerticalAlignment = VerticalAlignment.Center;
            value.TextWrapping = TextWrapping.NoWrap;
            value.TextTrimming = TextTrimming.None;
            if (compact)
            {
                value.SetResourceReference(TextBlock.FontSizeProperty, "BodyFontSize");
                value.Margin = new Thickness(8, 0, 0, 0);
            }
            else
            {
                value.ClearValue(TextBlock.FontSizeProperty);
                value.ClearValue(MarginProperty);
            }
            // Numeric data and its unit always remain on one line. Descriptions
            // can wrap inside a card, never splitting the value itself.
            foreach (var description in texts.Skip(2))
            {
                description.MaxWidth = 220 * WorkspaceReadingScale;
                if (compact) description.Visibility = Visibility.Collapsed;
                else description.SetResourceReference(VisibilityProperty, "InterfaceDescriptionsVisibility");
            }
        }
        UpdateStatisticsLayout();
    }
}
