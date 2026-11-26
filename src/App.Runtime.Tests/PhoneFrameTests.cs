using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestHiddenSensorInteractions(DeviceFrameProfile frame)
    {
        var hitTest = new DeviceFrameHitTest(frame);
        var mask = frame.Mask;
        foreach (var turns in new[] { 1, 3, 5, 7 })
        {
            var image = (BitmapSource)((ImageBrush)PhoneFrameGeometry.CreateScreenMask(frame, turns)).ImageSource;
            foreach (var (x, y, sensor) in new[] { (610, 85, true), (200, 59, false), (180, 74, false) })
            {
                var maskAlpha = mask[y * frame.FrameWidth + x];
                var expected = sensor && turns >= 4 ? 255 : maskAlpha;
                var screenX = x - frame.ScreenX;
                var screenY = y - frame.ScreenY;
                var clockwise = turns % 4 == 1;
                var pixel = clockwise ? new Int32Rect(frame.ScreenHeight - 1 - screenY, screenX, 1, 1) :
                    new Int32Rect(screenY, frame.ScreenWidth - 1 - screenX, 1, 1);
                var alpha = new byte[4];
                image.CopyPixels(pixel, alpha, 4, 0);
                InteractionAssert(alpha[3] == expected, $"Landscape OCR mask coverage differs at {x},{y} (turns {turns}).");

                var previewX = clockwise ? frame.FrameHeight - 1 - y : y;
                var previewY = clockwise ? x : frame.FrameWidth - 1 - x;
                var input = new PreviewPointerEventArgs(PreviewPointerKind.TouchDown,
                    previewX * 2 + 1, previewY * 2 + 1, 1, 0, frame.FrameHeight * 2, frame.FrameWidth * 2,
                    (uint)frame.ScreenHeight, (uint)frame.ScreenWidth, 0, 1, 0);
                InteractionAssert(hitTest.Contains(turns, input) == (expected >= 128),
                    $"Landscape hit testing disagrees with the visible sensor at {x},{y} (turns {turns}).");
            }
        }
    }

    private static int RunPhoneFrameTests(string output)
    {
        Directory.CreateDirectory(output);
        InteractionAssert(AppleDisplayCatalog.Products.SelectMany(p => p.Identifiers).All(id => DeviceFrameCatalog.Find(id) is not null),
            "Every currently cataloged iPhone/iPad identifier must have explicit frame artwork.");
        InteractionAssert(DeviceFrameCatalog.Find("iPhone3,1")?.Id == "iphone-4" &&
            DeviceFrameCatalog.Find("iPhone3,3")?.Id == "iphone-4-cdma",
            "GSM and CDMA iPhone 4 antenna/button layouts must remain distinct.");
        foreach (var product in AppleDisplayCatalog.Products)
        foreach (var identifier in product.Identifiers)
        {
            var device = IPhoneMirror.App.Models.DeviceViewModel.FromNative(
                new IPhoneMirror.App.Interop.NativeDeviceInfo { Udid = "model-name-fixture", ProductType = identifier }, DeviceAppearance.Unknown);
            InteractionAssert(device.ModelDisplay == product.Name.Replace("Xʀ", "XR", StringComparison.Ordinal),
                "A read ProductType must display the corresponding iPhone/iPad name.");
        }
        InteractionAssert(AppleProductNames.Resolve("iPhone19,2") == "iPhone 18 Pro" &&
            AppleProductNames.Resolve("iPhone19,3") == "iPhone 18 Pro Max" &&
            AppleProductNames.Resolve("iPhone19,7") == "iPhone 18 Pro Max",
            "iPhone 18 Pro and regional Pro Max identifiers must be recognized.");
        foreach (var identifier in new[] { "iPhone19,2", "iPhone19,3", "iPhone19,7" })
        {
            InteractionAssert(DeviceFrameCatalog.Find(identifier)?.Colors?.Length == 4,
                "iPhone 18 models must expose all four original finish assets.");
            foreach (var (code, finish) in new[] { ("1", "black"), ("2", "silver"), ("6", "burgundy"), ("9", "glacier") })
            {
                var selected = DeviceFrameCatalog.Resolve(identifier, new("1", code, code));
                InteractionAssert(selected.Detected && selected.ColorId == finish && !selected.Manual,
                    "iPhone 18 enclosure tokens must select their model's original finish.");
            }
        }
        InteractionAssert(DeviceFrameCatalog.Find("iPad13,16")!.Colors!.All(color =>
            new[] { "space-gray", "pink", "purple", "blue", "starlight" }.Contains(color.Id)),
            "Air 5 must not use mislabeled Air 4 green, silver or rose-gold finishes.");
        foreach (var identifier in new[] { "iPad13,18", "iPad13,19" })
        {
            var frame = DeviceFrameCatalog.Find(identifier)!;
            InteractionAssert(frame.Id == "ipad-10th-generation" && frame.Colors!.Select(c => c.Id).Order()
                .SequenceEqual(new[] { "blue", "pink", "silver", "yellow" }),
                "Both iPad 10 variants must offer their four reviewed front finishes.");
            InteractionAssert(!DeviceFrameCatalog.Resolve(identifier, new("1", "2", "2")).Detected,
                "Shared artwork must not inherit another ProductType's numeric housing codes.");
        }
        InteractionAssert(DeviceFrameCatalog.Profiles.SelectMany(f => f.ProductTypes).Distinct().Count() ==
            DeviceFrameCatalog.Profiles.Sum(f => f.ProductTypes.Length), "A ProductType is assigned to multiple frame profiles.");
        InteractionAssert(DeviceFrameCatalog.AllProfiles.SelectMany(f => f.Colors ?? []).Select(c => c.AssetId).Distinct().Count() ==
            DeviceFrameCatalog.AllProfiles.Sum(f => f.Colors?.Length ?? 0), "Frame assets collide across different models.");
        foreach (var frame in DeviceFrameCatalog.AllProfiles)
        {
            foreach (var identifier in frame.ProductTypes)
                InteractionAssert(DeviceFrameCatalog.Find(identifier, (uint)frame.ScreenWidth, (uint)frame.ScreenHeight) == frame, "A cellular/WiFi ProductType or panel is missing its frame.");
            foreach (var color in frame.Colors ?? [])
            {
                var manualChoice = DeviceFrameCatalog.Resolve(frame.ProductTypes[0], DeviceAppearance.Unknown with { SelectedColorId = color.Id },
                    (uint)frame.ScreenWidth, (uint)frame.ScreenHeight);
                var colorFrame = manualChoice.Profile!;
                InteractionAssert(colorFrame.FrameWidth > 0 && colorFrame.FrameHeight > 0 && colorFrame.ScreenX >= 0 &&
                    colorFrame.ScreenY >= 0 && colorFrame.ScreenX + colorFrame.ScreenWidth <= colorFrame.FrameWidth &&
                    colorFrame.ScreenY + colorFrame.ScreenHeight <= colorFrame.FrameHeight, "Invalid per-finish screen geometry.");
                foreach (var token in color.Tokens)
                {
                    var choice = DeviceFrameCatalog.Resolve(frame.ProductTypes[0], new("1", token, token),
                        (uint)frame.ScreenWidth, (uint)frame.ScreenHeight);
                    InteractionAssert(choice.Detected && choice.ColorId == color.Id && choice.Profile?.Id == color.AssetId,
                        "Enclosure color did not choose the matching model asset.");
                    InteractionAssert(choice.Profile!.Pixels.Length == choice.Profile.FrameWidth * choice.Profile.FrameHeight * 4 &&
                        choice.Profile.Mask.Length == choice.Profile.FrameWidth * choice.Profile.FrameHeight,
                        "Color-specific artwork or cutout mask is missing.");
                }
            }
            InteractionAssert(DeviceFrameCatalog.Find(frame.ProductTypes[0], (uint)frame.ScreenWidth, (uint)frame.ScreenHeight) == frame, "Incorrect model/panel matching.");
            InteractionAssert(frame.Pixels.Length == frame.FrameWidth * frame.FrameHeight * 4 &&
                frame.Mask.Length == frame.FrameWidth * frame.FrameHeight, "Incorrect artwork dimensions.");
            foreach (var rotation in Enumerable.Range(0, 4))
            foreach (var kind in new[] { PreviewPointerKind.Move, PreviewPointerKind.TouchMove })
            foreach (var fraction in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
            {
                var width = (rotation & 1) == 0 ? 600 : 1200;
                var height = (rotation & 1) == 0 ? 1200 : 600;
                var bounds = PhoneFrameGeometry.ScreenBounds(frame, rotation, width, height);
                var x = (int)Math.Round(bounds.X + bounds.Width * fraction);
                var y = (int)Math.Round(bounds.Y + bounds.Height * fraction);
                var input = new PreviewPointerEventArgs(kind, x, y, 1, 120, width, height,
                    (uint)width, (uint)height, rotation, 7, (nint)42) { WindowsButtons = 1 };
                var mapped = PhoneFrameGeometry.MapPointer(frame, rotation, input);
                InteractionAssert(Math.Abs(mapped.X - width * fraction) <= 2 &&
                    Math.Abs(mapped.Y - height * fraction) <= 2, "Model frame input is offset.");
                InteractionAssert(mapped.Rotation == rotation && mapped.TouchId == 7 &&
                    mapped.SourceWindow == 42 && mapped.WindowsButtons == 1 && mapped.Wheel == 120,
                    "Model frame mapping lost input metadata.");
            }
            // Notched/island devices reject the sensor; classic iPhones and iPads
            // keep the corresponding area usable. The original mask is authoritative.
            var cutout = new PreviewPointerEventArgs(PreviewPointerKind.TouchDown,
                frame.FrameWidth / 2, frame.ScreenY + 60, 1, 0,
                frame.FrameWidth, frame.FrameHeight, (uint)frame.ScreenWidth, (uint)frame.ScreenHeight, 0, 1, 0);
            var expectedCutout = frame.Mask[(frame.ScreenY + 60) * frame.FrameWidth + frame.FrameWidth / 2] >= 128;
            InteractionAssert(PhoneFrameGeometry.ContainsScreenPoint(frame, 0, cutout) == expectedCutout,
                "Cutout hit testing disagrees with the model mask.");
            var middle = new PreviewPointerEventArgs(PreviewPointerKind.TouchDown, frame.FrameWidth / 2, frame.FrameHeight / 2,
                1, 0, frame.FrameWidth, frame.FrameHeight, (uint)frame.ScreenWidth, (uint)frame.ScreenHeight, 0, 1, 0);
            InteractionAssert(PhoneFrameGeometry.ContainsScreenPoint(frame, 0, middle), "Model center is not an interactive screen.");
            foreach (var rotation in new[] { 0, 1, 4, 5 })
            {
                var landscape = (rotation & 1) != 0;
                var surfaceWidth = landscape ? frame.FrameHeight : frame.FrameWidth;
                var surfaceHeight = landscape ? frame.FrameWidth : frame.FrameHeight;
                var screen = PhoneFrameGeometry.ScreenBounds(frame, rotation, surfaceWidth, surfaceHeight);
                var inside = new PreviewPointerEventArgs(PreviewPointerKind.Wheel,
                    (int)Math.Ceiling(screen.Right) - 1, (int)screen.Top + (int)screen.Height / 2, 0, 120,
                    surfaceWidth, surfaceHeight, 160, 160, rotation, 0, 0);
                var mapped = PhoneFrameGeometry.MapPointer(frame, rotation, inside);
                InteractionAssert(mapped.X == 159 && mapped.Y >= 0 && mapped.Y < 160,
                    "The last physical pixel inside the frame must map to a usable source pixel.");
                var outside = new PreviewPointerEventArgs(PreviewPointerKind.Wheel, inside.X + 1, inside.Y, 0, 120,
                    surfaceWidth, surfaceHeight, 160, 160, rotation, 0, 0);
                InteractionAssert(!PhoneFrameGeometry.ContainsScreenPoint(frame, rotation, outside),
                    "The first pixel on the right frame boundary must stay outside the screen.");
            }
            GC.Collect();
        }
        InteractionAssert(!DeviceFrameCatalog.Resolve("iPhone13,1", new("1", "", "")).Detected,
            "Black front glass must not be reported as a black enclosure.");
        InteractionAssert(!DeviceFrameCatalog.Resolve("iPhone13,1", new("1", "9", "6")).Detected,
            "Conflicting known enclosure colors must not be guessed.");
        InteractionAssert(!DeviceFrameCatalog.Resolve("iPhone18,3", new("1", "999", "")).Detected,
            "Unknown product color codes must retain the explicit fallback state.");
        var observedWhite17 = DeviceFrameCatalog.Resolve("iPhone18,3", new("1", "2", "2"));
        InteractionAssert(observedWhite17.Detected && !observedWhite17.Manual &&
            observedWhite17.Profile?.Id == "iphone-17-white",
            "Owner-confirmed white iPhone 17 housing code 2 must use the white artwork automatically.");
        var iphone17Black = DeviceFrameCatalog.Resolve("iPhone18,3", new("1", "1", "1")).Profile!;
        var housingSample = (1380 * iphone17Black.FrameWidth + 41) * 4;
        InteractionAssert(iphone17Black.Pixels[housingSample + 2] is > 65 and < 125 &&
            iphone17Black.Pixels[housingSample + 3] == 255 &&
            observedWhite17.Profile!.Pixels[housingSample + 2] > 200,
            "Only the black finish should use the darker housing with unchanged opacity.");
        var blackPixels = iphone17Black.Pixels;
        foreach (var (x, y) in new[] { (140, 75), (1274, 141), (77, 2621), (1272, 2621) })
        {
            var corner = (y * iphone17Black.FrameWidth + x) * 4;
            InteractionAssert(blackPixels[corner + 2] is > 70 and < 110 && blackPixels[corner + 3] == 255,
                "The black frame still has an undimmed corner highlight inside the screen rectangle.");
        }
        InteractionAssert(DeviceFrameCatalog.Resolve("iPhone13,1", new("black", " BLUE ", "")).ColorId == "blue",
            "Named device colors should be normalized.");
        var appearancePath = Path.Combine(Path.GetFullPath(output), "appearance-cache-test.json");
        if (File.Exists(appearancePath)) File.Delete(appearancePath);
        var appearanceCache = new DeviceAppearanceCache(appearancePath);
        appearanceCache.Remember("usb-fixture", "iPhone13,1", new("1", "2", ""));
        var restartedCache = new DeviceAppearanceCache(appearancePath);
        var remembered = restartedCache.Remember("USB-FIXTURE", "iPhone13,1", DeviceAppearance.Unknown);
        InteractionAssert(DeviceFrameCatalog.Resolve("iPhone13,1", remembered).ColorId == "white",
            "Pre-capture housing color must survive process restart and absent USB metadata.");
        InteractionAssert(restartedCache.Remember("other-device", "iPhone13,1", DeviceAppearance.Unknown) == DeviceAppearance.Unknown,
            "Appearance cache leaked one phone's color to another device.");
        InteractionAssert(restartedCache.Remember("usb-fixture", "iPhone18,3", DeviceAppearance.Unknown) == DeviceAppearance.Unknown,
            "Appearance cache reused a different model's numeric color codes.");
        TestBoundAirPlayFrameColors(output, restartedCache);
        var partial = restartedCache.Remember("usb-fixture", "iPhone13,1", new("black", "", ""));
        InteractionAssert(partial.EnclosureColor == "2", "Partial metadata erased the housing color.");
        var newColor = restartedCache.Remember("usb-fixture", "iPhone13,1", new("1", "9", ""));
        InteractionAssert(newColor.EnclosureColor == "9" && newColor.HousingColor == "",
            "A fresh color must not be combined with a stale second housing field.");
        var nullCachePath = Path.Combine(Path.GetFullPath(output), "null-appearance.json");
        File.WriteAllText(nullCachePath, "{\"fixture\":{\"ProductType\":\"iPhone13,1\",\"Appearance\":null}}");
        InteractionAssert(new DeviceAppearanceCache(nullCachePath).Remember("fixture", "iPhone13,1", DeviceAppearance.Unknown) == DeviceAppearance.Unknown,
            "Malformed cached appearance should be ignored.");
        var manualWhite = DeviceFrameCatalog.Resolve("iPhone13,1", new("1", "", "", "white"));
        InteractionAssert(manualWhite.Manual && !manualWhite.Detected && manualWhite.Profile?.Id == "iphone-12-mini-white",
            "Explicit device color must select actual white artwork without claiming automatic detection.");
        TestHiddenSensorInteractions(manualWhite.Profile!);
        InteractionAssert(!DeviceFrameCatalog.Resolve("iPhone13,1", new("1", "", "", "invalid")).Manual,
            "Invalid persisted colors must not bypass model validation.");
        var colorSettings = new IPhoneMirror.App.Updater.UpdateSettings();
        colorSettings.DeviceFrameColors["fixture-a"] = "white";
        var colorClone = colorSettings.Clone();
        colorClone.DeviceFrameColors["fixture-a"] = "red";
        InteractionAssert(colorSettings.DeviceFrameColors["fixture-a"] == "white", "Color preferences clone shares mutable state.");
        var colorStore = new IPhoneMirror.App.Updater.UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "frame-color-settings.json"));
        colorStore.Save(colorSettings);
        InteractionAssert(colorStore.Load().DeviceFrameColors["fixture-a"] == "white", "Device color did not survive save/load.");
        TestGlobalPhoneFramePersistence(output);
        var blueDevice = IPhoneMirror.App.Models.DeviceViewModel.FromNative(
            new IPhoneMirror.App.Interop.NativeDeviceInfo { Udid = "color-fixture-a", ProductType = "iPhone13,1" }, new("1", "9", "9"));
        InteractionAssert(blueDevice.AsUsbPresentNoMux().Appearance == blueDevice.Appearance,
            "USB transition dropped cached appearance.");
        var redDevice = IPhoneMirror.App.Models.DeviceViewModel.FromNative(
            new IPhoneMirror.App.Interop.NativeDeviceInfo { Udid = "color-fixture-a", ProductType = "iPhone13,1" }, new("1", "6", "6"));
        blueDevice.UpdateFrom(redDevice);
        InteractionAssert(blueDevice.Appearance.EnclosureColor == "6", "Metadata refresh failed to update appearance.");
        InteractionAssert(DeviceFrameCatalog.Find("iPhone99,99") is null,
            "Unknown devices must not inherit another model's artwork.");
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
        app.InitializeComponent();
        typeof(IPhoneMirror.App.Localization.LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["zh-CN", false, false]);
        TestGlobalPhoneFrameWindows(app, output);
        foreach (var frame in DeviceFrameCatalog.Profiles.Where(f => f.ArtworkKind == "self-drawn"))
        foreach (var color in frame.Colors!)
        {
            var selection = DeviceFrameCatalog.Resolve(frame.ProductTypes[0], DeviceAppearance.Unknown with { SelectedColorId = color.Id });
            InteractionAssert(selection.Profile?.ArtworkKind == "self-drawn" && DeviceFrameCatalog.Describe(selection).Contains("自绘边框"),
                "A finish change must preserve the custom-drawn frame disclosure.");
            InteractionAssert(!DeviceFrameCatalog.Resolve(frame.ProductTypes[0], new("1", "999", "999")).Detected,
                "Drawing additional finishes must not invent unverified numeric color mappings.");
        }
        foreach (var id in new[] { "iPhone17,5", "iPhone18,5" })
        {
            var frame = DeviceFrameCatalog.Find(id)!;
            InteractionAssert(frame.ArtworkKind == "self-drawn" && frame.Mask[(frame.ScreenY+60)*frame.FrameWidth+frame.FrameWidth/2] == 0,
                "16e and 17e require a top-connected notch, not an island or plain rectangular screen.");
        }
        foreach (var product in AppleDisplayCatalog.Products)
        foreach (var identifier in product.Identifiers)
        {
            var profile = DeviceFrameCatalog.Find(identifier);
            if (profile is null) continue;
            var color = profile.Colors!.First();
            foreach (var appearance in new[] { DeviceAppearance.Unknown,
                new DeviceAppearance("", color.Tokens[0], ""),
                DeviceAppearance.Unknown with { SelectedColorId = color.Id } })
            {
                var selection = DeviceFrameCatalog.Resolve(identifier, appearance);
                InteractionAssert(DeviceFrameCatalog.Describe(selection).Contains(AppleProductNames.Resolve(identifier), StringComparison.Ordinal),
                    "Shared artwork must not replace the device's actual model in any color description.");
            }
        }
        VerifyActiveFrameMaskRetention();
        foreach (var (width, height, asset) in new[] {
            (1398u, 2034u, "iphone-duo"), (2034u, 1398u, "iphone-duo"),
            (1878u, 2670u, "iphone-duo-inner"), (2670u, 1878u, "iphone-duo-inner"),
            (939u, 1335u, "iphone-duo-inner") })
        {
            var selected = DeviceFrameCatalog.Resolve("iPhone19,4", DeviceAppearance.Unknown, width, height);
            InteractionAssert(selected.Profile?.Id == asset, "Duo must switch its own inner/outer artwork with the captured panel, including downscaled landscape video.");
            var white = DeviceFrameCatalog.Resolve("iPhone19,4", DeviceAppearance.Unknown with { SelectedColorId = "star-white" }, width, height);
            InteractionAssert(white.Profile?.Id == asset + "-star-white", "Duo panel changes must preserve the selected finish.");
        }
        var owner = new Window { Width = 500, Height = 800, ShowInTaskbar = false, Style = null, AllowsTransparency = false };
        owner.Show();
        var handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        foreach (var profile in DeviceFrameCatalog.Profiles.Where(profile => profile.Id is "iphone-12-mini" or "iphone-17" || profile.ProductTypes[0].StartsWith("iPad")).Take(12))
        foreach (var rotation in Enumerable.Range(0, 4))
        {
            var surface = new MappingPreviewSurface(handle, handle, "frame-test", 1,
                1080, 2340, rotation, BluetoothMouseDirection.Up, BluetoothMouseDirection.Up, false, false)
                { Frame = profile, FrameRotation = rotation };
            var overlay = new KeyboardMappingOverlayWindow(surface);
            try
            {
                overlay.Update(surface, []);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
                var point = (Point)KeyboardCall(overlay, "Project", .23, .71)!;
                var normalized = ((double X, double Y)?)KeyboardCall(overlay, "Normalize", point);
                InteractionAssert(normalized is { } value && Math.Abs(value.X - .23) < 1e-6 &&
                    Math.Abs(value.Y - .71) < 1e-6, "Frame marker and picking coordinates disagree.");
            }
            finally { overlay.Close(); }
        }
        owner.Close();
        bool applied = false;
        var window = new WindowStyleSettingsWindow(1, _ => true, false, _ => true,
            false, value => { applied = value; return true; }, "iPhone 12 mini · 已识别机身颜色：蓝色。全屏时自动隐藏边框。");
        window.Show();
        var toggle = (CheckBox)window.FindName("PhoneFrameCheckBox");
        toggle.IsChecked = true;
        InteractionAssert(applied, "Phone frame switch did not apply.");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        SaveWindowRender(window, Path.Combine(output, "phone-frame-settings.png"));
        toggle.IsChecked = false;
        InteractionAssert(!applied, "Phone frame switch did not restore.");
        window.Close();
        var failed = new WindowStyleSettingsWindow(1, _ => true, false, _ => true, false, _ => false);
        ((CheckBox)failed.FindName("PhoneFrameCheckBox")).IsChecked = true;
        InteractionAssert(((CheckBox)failed.FindName("PhoneFrameCheckBox")).IsChecked == false,
            "Failed frame setting must roll back the switch.");
        failed.Close();
        bool projectionEnabled = false;
        bool rejectChange = false;
        var projection = new ProjectionSettingsWindow(new object(),
            () => Task.CompletedTask, () => Task.CompletedTask,
            () => Task.CompletedTask, () => Task.CompletedTask, () => { });
        projection.ConfigurePhoneFrame(() => (projectionEnabled, true, "所有独立投屏窗口统一显示手机边框，并在重启后保留。"),
            value => { if (!rejectChange) projectionEnabled = value; return Task.FromResult(!rejectChange); });
        projection.Show();
        ((RadioButton)projection.FindName("ApplicationSettingsTab")).IsChecked = true;
        projection.UpdateLayout();
        var projectionToggle = (CheckBox)projection.FindName("PhoneFrameCheckBox");
        projectionToggle.IsChecked = true;
        projectionToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        InteractionAssert(projectionEnabled && projectionToggle.IsChecked == true, "Projection switch did not apply.");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        SaveWindowRender(projection, Path.Combine(output, "projection-phone-frame-settings.png"));
        projectionEnabled = false;
        projection.RefreshPhoneFrame();
        InteractionAssert(projectionToggle.IsChecked == false, "Projection switch did not reflect external changes.");
        rejectChange = true;
        projectionToggle.IsChecked = true;
        projectionToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        InteractionAssert(projectionToggle.IsChecked == false, "Projection switch did not roll back failure.");
        projection.Close();
        app.Shutdown();
        Console.WriteLine("PASS phone frame model descriptions, active mask retention, color matching/fallback, appearance retention, coordinates, switch and failure rollback.");
        return 0;
    }

    private static void TestBoundAirPlayFrameColors(string output, DeviceAppearanceCache cache)
    {
        var path = Path.Combine(output, $"frame-bindings-{Guid.NewGuid():N}.json");
        var bindings = new DeviceBindingManager(path);
        const string wiredId = "00008150-aabbcc0000000001";
        const string wirelessId = "airplay://02:34:56:78:9a:bc";
        const string product = "iPhone18,3";
        var fingerprint = new DeviceFingerprint(ProductType: product);
        var created = bindings.CreateProfileFromIdentity("Frame color fixture", DeviceIdentityType.Wired, wiredId, fingerprint);
        InteractionAssert(created.Success && created.Profile is not null &&
            bindings.Bind(created.Profile.Id, DeviceIdentityType.AirPlay, wirelessId, "Frame color fixture",
                fingerprint, userConfirmed: true).Success, "Could not create the bound AirPlay fixture.");

        IPhoneMirror.App.Models.DeviceViewModel Device(string id, string model) =>
            IPhoneMirror.App.Models.DeviceViewModel.FromNative(
                new IPhoneMirror.App.Interop.NativeDeviceInfo { Udid = id, ProductType = model }, DeviceAppearance.Unknown);
        var wireless = Device(wirelessId, product);
        foreach (var (code, finish) in new[] { ("1", "black"), ("2", "white"), ("9", "mist-blue"), ("17", "lavender"), ("18", "sage") })
        {
            cache.Remember(wiredId, product, new("1", code, code));
            var selection = DeviceFrameCatalog.Resolve(product, cache.ForProjection(wireless, bindings));
            InteractionAssert(selection.Detected && selection.ColorId == finish,
                $"Bound AirPlay did not inherit the matching iPhone 17 finish {finish}.");
        }
        InteractionAssert(cache.ForProjection(Device("airplay://02:34:56:78:9a:bd", product), bindings) == DeviceAppearance.Unknown &&
            cache.ForProjection(Device(wirelessId, "iPhone13,1"), bindings) == DeviceAppearance.Unknown,
            "An unbound wireless device or a different product must not inherit the wired finish.");
        InteractionAssert(bindings.Unbind(created.Profile!.Id, DeviceIdentityType.AirPlay) &&
            cache.ForProjection(wireless, bindings) == DeviceAppearance.Unknown,
            "An unbound AirPlay device must stop borrowing the cached finish.");
    }

    private static void TestGlobalPhoneFramePersistence(string output)
    {
        var path = Path.Combine(Path.GetFullPath(output), "global-frame-settings.json");
        var store = new IPhoneMirror.App.Updater.UpdateSettingsStore(path);
        var settings = new IPhoneMirror.App.Updater.UpdateSettings();
        InteractionAssert(!settings.PhoneFrameEnabled, "A new installation must keep phone frames disabled by default.");
        settings.DeviceFrameColors["fixture-a"] = "white";
        foreach (var enabled in new[] { true, false })
        {
            settings.PhoneFrameEnabled = enabled;
            store.Save(settings);
            var reloaded = new IPhoneMirror.App.Updater.UpdateSettingsStore(path).Load();
            InteractionAssert(reloaded.PhoneFrameEnabled == enabled &&
                reloaded.Clone().PhoneFrameEnabled == enabled &&
                reloaded.DeviceFrameColors["fixture-a"] == "white",
                "The global phone frame preference must survive save, restart and clone without changing per-device colors.");
        }

        foreach (var (json, expected) in new[]
        {
            ("{}", false),
            ("{\"DeviceFrameEnabled\":null}", false),
            ("{\"DeviceFrameEnabled\":{\"first\":false,\"second\":true}}", true),
            ("{\"DeviceFrameEnabled\":{\"first\":false,\"second\":false}}", false),
            ("{\"PhoneFrameEnabled\":false,\"DeviceFrameEnabled\":{\"first\":true}}", false),
            ("{\"PhoneFrameEnabled\":true,\"DeviceFrameEnabled\":{\"first\":false}}", true),
            ("{\"phoneframeenabled\":false,\"DeviceFrameEnabled\":{\"first\":true}}", false),
        })
        {
            File.WriteAllText(path, json);
            InteractionAssert(store.Load().PhoneFrameEnabled == expected &&
                new IPhoneMirror.App.Updater.UpdateSettingsStore(path).Load().PhoneFrameEnabled == expected,
                "Legacy migration must preserve an earlier opt-in and must never override an explicit global preference.");
        }
        Console.WriteLine("PASS global phone frame persistence, restart/clone, legacy migration and explicit disable precedence.");
    }

    private static void TestGlobalPhoneFrameWindows(App app, string output)
    {
        var originalStore = KeyboardField(app, "_settingsStore");
        var originalSettings = app.UpdateSettings.Clone();
        var originalPreviewMode = app.IsUiPreviewMode;
        var store = new IPhoneMirror.App.Updater.UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "global-frame-window-settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        app.UpdateSettings.PhoneFrameEnabled = false;
        var vm = new IPhoneMirror.App.ViewModels.MainViewModel();
        using var manager = new MultiDevicePreviewManager(vm);
        var core = (IPhoneMirror.App.Interop.NativeCore)KeyboardField(vm, "_core");
        var sessions = new List<IPhoneMirror.App.Interop.NativeSessionHandle>();
        var windows = (Dictionary<string, NativePreviewWindow>)KeyboardField(manager, "_windows");
        try
        {
            NativePreviewWindow Create(string identity, string product)
            {
                var session = core.CreateExternalSession(identity, false, 0);
                InteractionAssert(session.Success && session.Handle is not null, "Cannot create a synthetic frame preference session: " + session.Message);
                sessions.Add(session.Handle!);
                var image = Enumerable.Repeat((byte)128, 64 * 64 * 3 / 2).ToArray();
                core.PushExternalFrame(session.Handle!, 1, 64, 64, image);
                InteractionAssert(NativePreviewWindow.TryCreateAndShowForSession(session.Handle!.RawHandle,
                    1206, 2622, identity, 0, 3, () => false, () => 3,
                    _ => { }, () => { }, _ => { }, () => { }, out var preview) && preview is not null,
                    "Cannot create a synthetic frame preference preview.");
                InteractionAssert(preview!.UpdateDeviceAppearance(product, DeviceAppearance.Unknown),
                    "Cannot configure a synthetic frame preference model.");
                windows[identity] = preview;
                preview.PhoneFramePreference = () => app.UpdateSettings.PhoneFrameEnabled;
                preview.PhoneFramePreferenceRequested = enabled => manager.SetPhoneFrameAsync(enabled).GetAwaiter().GetResult();
                return preview;
            }

            var first = Create("global-frame-first", "iPhone13,1");
            var second = Create("global-frame-second", "iPhone18,1");
            var unknown = Create("global-frame-unknown", "iPhone99,99");
            KeyboardCall(first, "ShowStyleSettings");
            KeyboardCall(second, "ShowStyleSettings");
            var firstStyle = (WindowStyleSettingsWindow)KeyboardField(first, "_styleWindow");
            var secondStyle = (WindowStyleSettingsWindow)KeyboardField(second, "_styleWindow");
            app.IsUiPreviewMode = false;
            ((CheckBox)firstStyle.FindName("PhoneFrameCheckBox")).IsChecked = true;
            InteractionAssert(first.PhoneFrameEnabled && second.PhoneFrameEnabled && !unknown.PhoneFrameEnabled &&
                ((CheckBox)secondStyle.FindName("PhoneFrameCheckBox")).IsChecked == true && store.Load().PhoneFrameEnabled,
                "A style switch must enable every supported preview, skip unknown models, synchronize other style windows and persist globally.");
            var late = IPhoneMirror.App.Models.DeviceViewModel.FromNative(
                new IPhoneMirror.App.Interop.NativeDeviceInfo { Udid = "global-frame-unknown", ProductType = "iPhone18,3" },
                new("1", "2", "2"));
            vm.Devices.Add(late);
            KeyboardCall(manager, "RefreshOpenFrameAppearances");
            InteractionAssert(unknown.PhoneFrameEnabled && unknown.ActiveDeviceFrame?.Id == "iphone-17-white",
                "A background preview must apply late model and color without changing its video size.");
            late.UpdateFrom(IPhoneMirror.App.Models.DeviceViewModel.FromNative(
                new IPhoneMirror.App.Interop.NativeDeviceInfo { Udid = late.Udid, ProductType = "iPhone99,99" },
                DeviceAppearance.Unknown));
            KeyboardCall(manager, "RefreshOpenFrameAppearances");
            InteractionAssert(!unknown.PhoneFrameEnabled && unknown.ActiveDeviceFrame is null && app.UpdateSettings.PhoneFrameEnabled,
                "An unsupported model must drop stale artwork while retaining the global preference.");
            late.UpdateFrom(IPhoneMirror.App.Models.DeviceViewModel.FromNative(
                new IPhoneMirror.App.Interop.NativeDeviceInfo { Udid = late.Udid, ProductType = "iPhone13,1" },
                DeviceAppearance.Unknown));
            KeyboardCall(manager, "RefreshOpenFrameAppearances");
            InteractionAssert(unknown.PhoneFrameEnabled && unknown.ActiveDeviceFrame?.Id == "iphone-12-mini",
                "A newly supported model must inherit the saved preference.");

            var blockedParent = Path.Combine(Path.GetFullPath(output), "blocked-global-frame-windows");
            File.WriteAllText(blockedParent, "A file cannot hold settings.");
            SetKeyboardField(app, "_settingsStore", new IPhoneMirror.App.Updater.UpdateSettingsStore(Path.Combine(blockedParent, "settings.json")));
            InteractionAssert(!manager.SetPhoneFrameAsync(false).GetAwaiter().GetResult() && first.PhoneFrameEnabled && second.PhoneFrameEnabled && unknown.PhoneFrameEnabled &&
                app.UpdateSettings.PhoneFrameEnabled && ((CheckBox)firstStyle.FindName("PhoneFrameCheckBox")).IsChecked == true &&
                ((CheckBox)secondStyle.FindName("PhoneFrameCheckBox")).IsChecked == true,
                "A failed global save must restore every preview and open style checkbox.");
            SetKeyboardField(app, "_settingsStore", store);
            InteractionAssert(manager.SetPhoneFrameAsync(false).GetAwaiter().GetResult() && !first.PhoneFrameEnabled && !second.PhoneFrameEnabled && !unknown.PhoneFrameEnabled &&
                !store.Load().PhoneFrameEnabled, "Disabling the global preference must update every preview and persist.");

            var frameShortcut = new SettingShortcutBinding { Setting = PreviewShortcutSetting.PhoneFrame, VirtualKey = 0x78, Value = 1 };
            var restoreFirst = first.BeginTemporarySetting(frameShortcut);
            InteractionAssert(restoreFirst is not null && first.PhoneFrameEnabled && !second.PhoneFrameEnabled &&
                !store.Load().PhoneFrameEnabled && ((CheckBox)firstStyle.FindName("PhoneFrameCheckBox")).IsChecked == false,
                "The phone frame shortcut must affect only its preview and leave the global setting untouched.");
            var hideFirst = first.BeginTemporarySetting(frameShortcut with { Value = 0 });
            InteractionAssert(hideFirst is not null && !first.PhoneFrameEnabled, "The most recent shortcut value must win.");
            InteractionAssert(manager.SetPhoneFrameAsync(true).GetAwaiter().GetResult() && !first.PhoneFrameEnabled && second.PhoneFrameEnabled,
                "Changing the global preference must preserve an active per-window override.");
            restoreFirst!();
            InteractionAssert(!first.PhoneFrameEnabled, "Removing an older shortcut must leave the current one applied.");
            hideFirst!();
            InteractionAssert(first.PhoneFrameEnabled, "Releasing the last shortcut must restore the latest global preference.");
            InteractionAssert(manager.SetPhoneFrameAsync(false).GetAwaiter().GetResult(), "Could not restore the preference for the failure fixture.");

            var secondSession = second.SessionHandle;
            SetKeyboardField(second, "_sessionHandle", ulong.MaxValue);
            try
            {
                InteractionAssert(!manager.SetPhoneFrameAsync(true).GetAwaiter().GetResult() && !first.PhoneFrameEnabled && !second.PhoneFrameEnabled && !unknown.PhoneFrameEnabled &&
                    !app.UpdateSettings.PhoneFrameEnabled && !store.Load().PhoneFrameEnabled,
                    "A native apply failure must roll back earlier previews and must not save the rejected global preference.");
            }
            finally { SetKeyboardField(second, "_sessionHandle", secondSession); }
            Console.WriteLine("PASS synthetic native multiwindow frame synchronization, unsupported-model skip, save failure and native failure rollback.");
        }
        finally
        {
            app.IsUiPreviewMode = true;
            manager.Dispose();
            foreach (var session in sessions) session.Dispose();
            WaitReviewTask(vm.ShutdownAsync());
            SetKeyboardField(app, "_settingsStore", originalStore);
            app.RestoreUpdateSettings(originalSettings);
            app.IsUiPreviewMode = originalPreviewMode;
        }
    }

    private static void VerifyActiveFrameMaskRetention()
    {
        var mask = ExerciseActiveFrameHitTest();
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        InteractionAssert(!mask.IsAlive, "An inactive frame must not retain its decoded mask indefinitely.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseActiveFrameHitTest()
    {
        var frame = DeviceFrameCatalog.Find("iPhone13,1")!;
        var hitTest = new DeviceFrameHitTest(frame);
        var mask = new WeakReference(frame.Mask);
        var point = new PreviewPointerEventArgs(PreviewPointerKind.ButtonDown,
            frame.FrameWidth / 2, frame.FrameHeight / 2, 1, 0, frame.FrameWidth, frame.FrameHeight,
            (uint)frame.ScreenWidth, (uint)frame.ScreenHeight, 0, 0, 0);
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        InteractionAssert(mask.IsAlive, "Garbage collection released the enabled preview's hit-test mask.");
        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 1000; ++n)
            InteractionAssert(hitTest.Contains(0, point), "Active frame hit testing lost the screen center.");
        InteractionAssert(GC.GetAllocatedBytesForCurrentThread() - startBytes < 4096,
            "Pointer hit testing decoded or allocated another full-resolution mask.");
        GC.KeepAlive(hitTest);
        return mask;
    }
}
