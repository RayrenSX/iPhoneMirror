using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.UI.Controls;

public class ModernCard : ContentControl
{
    public static readonly DependencyProperty IsInteractiveProperty =
        DependencyProperty.Register(nameof(IsInteractive), typeof(bool),
            typeof(ModernCard), new PropertyMetadata(true));

    public bool IsInteractive
    {
        get => (bool)GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }
}
