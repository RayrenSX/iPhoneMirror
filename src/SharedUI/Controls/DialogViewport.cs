using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IPhoneMirror.UI.Controls;

/// <summary>Keeps the dialog's normal bounded content layout, with a page scrollbar
/// when large text or a short work area leaves no room for its header and actions.</summary>
public class DialogViewport : ScrollViewer
{
    internal double AvailableHeight { get; private set; } = double.PositiveInfinity;

    protected override Size MeasureOverride(Size constraint)
    {
        AvailableHeight = constraint.Height;
        return base.MeasureOverride(constraint);
    }

    internal static double MinimumHeight(FrameworkElement element)
    {
        if (element.Visibility == Visibility.Collapsed) return 0;
        var margin = element.Margin.Top + element.Margin.Bottom;
        if (element is ScrollViewer or FlowDocumentScrollViewer)
            return Math.Max(element.MinHeight,
                element.TryFindResource("DialogContentMinHeight") is double minimum ? minimum : 120) + margin;
        if (element is Border { Child: FrameworkElement child } border)
            return Math.Max(element.MinHeight, MinimumHeight(child) + border.Padding.Top +
                border.Padding.Bottom + border.BorderThickness.Top + border.BorderThickness.Bottom) + margin;
        if (element is Grid grid && grid.RowDefinitions.Count > 0)
        {
            var rows = grid.RowDefinitions.Select(row => row.Height.IsAbsolute
                ? row.Height.Value : row.MinHeight).ToArray();
            foreach (var item in grid.Children.OfType<FrameworkElement>().Where(item => item.Visibility != Visibility.Collapsed))
            {
                var row = Math.Min(Grid.GetRow(item), rows.Length - 1);
                var span = Math.Min(Grid.GetRowSpan(item), rows.Length - row);
                var needed = grid.RowDefinitions[row].Height.IsAuto ? item.DesiredSize.Height : MinimumHeight(item);
                var deficit = needed - rows.Skip(row).Take(span).Sum();
                var growable = Enumerable.Range(row, span).Where(i => !grid.RowDefinitions[i].Height.IsAbsolute).ToArray();
                if (deficit > 0 && growable.Length > 0)
                    foreach (var index in growable) rows[index] += deficit / growable.Length;
            }
            return Math.Max(element.MinHeight, rows.Sum()) + margin;
        }
        // Follow presenters and tab templates to their layout grid. StackPanels
        // already measure their vertical content at its natural height.
        if (element is ContentPresenter or TabControl)
        {
            if (VisualTreeHelper.GetChildrenCount(element) > 0 &&
                VisualTreeHelper.GetChild(element, 0) is FrameworkElement content)
                return Math.Max(element.MinHeight, MinimumHeight(content)) + margin;
        }
        return element.DesiredSize.Height;
    }
}

public class DialogLayoutPresenter : ContentPresenter
{
    protected override Size MeasureOverride(Size constraint)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not DialogViewport viewport) continue;
            constraint.Height = Math.Min(constraint.Height, viewport.AvailableHeight);
            break;
        }
        var measured = base.MeasureOverride(constraint);
        // A Grid reports a constrained DesiredSize even when its Auto rows and
        // minimum content row exceed that constraint. In that case let the page
        // flow naturally; the outer viewer owns scrolling, including the actions.
        if (VisualTreeHelper.GetChildrenCount(this) > 0 &&
            VisualTreeHelper.GetChild(this, 0) is FrameworkElement root)
        {
            var required = DialogViewport.MinimumHeight(root);
            if (required > constraint.Height)
                measured = base.MeasureOverride(new Size(constraint.Width, double.PositiveInfinity));
        }
        return measured;
    }
}
