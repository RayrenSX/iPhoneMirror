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
    double CurveExponent,
    double GdiRadiusRatio)
{
    internal static readonly DeviceCornerProfile Rectangular = new(
        "rectangular", false, 0.0, 2.0, 0.0);

    /// <summary>Radius in pixels for a short edge of <paramref name="shortEdge"/> pixels.</summary>
    internal int GetGdiRadius(int shortEdge, double dpiScale = 1.0)
    {
        if (!IsRounded || shortEdge <= 0) return 0;
        var fitted = shortEdge * Math.Clamp(GdiRadiusRatio, 0.0, 0.5);
        // Keep a tiny resized preview anti-aliased enough to avoid a visibly
        // square corner, but never let the floor dominate a normal window.
        var minimum = Math.Min(shortEdge * 0.20, 6.0 * Math.Max(0.5, dpiScale));
        return Math.Max(1, (int)Math.Round(Math.Max(fitted, minimum)));
    }
}
