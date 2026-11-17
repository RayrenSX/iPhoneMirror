using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDonationIntegrityTests(string output)
    {
        Directory.CreateDirectory(output);
        var rejected = new List<string>();
        var binaryTamperChecks = new List<Action>();
        foreach (var method in Enum.GetValues<DonationPaymentMethod>())
        {
            var resource = method == DonationPaymentMethod.WeChat ? "wechat.png" : "alipay.png";
            using var embedded = typeof(App).Assembly.GetManifestResourceStream("IPhoneMirror.App.Donation." + resource)!;
            using var copy = new MemoryStream();
            embedded.CopyTo(copy);
            var approved = copy.ToArray();
            using (var input = new MemoryStream(approved))
            {
                var loaded = DonationAssets.LoadVerified(method, input);
                InteractionAssert(loaded.IsFrozen && loaded.PixelWidth == 640 && loaded.PixelHeight == 720,
                    "The verified donation bitmap must be loaded and immutable.");
            }
            Reject("empty", []);
            Reject("truncated", approved[..^1]);
            Reject("appended", [.. approved, 0]);
            var damaged = approved.ToArray();
            damaged[damaged.Length / 2] ^= 0x01;
            Reject("single-byte-tamper", damaged);
            Reject("different-valid-png", MakeAlternateDonationImage(approved));
            var other = method == DonationPaymentMethod.WeChat ? "alipay.png" : "wechat.png";
            using var otherEmbedded = typeof(App).Assembly.GetManifestResourceStream("IPhoneMirror.App.Donation." + other)!;
            using var otherCopy = new MemoryStream();
            otherEmbedded.CopyTo(otherCopy);
            Reject("swapped-payment-method", otherCopy.ToArray());
            binaryTamperChecks.Add(RejectPatchedResource);

            void Reject(string scenario, byte[] bytes)
            {
                var failed = false;
                try
                {
                    using var input = new MemoryStream(bytes);
                    DonationAssets.LoadVerified(method, input);
                }
                catch (Exception error) when (error is IOException or InvalidDataException) { failed = true; }
                InteractionAssert(failed, $"{method}: accepted {scenario} donation image.");
                rejected.Add(method + "/" + scenario);
            }

            void RejectPatchedResource()
            {
                var binary = File.ReadAllBytes(typeof(App).Assembly.Location);
                var resourceOffset = binary.AsSpan().IndexOf(approved);
                InteractionAssert(resourceOffset >= 0, "Embedded PNG was not found in the compiled assembly.");
                binary[resourceOffset + approved.Length / 2] ^= 0x01;
                var context = new AssemblyLoadContext("Donation-tamper-" + method, isCollectible: true);
                var failed = false;
                try
                {
                    using var patchedStream = new MemoryStream(binary);
                    var patched = context.LoadFromStream(patchedStream);
                    var service = patched.GetType(typeof(DonationAssets).FullName!, throwOnError: true)!;
                    var methodType = patched.GetType(typeof(DonationPaymentMethod).FullName!, throwOnError: true)!;
                    service.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, [Enum.Parse(methodType, method.ToString())]);
                }
                catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
                { failed = true; }
                finally { context.Unload(); }
                InteractionAssert(failed, $"{method}: the patched embedded resource was accepted.");
                rejected.Add(method + "/patched-assembly-resource");
            }
        }

        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var about = new AboutWindow(app, (MainViewModel)owner.DataContext) { Owner = owner };
        try
        {
            about.Show();
            ((Button)about.FindName("SupportAuthorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            about.UpdateLayout();
            InteractionAssert(((TabControl)about.FindName("AboutTabs")).SelectedItem == about.FindName("DonationTab"),
                "The About support entry did not open the donation page.");
            var wechat = (Image)about.FindName("WeChatDonationQrCode");
            var alipay = (Image)about.FindName("AlipayDonationQrCode");
            InteractionAssert(wechat.Source is { IsFrozen: true } && alipay.Source is { IsFrozen: true } &&
                wechat.RenderSize == alipay.RenderSize && wechat.ActualWidth == 240 && wechat.ActualHeight == 270,
                "The two verified codes must have matching rendered dimensions.");
            InteractionAssert(((FrameworkElement)about.FindName("DonationIntegrityError")).Visibility == Visibility.Collapsed,
                "Approved embedded donation images triggered the failure state.");
            var first = wechat.TransformToAncestor(about).TransformBounds(new Rect(wechat.RenderSize));
            var second = alipay.TransformToAncestor(about).TransformBounds(new Rect(alipay.RenderSize));
            InteractionAssert(Math.Abs(first.Top - second.Top) <= 1 && first.Right < second.Left,
                "The wide donation layout must align both codes beside one another.");
            SaveWindowRender(about, Path.Combine(output, "verified-dual-payment.png"));
            about.Width = 722;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            about.UpdateLayout();
            InteractionAssert(Grid.GetRow((UIElement)about.FindName("AlipayDonationCard")) == 2 &&
                wechat.RenderSize == alipay.RenderSize,
                "A narrow window must stack both codes without changing their relative sizes.");
        }
        finally
        {
            about.Close();
            CloseWorkspaceTestWindow(owner);
            app.Shutdown();
        }
        // WPF caches assemblies by pack URI. Load alternate assemblies only
        // after closing the UI so they cannot alter its resource resolution.
        foreach (var check in binaryTamperChecks) check();
        Console.WriteLine($"PASS donation integrity: {rejected.Count} altered images rejected; frozen embedded images; equal code dimensions; responsive layout.");
        File.WriteAllText(Path.Combine(output, "integrity-results.json"), JsonSerializer.Serialize(new
        {
            rejected, verifiedImageSize = new[] { 640, 720 }, renderedImageSize = new[] { 240, 270 },
            embeddedImagesOnly = true, immutableBitmaps = true, responsiveLayout = true,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static byte[] MakeAlternateDonationImage(byte[] approved)
    {
        using var encoded = new MemoryStream(approved);
        var original = BitmapFrame.Create(encoded, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new WriteableBitmap(new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0));
        bitmap.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 0, 0, 255, 255 }, 4, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
