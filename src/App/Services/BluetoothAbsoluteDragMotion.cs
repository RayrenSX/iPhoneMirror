using System.Text.Json;

namespace IPhoneMirror.App.Services;

// Absolute HID positions do not move AssistiveTouch's active contact. During
// a drag use short relative reports, predicting their accelerated distance in
// device points instead of treating normalized coordinates as mouse counts.
internal static class BluetoothAbsoluteDragMotion
{
    internal const int MaximumCounts = 48;
    internal static (double X, double Y) Predict(short x, short y)
    {
        // Short-report curve measured on the validation phone (iOS 18.7.8).
        // Short and medium reports were measured on the validation phone.
        // Invert their accelerated travel instead of replaying many tiny
        // reports, each of which costs another BLE connection interval.
        // Other AssistiveTouch tracking settings can change this curve.
        var length = Math.Floor(Math.Sqrt(x * x + y * y));
        var gain = .22 + .047 * length;
        return (x * gain, y * gain);
    }

    internal static (short X, short Y) Next(double x, double y)
    {
        var best = (X: (short)0, Y: (short)0);
        var error = x * x + y * y;
        for (short dx = -MaximumCounts; dx <= MaximumCounts; dx++)
        for (short dy = -MaximumCounts; dy <= MaximumCounts; dy++)
        {
            if (dx * dx + dy * dy > MaximumCounts * MaximumCounts) continue;
            var predicted = Predict(dx, dy);
            var candidate = Math.Pow(x - predicted.X, 2) + Math.Pow(y - predicted.Y, 2);
            if (candidate >= error) continue;
            best = (dx, dy);
            error = candidate;
        }
        return best;
    }

    private static readonly Lazy<Dictionary<string, double>> ShortEdges = new(() =>
    {
        using var stream = typeof(BluetoothAbsoluteDragMotion).Assembly.GetManifestResourceStream(
            "IPhoneMirror.App.Devices.AppleDisplayCatalog.json")!;
        using var json = JsonDocument.Parse(stream);
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in json.RootElement.GetProperty("products").EnumerateArray())
        foreach (var id in product.GetProperty("identifiers").EnumerateArray())
        {
            var model = id.GetString()!;
            var pixels = product.GetProperty("width").GetInt32();
            result[model] = model.StartsWith("iPad", StringComparison.Ordinal)
                ? pixels > 1024 ? pixels / 2.0 : pixels
                : model is "iPhone13,1" or "iPhone14,4" ? 375 : pixels switch
                {
                    320 or 640 => 320, 750 => 375, 828 => 414, 1080 or 1242 => 414,
                    1125 => 375, 1170 => 390, 1179 => 393, 1206 => 402,
                    1284 => 428, 1290 => 430, 1320 => 440,
                    _ => pixels >= 1000 ? pixels / 3.0 : pixels / 2.0,
                };
        }
        return result;
    });

    internal static (double Width, double Height) Geometry(string? productType,
        uint width, uint height, int previewRotation)
    {
        if ((previewRotation & 1) != 0) (width, height) = (height, width);
        var shortEdge = productType is not null && ShortEdges.Value.TryGetValue(productType, out var known)
            ? known : productType?.StartsWith("iPad", StringComparison.Ordinal) == true ? 768 : 375;
        if (width == 0 || height == 0) return (shortEdge, shortEdge * 812 / 375);
        var scale = shortEdge / Math.Min(width, height);
        return (width * scale, height * scale);
    }
}
