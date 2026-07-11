using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

internal static class ScreenshotService
{
    private static long _sequence;

    internal static string CreateDefaultPath(string? directory = null)
    {
        directory ??= Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var sequence = Interlocked.Increment(ref _sequence);
        return Path.Combine(directory,
            $"iPhoneMirror-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{sequence:D3}.png");
    }

    internal static string CapturePng(Func<VideoFrame?> frameProvider, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(frameProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var frame = frameProvider() ??
            throw new InvalidOperationException(LocalizationService.Get("ScreenshotNoFrame"));
        var requiredBytes = checked((int)(frame.Stride * frame.Height));
        if (frame.Width == 0 || frame.Height == 0 || frame.Stride < frame.Width * 4U ||
            frame.Pixels.Length < requiredBytes)
        {
            throw new InvalidDataException(LocalizationService.Get("ScreenshotInvalidFrame"));
        }

        // NativeCore reuses its copy buffer. Own this frame before another
        // preview/screenshot request can overwrite it.
        var pixels = frame.Pixels.AsSpan(0, requiredBytes).ToArray();
        var bitmap = BitmapSource.Create(
            checked((int)frame.Width), checked((int)frame.Height),
            96, 96, PixelFormats.Bgra32, null, pixels, checked((int)frame.Stride));
        bitmap.Freeze();

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        encoder.Save(output);
        return fullPath;
    }
}
