using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace IPhoneMirror.UI.Controls;

/// <summary>The library's generated selection template uses unwrapped text even
/// with DisplayMemberPath. Keep its data/template support while allowing reflow.</summary>
public static class ComboBoxTextLayout
{
    public static readonly DependencyProperty WrapSelectionProperty = DependencyProperty.RegisterAttached(
        "WrapSelection", typeof(bool), typeof(ComboBoxTextLayout), new PropertyMetadata(false, OnChanged));

    public static bool GetWrapSelection(DependencyObject element) => (bool)element.GetValue(WrapSelectionProperty);
    public static void SetWrapSelection(DependencyObject element, bool value) => element.SetValue(WrapSelectionProperty, value);

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ComboBox combo) return;
        if ((bool)args.NewValue)
        {
            combo.Loaded += OnLoaded;
            combo.SelectionChanged += OnSelectionChanged;
            if (combo.IsLoaded) Schedule(combo);
        }
        else
        {
            combo.Loaded -= OnLoaded;
            combo.SelectionChanged -= OnSelectionChanged;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Schedule((ComboBox)sender);
    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs args) => Schedule((ComboBox)sender);
    private static void Schedule(ComboBox combo) => combo.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
    {
        if (combo.IsLoaded && GetWrapSelection(combo)) WrapText(combo);
    }));

    private static void WrapText(DependencyObject element)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is TextBlock text && !text.FontFamily.Source.Contains("Fluent") && !text.FontFamily.Source.Contains("MDL2"))
                text.SetCurrentValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            else WrapText(child);
        }
    }
}
