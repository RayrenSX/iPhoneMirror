using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Maps Apple's hardware ProductType plus the decoded picture geometry to a
/// display-outline profile. ProductType wins when known; geometry is only a
/// conservative forward-compatibility fallback for future devices.
/// </summary>
internal static class DeviceCornerProfileResolver
{
    // These are visual curve fits, not Apple-published physical dimensions.
    // Apple publishes device-specific product-bezel artwork and confirms in
    // Technical Specifications whether a display has rounded corners, but it
    // does not publish a numeric display corner radius. Keep the constants in
    // one place so a future official bezel can be calibrated without touching
    // renderer/window code.
    internal static readonly DeviceCornerProfile IPhoneX = new(
        "iphone-x", true, 0.1460, 2.15, 0.1320);
    internal static readonly DeviceCornerProfile IPhoneNotch = new(
        "iphone-notch", true, 0.1580, 2.25, 0.1420);
    internal static readonly DeviceCornerProfile IPhone12Mini = new(
        "iphone-12-mini", true, 0.1180, 2.18, 0.1080);
    internal static readonly DeviceCornerProfile IPhone13Mini = new(
        "iphone-13-mini", true, 0.1240, 2.20, 0.1130);
    internal static readonly DeviceCornerProfile IPhone12Standard = new(
        "iphone-12-standard", true, 0.1390, 2.22, 0.1260);
    internal static readonly DeviceCornerProfile IPhone12Max = new(
        "iphone-12-max", true, 0.1320, 2.22, 0.1200);
    internal static readonly DeviceCornerProfile IPhoneDynamicIsland = new(
        // The main compact preview is an opaque child HWND. Its region must
        // follow the shader's curve; a smaller region radius exposes a black
        // strip between the child silhouette and the anti-aliased picture.
        "iphone-dynamic-island", true, 0.1784, 2.36, 0.1784);
    // Model-specific visual fits against the checked-in screen masks. These
    // are measured artwork fits, not Apple-published mechanical dimensions.
    // See docs/diagnostics/recent-iphone-corners-20261117.md for provenance.
    internal static readonly DeviceCornerProfile IPhone16 = new(
        "iphone-16", true, 0.17700, 2.6358, 0.17700);
    internal static readonly DeviceCornerProfile IPhone16Plus = new(
        "iphone-16-plus", true, 0.17090, 2.8106, 0.17090);
    internal static readonly DeviceCornerProfile IPhone16Pro = new(
        "iphone-16-pro", true, 0.19272, 2.5886, 0.19272);
    internal static readonly DeviceCornerProfile IPhone16ProMax = new(
        "iphone-16-pro-max", true, 0.17100, 2.5040, 0.17100);
    internal static readonly DeviceCornerProfile IPhone17 = new(
        "iphone-17", true, 0.19590, 2.6304, 0.19590);
    internal static readonly DeviceCornerProfile IPhone17Pro = new(
        "iphone-17-pro", true, 0.19452, 2.6086, 0.19452);
    internal static readonly DeviceCornerProfile IPhone17ProMax = new(
        "iphone-17-pro-max", true, 0.17832, 2.5876, 0.17832);
    internal static readonly DeviceCornerProfile IPhoneAir = new(
        "iphone-air", true, 0.18298, 2.4729, 0.18298);
    internal static readonly DeviceCornerProfile IPhone18Pro = new(
        "iphone-18-pro", true, 0.19058, 2.5518, 0.19058);
    internal static readonly DeviceCornerProfile IPhone18ProMax = new(
        "iphone-18-pro-max", true, 0.17698, 2.5706, 0.17698);
    internal static readonly DeviceCornerProfile IPhoneDuoInner = new(
        "iphone-duo-inner", true, 0.09698, 2.5022, 0.09698);
    internal static readonly DeviceCornerProfile IPhoneModernNotch = new(
        "iphone-modern-notch", true, 0.1580, 2.25, 0.1580);
    internal static readonly DeviceCornerProfile IPadPro = new(
        "ipad-pro-rounded", true, 0.0430, 2.12, 0.0390);
    internal static readonly DeviceCornerProfile IPadAir = new(
        "ipad-air-rounded", true, 0.0390, 2.08, 0.0360);
    internal static readonly DeviceCornerProfile IPadMini = new(
        "ipad-mini-rounded", true, 0.0520, 2.12, 0.0470);
    internal static readonly DeviceCornerProfile IPadBase = new(
        "ipad-rounded", true, 0.0380, 2.08, 0.0350);

    private static readonly HashSet<(int Major, int Minor)> RectangularIPhones =
    [
        // iPhone 8 / 8 Plus members of the mixed iPhone10 generation.
        (10, 1), (10, 2), (10, 4), (10, 5),
        // iPhone SE (2nd and 3rd generation).
        (12, 8), (14, 6),
    ];

    private static readonly HashSet<(int Major, int Minor)> NotchedModernIPhones =
    [
        // e models retain a notch while their generation's other models use
        // Dynamic Island. Their project-drawn bezels do not establish a new
        // physical radius, so retain the conservative notch curve.
        (17, 5), (18, 5),
    ];

    internal static DeviceCornerProfile Resolve(
        string? productType,
        uint frameWidth = 0,
        uint frameHeight = 0)
    {
        if (TryParseProductType(productType, "iPhone", out var phoneMajor, out var phoneMinor))
            return ResolveIPhone(phoneMajor, phoneMinor, frameWidth, frameHeight);

        if (TryParseProductType(productType, "iPad", out var padMajor, out var padMinor))
            return ResolveIPad(padMajor, padMinor);

        // Apple TV, iPod and known non-mobile identifiers are rectangular.
        if (!string.IsNullOrWhiteSpace(productType)) return DeviceCornerProfile.Rectangular;

        return ResolveByGeometry(frameWidth, frameHeight);
    }

    private static DeviceCornerProfile ResolveIPhone(int major, int minor, uint width, uint height)
    {
        if (major < 10 || RectangularIPhones.Contains((major, minor)))
            return DeviceCornerProfile.Rectangular;

        // iPhone X shares the iPhone10 generation with iPhone 8, so it must be
        // selected by minor identifier rather than the major generation.
        if (major == 10) return minor is 3 or 6
            ? IPhoneX
            : DeviceCornerProfile.Rectangular;

        if (major == 13) return minor switch
        {
            1 => IPhone12Mini,
            2 or 3 => IPhone12Standard,
            4 => IPhone12Max,
            _ => IPhoneNotch,
        };
        if (major == 14) return minor switch
        {
            4 => IPhone13Mini,
            3 or 5 or 7 or 8 => IPhone12Standard,
            2 => IPhone12Max,
            _ => IPhoneNotch,
        };

        // ProductType iPhone15,* starts with iPhone 14 Pro and is the first
        // all-Dynamic-Island generation. Later identifiers inherit that fit.
        if (NotchedModernIPhones.Contains((major, minor))) return IPhoneModernNotch;
        if (major == 17) return minor switch
        {
            1 => IPhone16Pro,
            2 => IPhone16ProMax,
            3 => IPhone16,
            4 => IPhone16Plus,
            _ => IPhoneDynamicIsland,
        };
        if (major == 18) return minor switch
        {
            1 => IPhone17Pro,
            2 => IPhone17ProMax,
            3 => IPhone17,
            4 => IPhoneAir,
            _ => IPhoneDynamicIsland,
        };
        if (major == 19) return minor switch
        {
            2 => IPhone18Pro,
            3 or 7 => IPhone18ProMax,
            4 => ResolveDuo(width, height),
            _ => IPhoneDynamicIsland,
        };
        if (major >= 15) return IPhoneDynamicIsland;
        return IPhoneNotch;
    }

    private static DeviceCornerProfile ResolveDuo(uint width, uint height)
    {
        if (width == 0 || height == 0) return DeviceCornerProfile.Rectangular;
        // The two panels share ProductType. Use the same midpoint between
        // catalog panel aspects as DeviceFrameCatalog; reject other shapes.
        const double outerAspect = 1398d / 2034;
        const double innerAspect = 2007d / 2853;
        var aspect = Math.Min(width, height) / (double)Math.Max(width, height);
        var midpoint = (outerAspect + innerAspect) * .5;
        if (aspect >= midpoint && Math.Abs(aspect - innerAspect) < (innerAspect - outerAspect) * .5)
            return IPhoneDuoInner;
        // The official outer-panel mask has asymmetric left/right corners.
        // A single symmetric radius would discard valid pixels. Until that
        // shape is supported, retain the source instead of over-clipping it.
        return DeviceCornerProfile.Rectangular;
    }

    private static DeviceCornerProfile ResolveIPad(int major, int minor)
    {
        // iPad8,* is the 2018/2020 all-screen Pro family. iPad9 through iPad12
        // retain the rectangular Home-button display. The all-screen Air/Pro/
        // mini/base families resume at iPad13,*.
        if (major == 8) return IPadPro;
        if (major < 13) return DeviceCornerProfile.Rectangular;

        if (major == 13)
        {
            if (minor is 1 or 2 or 16 or 17) return IPadAir;
            if (minor is 18 or 19) return IPadBase;
            return IPadPro;
        }

        if (major == 14)
        {
            if (minor is 1 or 2) return IPadMini;
            if (minor is >= 8 and <= 11) return IPadAir;
            return IPadPro;
        }
        if (major == 15)
        {
            if (minor is >= 3 and <= 6) return IPadAir;
            if (minor is 7 or 8) return IPadBase;
            return IPadPro;
        }
        if (major == 16)
        {
            if (minor is 1 or 2) return IPadMini;
            if (minor is >= 8 and <= 11) return IPadAir;
            return IPadPro;
        }
        // iPad17,* begins with the M5 Pro family. Future unknown all-screen
        // iPads use a moderate generic fit rather than clipping like a phone.
        if (major == 17) return IPadPro;
        if (major == 19 && minor is 1 or 2) return IPadMini;
        if (major > 17) return IPadBase;
        return IPadPro;
    }

    private static DeviceCornerProfile ResolveByGeometry(uint width, uint height)
    {
        if (width == 0 || height == 0) return DeviceCornerProfile.Rectangular;
        var shortEdge = Math.Min(width, height);
        var longEdge = Math.Max(width, height);
        var ratio = shortEdge / (double)longEdge;

        // Current full-screen iPhones are tall (roughly 0.42-0.50). Current
        // rounded iPads are much wider (roughly 0.69-0.75). Gaps deliberately
        // remain rectangular so an unusual future stream is not over-clipped.
        if (ratio is >= 0.38 and <= 0.56) return IPhoneDynamicIsland;
        if (ratio is >= 0.64 and <= 0.80) return IPadBase;
        return DeviceCornerProfile.Rectangular;
    }

    private static bool TryParseProductType(
        string? value,
        string prefix,
        out int major,
        out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var suffix = value.AsSpan(prefix.Length);
        var comma = suffix.IndexOf(',');
        if (comma <= 0 || comma == suffix.Length - 1) return false;
        return int.TryParse(suffix[..comma], out major) &&
            int.TryParse(suffix[(comma + 1)..], out minor) &&
            major >= 0 && minor >= 0;
    }
}
