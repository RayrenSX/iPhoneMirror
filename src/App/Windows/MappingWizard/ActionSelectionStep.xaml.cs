using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using Wpf.Ui.Controls;
namespace IPhoneMirror.App.Windows.MappingWizard;
public partial class ActionSelectionStep : UserControl
{
    public ActionSelectionStep() => InitializeComponent();
}
internal sealed record MappingInputOption(MappingInputKind Kind, int Button = 1)
{
    public string Label => LocalizationService.Get(Kind == MappingInputKind.MouseButton ? "MappingMouse" + Button : "Mapping" + Kind);
}
internal sealed record MappingActionOption(MappedTouchAction Value, SymbolRegular Icon)
{
    public string Label => LocalizationService.Get("MappingAction" + Value);
    public string Hint => LocalizationService.Get("WizardActionHint" + Value);
}
