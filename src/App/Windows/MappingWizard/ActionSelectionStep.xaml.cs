using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using Wpf.Ui.Controls;
namespace IPhoneMirror.App.Windows.MappingWizard;
public partial class ActionSelectionStep : UserControl
{
    public ActionSelectionStep() => InitializeComponent();
}
internal sealed record MappingActionOption(MappedTouchAction Value, SymbolRegular Icon)
{
    public string Label => LocalizationService.Get("MappingAction" + Value);
    public string Hint => LocalizationService.Get("WizardActionHint" + Value);
}
