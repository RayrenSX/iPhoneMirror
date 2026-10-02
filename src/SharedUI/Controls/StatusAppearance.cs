using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace IPhoneMirror.UI.Controls;

public enum StatusTone { Neutral, Info, Success, Warning, Error, Disabled }

/// <summary>Semantic colors use dynamic resources so open status views follow theme changes.</summary>
public static class StatusAppearance
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.RegisterAttached(
        "Tone", typeof(StatusTone), typeof(StatusAppearance),
        new PropertyMetadata(StatusTone.Neutral, OnToneChanged));
    public static StatusTone GetTone(DependencyObject target) => (StatusTone)target.GetValue(ToneProperty);
    public static void SetTone(DependencyObject target, StatusTone value) => target.SetValue(ToneProperty, value);

    private static void OnToneChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var tone = (StatusTone)args.NewValue;
        var brush = tone switch
        {
            StatusTone.Info => "InfoBrush", StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush", StatusTone.Error => "ErrorBrush",
            StatusTone.Disabled => "DisabledTextBrush", _ => "MutedTextBrush",
        };
        if (target is Border border)
        {
            border.SetResourceReference(Border.BorderBrushProperty, brush);
            border.SetResourceReference(Border.BackgroundProperty, tone switch
            {
                StatusTone.Info => "InfoSurfaceBrush", StatusTone.Success => "SuccessSurfaceBrush",
                StatusTone.Warning => "WarningSurfaceBrush", StatusTone.Error => "ErrorSurfaceBrush",
                _ => "ControlFillBrush",
            });
        }
        else if (target is System.Windows.Shapes.Shape shape) shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        else if (target is TextBlock text) text.SetResourceReference(TextBlock.ForegroundProperty, brush);
        else if (target is FrameworkElement element) element.SetResourceReference(TextElement.ForegroundProperty, brush);
    }
}
