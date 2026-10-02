using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Controls;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace IPhoneMirror.App.Controls;

public class ThemedSymbolIcon : SymbolIcon
{
    public ThemedSymbolIcon()
    {
        Loaded += (_, _) =>
        {
            // A hidden startup HWND lets ancestor bindings run before the icon
            // is in the window's visual tree. Reattach them when it is loaded.
            var binding = BindingOperations.GetBindingExpression(this, ForegroundProperty);
            if (binding is { Status: not BindingStatus.Active } &&
                binding.ParentBinding.RelativeSource?.Mode == RelativeSourceMode.FindAncestor)
                SetBinding(ForegroundProperty, binding.ParentBinding);
        };
    }

    protected override UIElement InitializeChildren()
    {
        var glyph = (WpfTextBlock)base.InitializeChildren();
        // WPF UI's glyph is only a visual child. Give it an explicit source so
        // hidden HWND creation cannot leave its inherited foreground black.
        glyph.SetBinding(WpfTextBlock.ForegroundProperty,
            new Binding(nameof(Foreground)) { Source = this, Mode = BindingMode.OneWay });
        return glyph;
    }
}
