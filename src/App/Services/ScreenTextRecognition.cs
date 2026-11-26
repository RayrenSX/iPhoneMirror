using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace IPhoneMirror.App.Services;

internal static class ScreenTextRecognition
{
    internal static async Task<ScreenTextPage> ReadPageAsync(VideoFrame frame, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ScreenshotService.ValidateFrame(frame);
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ??
            OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault();
        if (engine is null)
            throw new InvalidOperationException(LocalizationService.Get("ScreenTextNoLanguage"));

        var width = checked((int)frame.Width);
        var height = checked((int)frame.Height);
        var stride = checked((int)frame.Stride);
        var scale = Math.Max(1.0, Math.Max(width, height) / (double)OcrEngine.MaxImageDimension);
        var outputWidth = Math.Max(1, (int)(width / scale));
        var outputHeight = Math.Max(1, (int)(height / scale));
        var scaleX = (double)width / outputWidth;
        var scaleY = (double)height / outputHeight;
        var outputStride = checked(outputWidth * 4);
        var length = checked(outputStride * outputHeight);
        var pixels = frame.Pixels;
        if (scale > 1 || stride != outputStride)
        {
            pixels = new byte[length];
            for (var y = 0; y < outputHeight; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                var sourceRow = (int)(y * scaleY) * stride;
                var row = y * outputStride;
                if (scale == 1)
                {
                    frame.Pixels.AsSpan(sourceRow, outputStride).CopyTo(pixels.AsSpan(row));
                    continue;
                }
                for (var x = 0; x < outputWidth; x++)
                    frame.Pixels.AsSpan(sourceRow + (int)(x * scaleX) * 4, 4).CopyTo(pixels.AsSpan(row + x * 4));
            }
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(0, length),
            BitmapPixelFormat.Bgra8, outputWidth, outputHeight, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellation);
        return new ScreenTextPage(result.Lines.Select(line => (line.Text,
            line.Words.Select(word =>
            {
                var bounds = word.BoundingRect;
                return (word.Text, new Rect(bounds.X * scaleX, bounds.Y * scaleY,
                    bounds.Width * scaleX, bounds.Height * scaleY));
            }))));
    }
}
