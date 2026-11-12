using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunToolbarFocusVideoTests(string output)
    {
        Directory.CreateDirectory(output);
        // Colored source and background make actual pixel coverage measurable,
        // including the black bars that disappear when a frame is stretched.
        BitmapSource Solid(int width, int height, double dpiX, double dpiY, Color color)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
            var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Pbgra32, null, pixels, width * 4);
            bitmap.Freeze(); return bitmap;
        }
        foreach (var dpi in new[] { 96d, 144d })
        foreach (var (name, sourceWidth, sourceHeight, host, expectedPicture) in new[]
        {
            ("portrait-in-wide-preview", 200, 400, new Rect(40, 40, 600, 400), new Rect(240, 40, 200, 400)),
            ("landscape-in-wide-preview", 400, 200, new Rect(40, 40, 600, 400), new Rect(40, 90, 600, 300)),
            ("portrait-in-tall-preview", 200, 400, new Rect(40, 40, 200, 440), new Rect(40, 60, 200, 400)),
            ("landscape-in-tall-preview", 400, 200, new Rect(40, 40, 200, 440), new Rect(40, 210, 200, 100)),
            ("matching-aspect", 200, 400, new Rect(40, 40, 200, 400), new Rect(40, 40, 200, 400))
        })
        {
            var scale = dpi / 96;
            var background = Solid((int)(680 * scale), (int)(520 * scale), dpi, dpi, Colors.Magenta);
            // Pixel aspect is authoritative for native video, even if image
            // metadata reports different horizontal and vertical DPI.
            var video = Solid(sourceWidth, sourceHeight, 192, 96, Colors.White);
            var composite = MainWindow.CompositeToolbarFocusFrame(background, video, host);
            InteractionAssert(composite.IsFrozen && composite.PixelWidth == background.PixelWidth &&
                composite.PixelHeight == background.PixelHeight && composite.DpiX == dpi && composite.DpiY == dpi,
                "Compositing changed the scene resolution, DPI or snapshot ownership.");
            var pixels = new byte[composite.PixelWidth * composite.PixelHeight * 4];
            composite.CopyPixels(pixels, composite.PixelWidth * 4, 0);
            Color At(double x, double y)
            {
                var index = ((int)Math.Floor(y * scale) * composite.PixelWidth + (int)Math.Floor(x * scale)) * 4;
                return Color.FromArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index]);
            }
            var centerX = host.Left + host.Width / 2; var centerY = host.Top + host.Height / 2;
            var whiteX = Enumerable.Range((int)(host.Left * scale), (int)(host.Width * scale))
                .Where(x => At((x + .5) / scale, centerY).R > 240).ToArray();
            var whiteY = Enumerable.Range((int)(host.Top * scale), (int)(host.Height * scale))
                .Where(y => At(centerX, (y + .5) / scale).R > 240).ToArray();
            var actual = new Rect(whiteX.Min() / scale, whiteY.Min() / scale,
                (whiteX.Max() - whiteX.Min() + 1) / scale, (whiteY.Max() - whiteY.Min() + 1) / scale);
            InteractionAssert(Math.Abs(actual.Left - expectedPicture.Left) <= 1 && Math.Abs(actual.Top - expectedPicture.Top) <= 1 &&
                Math.Abs(actual.Width - expectedPicture.Width) <= 1 && Math.Abs(actual.Height - expectedPicture.Height) <= 1,
                $"{name}/{dpi}: focus video is stretched or off-center: actual={actual}, expected={expectedPicture}.");
            InteractionAssert(At(20, 20) == Colors.Magenta && At(host.Left + 1, host.Top + 1) == Colors.Magenta,
                "Compositing overwrote UI outside the host or removed its rounded clip.");
            if (expectedPicture.Width < host.Width)
                InteractionAssert(At(host.Left + 20, centerY) == Colors.Black && At(host.Right - 20, centerY) == Colors.Black,
                    "The original scene shows through the horizontal video bars.");
            if (expectedPicture.Height < host.Height)
                InteractionAssert(At(centerX, host.Top + 5) == Colors.Black && At(centerX, host.Bottom - 5) == Colors.Black,
                    "The original scene shows through the vertical video bars.");
            using var stream = File.Create(Path.Combine(output, $"{name}-{dpi}.png"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(composite)); encoder.Save(stream);
            Console.WriteLine($"PASS {name} at {dpi} DPI: aspect preserved, centered video, black bars, clipped host and unchanged scene pixels.");
        }
        return 0;
    }
}
