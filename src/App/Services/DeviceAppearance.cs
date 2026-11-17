namespace IPhoneMirror.App.Services;

internal sealed record DeviceAppearance(string FrontColor, string EnclosureColor, string HousingColor, string? SelectedColorId = null)
{
    internal static DeviceAppearance Unknown { get; } = new("", "", "");
}
