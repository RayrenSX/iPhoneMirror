using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IPhoneMirror.App.Services;

internal sealed record DeviceFrameProfile(string Id, string Name, string[] ProductTypes,
    int FrameWidth, int FrameHeight, int ScreenX, int ScreenY, int ScreenWidth, int ScreenHeight,
    DeviceFrameColor[]? Colors = null, string DefaultColorId = "black", DeviceFrameProfile[]? DisplayModes = null,
    string ArtworkKind = "")
{
    private WeakReference<byte[]>? _pixels;
    private WeakReference<byte[]>? _mask;
    internal byte[] Pixels
    {
        get
        {
            if (_pixels?.TryGetTarget(out var cached) == true) return cached;
            var pixels = ReadPixels(Id + ".png", PixelFormats.Pbgra32, 4);
            _pixels = new(pixels);
            return pixels;
        }
    }
    internal byte[] Mask
    {
        get
        {
            if (_mask?.TryGetTarget(out var cached) == true) return cached;
            var mask = ReadPixels(Id + "-screen.png", PixelFormats.Gray8, 1);
            _mask = new(mask);
            return mask;
        }
    }

    private byte[] ReadPixels(string file, PixelFormat format, int bytesPerPixel)
    {
        using var stream = typeof(DeviceFrameCatalog).Assembly.GetManifestResourceStream(
            "IPhoneMirror.App.DeviceFrames." + file) ?? throw new InvalidDataException("Missing device frame: " + file);
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (bitmap.PixelWidth != FrameWidth || bitmap.PixelHeight != FrameHeight)
            throw new InvalidDataException("Incorrect device frame dimensions: " + file);
        var converted = new FormatConvertedBitmap(bitmap, format, null, 0);
        var bytes = new byte[FrameWidth * FrameHeight * bytesPerPixel];
        converted.CopyPixels(bytes, FrameWidth * bytesPerPixel, 0);
        return bytes;
    }

    internal Rect RotatedScreen(int turns)
    {
        double x = ScreenX / (double)FrameWidth, y = ScreenY / (double)FrameHeight;
        double w = ScreenWidth / (double)FrameWidth, h = ScreenHeight / (double)FrameHeight;
        for (int i = 0; i < (turns % 4 + 4) % 4; ++i) (x, y, w, h) = (1 - y - h, x, h, w);
        return new Rect(x, y, w, h);
    }
}

internal sealed record DeviceFrameColor(string Id, string AssetId, string[] Tokens,
    int FrameWidth = 0, int FrameHeight = 0, int ScreenX = 0, int ScreenY = 0, int ScreenWidth = 0, int ScreenHeight = 0);
internal sealed record DeviceFrameSelection(DeviceFrameProfile? Profile, string ColorId, bool Detected,
    bool Manual = false, string? ProductType = null);
internal sealed record DeviceFrameColorOption(string Id, string Label);

internal static class DeviceFrameCatalog
{
    internal static IReadOnlyList<DeviceFrameProfile> Profiles { get; } = Load();
    internal static IReadOnlyList<DeviceFrameProfile> AllProfiles { get; } = Profiles
        .SelectMany(profile => new[] { profile }.Concat(profile.DisplayModes ?? [])).ToArray();

    internal static DeviceFrameProfile? Find(string? productType, uint sourceWidth = 0, uint sourceHeight = 0)
    {
        var profile = Profiles.FirstOrDefault(profile => profile.ProductTypes.Contains(productType, StringComparer.Ordinal));
        if (profile?.DisplayModes is not { Length: > 0 } modes || sourceWidth == 0 || sourceHeight == 0) return profile;
        // A foldable's known panels share one ProductType. Compare only that
        // model's original panel artwork, independent of orientation/scale.
        var aspect = Math.Min(sourceWidth, sourceHeight) / (double)Math.Max(sourceWidth, sourceHeight);
        return modes.Prepend(profile).MinBy(mode => Math.Abs(aspect - mode.ScreenWidth / (double)mode.ScreenHeight));
    }

    private static readonly Dictionary<string, DeviceFrameProfile> Variants = AllProfiles
        .SelectMany(profile => (profile.Colors ?? []).Select(color =>
            new KeyValuePair<string, DeviceFrameProfile>(color.AssetId,
                color.AssetId == profile.Id ? profile : new DeviceFrameProfile(color.AssetId, profile.Name, profile.ProductTypes,
                    color.FrameWidth == 0 ? profile.FrameWidth : color.FrameWidth,
                    color.FrameHeight == 0 ? profile.FrameHeight : color.FrameHeight,
                    color.FrameWidth == 0 ? profile.ScreenX : color.ScreenX,
                    color.FrameHeight == 0 ? profile.ScreenY : color.ScreenY,
                    color.ScreenWidth == 0 ? profile.ScreenWidth : color.ScreenWidth,
                    color.ScreenHeight == 0 ? profile.ScreenHeight : color.ScreenHeight, profile.Colors, profile.DefaultColorId,
                    ArtworkKind: profile.ArtworkKind))))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    internal static DeviceFrameSelection Resolve(string? productType, DeviceAppearance appearance,
        uint sourceWidth = 0, uint sourceHeight = 0)
    {
        var profile = Find(productType, sourceWidth, sourceHeight);
        if (profile is null) return new(null, "black", false, ProductType: productType);
        var manual = (profile.Colors ?? []).FirstOrDefault(color => color.Id == appearance.SelectedColorId);
        if (manual is not null) return new(Variants[manual.AssetId], manual.Id, false, true, productType);
        var tokens = new[] { appearance.HousingColor, appearance.EnclosureColor }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant()).ToArray();
        // DeviceColor is front glass, not the enclosure. Never use it as a fallback.
        var matches = (profile.Colors ?? []).Where(color =>
            color.Tokens.Any(token => tokens.Contains(token, StringComparer.Ordinal))).ToArray();
        if (matches.Length != 1) return new(profile, profile.DefaultColorId, false, ProductType: productType);
        var selected = matches[0];
        return new(Variants[selected.AssetId], selected.Id, true, ProductType: productType);
    }

    internal static string Describe(DeviceFrameSelection selection)
    {
        if (selection.Profile is null) return Localization.LocalizationService.Get("IndependentWindowPhoneFrameUnavailable");
        var color = ColorLabel(selection.ColorId);
        var model = string.IsNullOrWhiteSpace(selection.ProductType)
            ? selection.Profile.Name : AppleProductNames.Resolve(selection.ProductType);
        var description = Localization.LocalizationService.Format(selection.Manual ? "DeviceFrameManualFormat" : selection.Detected
            ? "IndependentWindowPhoneFrameDetectedFormat" : "IndependentWindowPhoneFrameDefaultFormat", model, color);
        return selection.Profile.ArtworkKind == "self-drawn"
            ? description + Localization.LocalizationService.Get("DeviceFrameDrawnSuffix") : description;
    }

    internal static string ColorLabel(string id) => Localization.LocalizationService.Get("DeviceFrameColor_" + id);

    internal static DeviceFrameColorOption[] ColorOptions(string? productType) =>
        new[] { new DeviceFrameColorOption("", Localization.LocalizationService.Get("DeviceFrameColorAuto")) }
            .Concat((Find(productType)?.Colors ?? []).Select(c => new DeviceFrameColorOption(c.Id, ColorLabel(c.Id)))).ToArray();

    private static IReadOnlyList<DeviceFrameProfile> Load()
    {
        using var stream = typeof(DeviceFrameCatalog).Assembly.GetManifestResourceStream(
            "IPhoneMirror.App.DeviceFrames.catalog.json")!;
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Invalid frame catalog.");
        return json.RootElement.GetProperty("frames").Deserialize<DeviceFrameProfile[]>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
