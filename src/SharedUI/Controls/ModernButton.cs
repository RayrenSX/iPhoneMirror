using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.UI.Controls;

public class ModernButton : Button
{
    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool),
            typeof(ModernButton), new PropertyMetadata(false));

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }
}
