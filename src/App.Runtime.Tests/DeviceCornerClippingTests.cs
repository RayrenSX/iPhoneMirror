using System.IO;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using IPhoneMirror.App;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDeviceCornerClippingTests(string output)
    {
        Directory.CreateDirectory(output);
        var results = AuditDeviceCornerProfiles(typeof(App).Assembly);
        File.WriteAllText(Path.Combine(output, "results.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {results.Count} device/panel profiles compared against source screen artwork.");
        return 0;
    }

    private static List<object> AuditDeviceCornerProfiles(Assembly assembly)
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
            ["iPhone13,1"] = "iphone-12-mini", ["iPhone13,2"] = "iphone-12-standard",
            ["iPhone13,3"] = "iphone-12-standard", ["iPhone13,4"] = "iphone-12-max",
            ["iPhone14,4"] = "iphone-13-mini", ["iPhone14,5"] = "iphone-13",
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
            if (!ids.Any(id => expected.ContainsKey(id) || id == "iPhone19,4")) continue;
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
                    var tolerance = frameId == "iphone-13-mini" ? 1.7 : 1.5;
                    InteractionAssert(maximumArtworkError < tolerance,
                        frameId + $" exceeds its source-mask fit tolerance: {maximumArtworkError:F3}px.");
                }
                rows.Add(new { name = Read(product, "Name"), productTypes = ids, frameId,
                    panelWidth = width, panelHeight = height, profileId = id, ratio, exponent,
                    classification = unsupported ? "unsupported-asymmetric-outer" : estimated ? "estimated-modern-notch" : "verified-source-artwork-fit",
                    maximumArtworkError });
            }
        }
        InteractionAssert(rows.Count >= 20, "The corner audit omitted catalog panels.");
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
}
