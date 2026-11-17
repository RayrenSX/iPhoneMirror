using System.IO;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDeviceCornerClippingTests(string output)
    {
        Directory.CreateDirectory(output);
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var assembly = typeof(App).Assembly;
        var resolver = assembly.GetType("IPhoneMirror.App.Services.DeviceCornerProfileResolver")!;
        var profile = resolver.GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["iPhone18,3", 994u, 2160u])!;
        var profileType = profile.GetType();
        var radiusRatio = (double)profileType.GetProperty("RadiusRatio", members)!.GetValue(profile)!;
        var exponent = (double)profileType.GetProperty("CurveExponent", members)!.GetValue(profile)!;
        var radiusMethod = profileType.GetMethod("GetGdiRadius", members)!;
        using var host = (HwndHost)Activator.CreateInstance(
            assembly.GetType("IPhoneMirror.App.Controls.NativePreviewHost")!, nonPublic: true)!;
        var createRegion = host.GetType().GetMethod("CreateDeviceCornerRegion", members)!;
        var results = new List<object>();
        var maximumBlackBandPixels = 0;
        foreach (var (width, height, dpi) in new[]
        {
            (240, 522, 1d), (370, 805, 1d), (370, 805, 1.25), (480, 1044, 1.5),
            (742, 1612, 2d), (805, 370, 1d), (1044, 480, 1.5), (2160, 994, 2d),
        })
        {
            var radius = (int)radiusMethod.Invoke(profile, [Math.Min(width, height), dpi])!;
            var region = (nint)createRegion.Invoke(host, [width, height, radius, exponent])!;
            InteractionAssert(region != 0, "Cannot create the production device corner region.");
            try
            {
                var band = new int[4];
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    // The shader evaluates the normalized superellipse at the
                    // pixel center. A complete black band is beyond its 1px
                    // antialias fringe, yet still inside the opaque child HWND.
                    var distance = DeviceCornerShaderDistance(x + .5, y + .5,
                        width, height, radiusRatio, exponent);
                    if (distance > 1.5 && CornerPtInRegion(region, x, y))
                        band[(y >= height / 2 ? 2 : 0) + (x >= width / 2 ? 1 : 0)]++;
                }
                maximumBlackBandPixels = Math.Max(maximumBlackBandPixels, band.Sum());
                results.Add(new { width, height, dpi, regionRadius = radius,
                    shaderRadius = Math.Min(width, height) * radiusRatio,
                    blackBandPixels = band, totalBlackBandPixels = band.Sum() });
                if (width == 370 && height == 805 && dpi == 1)
                {
                    WriteDeviceCornerComposite(host, createRegion,
                        Path.Combine(output, "iphone17-current.png"), width, height,
                        radius, radiusRatio, exponent);
                    WriteDeviceCornerComposite(host, createRegion,
                        Path.Combine(output, "iphone17-previous-gdi-fit.png"), width, height,
                        (int)Math.Round(width * .1583), radiusRatio, exponent);
                }
            }
            finally { CornerDeleteObject(region); }
        }
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            scope = "Actual production HWND region compared with the renderer superellipse; composite PNGs are mathematical coverage diagnostics, not phone screenshots.",
            radiusRatio, exponent, maximumBlackBandPixels, cases = results,
            recentProfiles = AuditRecentCornerProfiles(assembly, host, createRegion),
        }, new JsonSerializerOptions { WriteIndented = true }));
        InteractionAssert(maximumBlackBandPixels == 0,
            $"The iPhone 17 child region exposes {maximumBlackBandPixels} fully black pixels outside its shader outline.");
        Console.WriteLine("PASS iPhone 17 corner clipping: production HWND regions align with the shader outline in all four corners, portrait/landscape sizes and DPI scales.");
        return 0;
    }

    private static List<object> AuditRecentCornerProfiles(Assembly assembly,
        HwndHost host, MethodInfo createRegion)
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        object Read(object item, string name) => item.GetType().GetProperty(name, members)!.GetValue(item)!;
        var resolver = assembly.GetType("IPhoneMirror.App.Services.DeviceCornerProfileResolver")!;
        var frames = assembly.GetType("IPhoneMirror.App.Services.DeviceFrameCatalog")!;
        var catalog = assembly.GetType("IPhoneMirror.App.Services.AppleDisplayCatalog")!;
        var rows = new List<object>();
        var expected = new Dictionary<string, string>
        {
            ["iPhone17,1"] = "iphone-16-pro", ["iPhone17,2"] = "iphone-16-pro-max",
            ["iPhone17,3"] = "iphone-16", ["iPhone17,4"] = "iphone-16-plus",
            ["iPhone17,5"] = "iphone-modern-notch", ["iPhone18,1"] = "iphone-17-pro",
            ["iPhone18,2"] = "iphone-17-pro-max", ["iPhone18,3"] = "iphone-17",
            ["iPhone18,4"] = "iphone-air", ["iPhone18,5"] = "iphone-modern-notch",
            ["iPhone19,2"] = "iphone-18-pro", ["iPhone19,3"] = "iphone-18-pro-max",
            ["iPhone19,7"] = "iphone-18-pro-max",
        };
        foreach (var product in (IEnumerable)catalog.GetProperty("Products", statics)!.GetValue(null)!)
        {
            if ((string)Read(product, "Family") != "iPhone") continue;
            var ids = ((IEnumerable<string>)Read(product, "Identifiers")).ToArray();
            if (!ids.Any(id => id.StartsWith("iPhone17,") || id.StartsWith("iPhone18,") || id.StartsWith("iPhone19,"))) continue;
            var panel = frames.GetMethod("Find", statics)!.Invoke(null,
                [ids[0], Read(product, "Width"), Read(product, "Height")])!;
            foreach (var frame in new[] { panel }.Concat((Read(panel, "DisplayModes") as IEnumerable)?.Cast<object>() ?? []))
            {
                var width = (int)Read(frame, "ScreenWidth");
                var height = (int)Read(frame, "ScreenHeight");
                var frameId = (string)Read(frame, "Id");
                var profile = resolver.GetMethod("Resolve", statics)!.Invoke(null, [ids[0], (uint)width, (uint)height])!;
                var id = (string)Read(profile, "Id");
                var ratio = (double)Read(profile, "RadiusRatio");
                var exponent = (double)Read(profile, "CurveExponent");
                var estimated = id == "iphone-modern-notch";
                var unsupported = frameId == "iphone-duo";
                foreach (var productType in ids)
                {
                    var actual = resolver.GetMethod("Resolve", statics)!.Invoke(null, [productType, (uint)width, (uint)height])!;
                    InteractionAssert((string)Read(actual, "Id") == (unsupported ? "rectangular" :
                        frameId == "iphone-duo-inner" ? "iphone-duo-inner" : expected[productType]),
                        productType + " has an incorrect recent-device corner mapping.");
                }
                var maximumArtworkError = 0d;
                if (!estimated && !unsupported)
                {
                    var mask = (byte[])Read(frame, "Mask");
                    var sx = (int)Read(frame, "ScreenX");
                    var sy = (int)Read(frame, "ScreenY");
                    var fw = (int)Read(frame, "FrameWidth");
                    for (var corner = 0; corner < 4; corner++)
                    for (var offset = 1; offset < Math.Min(width, height) * .23; offset++)
                    {
                        var y = sy + ((corner & 2) == 0 ? offset : height - 1 - offset);
                        for (var x = 0; x < width / 3; x++)
                        {
                            var sampleX = sx + ((corner & 1) == 0 ? x : width - 1 - x);
                            if (mask[y * fw + sampleX] < 128) continue;
                            maximumArtworkError = Math.Max(maximumArtworkError,
                                Math.Abs(DeviceCornerShaderDistance(x + .5, offset + .5,
                                    width, height, ratio, exponent)));
                            break;
                        }
                    }
                    InteractionAssert(maximumArtworkError < 1.5,
                        frameId + $" exceeds its source-mask fit tolerance: {maximumArtworkError:F3}px.");
                }
                var maximumBand = 0;
                if (!unsupported)
                foreach (var shortEdge in new[] { 240, 370, 600 })
                foreach (var landscape in new[] { false, true })
                {
                    var longEdge = (int)Math.Round(shortEdge * height / (double)width);
                    var w = landscape ? longEdge : shortEdge;
                    var h = landscape ? shortEdge : longEdge;
                    var radius = (int)profile.GetType().GetMethod("GetGdiRadius", members)!.Invoke(profile, [shortEdge, 1.5])!;
                    var region = (nint)createRegion.Invoke(host, [w, h, radius, exponent])!;
                    try
                    {
                        var band = 0;
                        var extent = (int)Math.Ceiling(shortEdge * ratio) + 2;
                        for (var corner = 0; corner < 4; corner++)
                        for (var y = 0; y < extent; y++)
                        for (var x = 0; x < extent; x++)
                        {
                            var px = (corner & 1) == 0 ? x : w - 1 - x;
                            var py = (corner & 2) == 0 ? y : h - 1 - y;
                            if (DeviceCornerShaderDistance(px + .5, py + .5, w, h, ratio, exponent) > 1.5 &&
                                CornerPtInRegion(region, px, py)) band++;
                        }
                        maximumBand = Math.Max(maximumBand, band);
                    }
                    finally { CornerDeleteObject(region); }
                }
                InteractionAssert(maximumBand == 0, frameId + " exposes black child-window corner bands.");
                rows.Add(new { name = Read(product, "Name"), productTypes = ids, frameId,
                    panelWidth = width, panelHeight = height, profileId = id, ratio, exponent,
                    classification = unsupported ? "unsupported-asymmetric-outer" : estimated ? "estimated-modern-notch" : "verified-source-artwork-fit",
                    maximumArtworkError, maximumBlackBandPixels = maximumBand });
            }
        }
        InteractionAssert(rows.Count >= 14, "Recent-device audit omitted catalog panels.");
        return rows;
    }

    private static double DeviceCornerShaderDistance(double x, double y,
        int width, int height, double ratio, double exponent)
    {
        var radius = Math.Min(width, height) * ratio;
        var u = Math.Max(Math.Abs(x - width * .5) - (width * .5 - radius), 0) / radius;
        var v = Math.Max(Math.Abs(y - height * .5) - (height * .5 - radius), 0) / radius;
        return (Math.Pow(Math.Pow(u, exponent) + Math.Pow(v, exponent), 1 / exponent) - 1) * radius;
    }

    private static void WriteDeviceCornerComposite(HwndHost host, MethodInfo createRegion,
        string path, int width, int height, int regionRadius, double ratio, double exponent)
    {
        var region = (nint)createRegion.Invoke(host, [width, height, regionRadius, exponent])!;
        try
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var color = (byte)39;
                if (CornerPtInRegion(region, x, y))
                {
                    var distance = DeviceCornerShaderDistance(x + .5, y + .5,
                        width, height, ratio, exponent);
                    var coverage = Math.Clamp((.75 - distance) / 1.5, 0, 1);
                    coverage = coverage * coverage * (3 - 2 * coverage);
                    color = (byte)Math.Round(244 * coverage);
                }
                var offset = (y * width + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = color;
                pixels[offset + 3] = 255;
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
                null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally { CornerDeleteObject(region); }
    }

    [DllImport("gdi32.dll", EntryPoint = "PtInRegion")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CornerPtInRegion(nint region, int x, int y);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CornerDeleteObject(nint value);
}
