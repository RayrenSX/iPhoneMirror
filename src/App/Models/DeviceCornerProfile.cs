namespace IPhoneMirror.App.Models;

/// <summary>
/// A visual fit for the active display outline, expressed independently of
/// pixels so the same curve follows window resize, source rotation and local
/// render-size presets.
/// </summary>
internal readonly record struct DeviceCornerProfile(
    string Id,
    bool IsRounded,
    double RadiusRatio,
    double CurveExponent)
{
    internal static readonly DeviceCornerProfile Rectangular = new(
        "rectangular", false, 0.0, 2.0);
}
