using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace IPhoneMirror.App.Services;

internal enum DonationPaymentMethod { WeChat, Alipay }

internal static class DonationAssets
{
    internal const string OfficialProjectUrl = "https://github.com/RayrenSX/iPhoneMirror";
    internal const string RecipientDisplayName = "修仙之道上的翔（**翔）";

    // Pin the exact approved image bytes in code, separately from the embedded
    // resources. No paths, settings, downloads or WPF resource overrides are used.
    // This rejects resource substitution in an otherwise unmodified binary; it
    // cannot authenticate a third-party rebuild that also patches this verifier.
    private static (string Resource, int Length, string Sha256) Describe(DonationPaymentMethod method) => method switch
    {
        DonationPaymentMethod.WeChat => ("IPhoneMirror.App.Donation.wechat.png", 243733,
            "7f16955cef33d831e5a0e8775b87fc78536c0f162f03c4a0e48ae3a8a0af12c2"),
        DonationPaymentMethod.Alipay => ("IPhoneMirror.App.Donation.alipay.png", 129227,
            "e93f62b75fbfcf51c0694a0db8fd6237729e7647932fc98610117554e553665a"),
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    internal static BitmapSource Load(DonationPaymentMethod method)
    {
        using var stream = typeof(DonationAssets).Assembly.GetManifestResourceStream(Describe(method).Resource)
            ?? throw new InvalidDataException("The embedded donation image is missing.");
        return LoadVerified(method, stream);
    }

    internal static BitmapSource LoadVerified(DonationPaymentMethod method, Stream stream)
    {
        var asset = Describe(method);
        var encoded = new byte[asset.Length];
        stream.ReadExactly(encoded);
        if (stream.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(encoded), Convert.FromHexString(asset.Sha256)))
            throw new InvalidDataException("The donation image failed integrity verification.");

        // Verify first, then decode those same bytes. Freeze a fully loaded image
        // so later file changes, deferred reads and mutable images cannot replace it.
        using var verifiedStream = new MemoryStream(encoded, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = verifiedStream;
        bitmap.EndInit();
        if (bitmap.PixelWidth != 640 || bitmap.PixelHeight != 720)
            throw new InvalidDataException("The donation image dimensions are incorrect.");
        bitmap.Freeze();
        return bitmap;
    }
}
