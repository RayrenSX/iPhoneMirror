using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
namespace IPhoneMirror.App.Windows.MappingWizard;
public partial class ParameterStep : UserControl
{
    public ParameterStep() => InitializeComponent();
}
internal sealed record MappingSwipeDirectionOption(MappedTouchAction Value)
{
    public string Label => LocalizationService.Get(Value == MappedTouchAction.Swipe ? "MappingSwipeDirectionFree" : "MappingAction" + Value);
}
