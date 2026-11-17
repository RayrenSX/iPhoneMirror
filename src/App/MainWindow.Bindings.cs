using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private void InitializeWorkspaceBindings()
    {
        // The tray creates an HWND before the workspace joins the visual tree.
        // Cross-element sources must survive that hidden initialization and the
        // Popup's separate tree without waiting for ancestor/name resolution.
        InitializeSettingsCategoryVisibility();
        VersionLabel.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(VersionText)) { Source = this, Mode = BindingMode.OneWay });
        PreviewTransitionMask.PlacementTarget = PreviewPanel;
        PreviewTransitionMaskContent.SetBinding(WidthProperty,
            new Binding(nameof(ActualWidth)) { Source = PreviewPanel, Mode = BindingMode.OneWay });
        PreviewTransitionMaskContent.SetBinding(HeightProperty,
            new Binding(nameof(ActualHeight)) { Source = PreviewPanel, Mode = BindingMode.OneWay });
    }
}
