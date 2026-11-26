using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Controls;

namespace IPhoneMirror.App.Services;

// Only an enabled preview retains its decoded hit-test mask. The catalog's
// weak cache can release inactive finishes without moving decoding onto clicks.
internal sealed class DeviceFrameHitTest
{
    private readonly DeviceFrameProfile _frame;
    private readonly byte[] _mask;
    private readonly Int32Rect _hiddenSensor;

    internal DeviceFrameHitTest(DeviceFrameProfile frame)
    {
        _frame = frame;
        _mask = frame.Mask;
        _hiddenSensor = PhoneFrameGeometry.HiddenSensorBounds(frame, _mask);
    }

    internal bool Contains(int turns, PreviewPointerEventArgs input) =>
        PhoneFrameGeometry.ContainsScreenPoint(_frame, turns, input, _mask, _hiddenSensor);
}

internal static class PhoneFrameGeometry
{
    internal static Rect ScreenBounds(DeviceFrameProfile frame, int turns, int surfaceWidth, int surfaceHeight)
    {
        var viewport = ArtworkBounds(frame, turns, surfaceWidth, surfaceHeight);
        if (viewport.IsEmpty) return Rect.Empty;
        var bounds = frame.RotatedScreen(turns);
        return new Rect(viewport.Left + bounds.X * viewport.Width, viewport.Top + bounds.Y * viewport.Height,
            bounds.Width * viewport.Width, bounds.Height * viewport.Height);
    }

    internal static Rect ArtworkBounds(DeviceFrameProfile frame, int turns, int surfaceWidth, int surfaceHeight)
    {
        var landscape = (turns & 1) != 0;
        return PreviewCoordinateMapper.ContentRect(surfaceWidth, surfaceHeight,
            (uint)(landscape ? frame.FrameHeight : frame.FrameWidth),
            (uint)(landscape ? frame.FrameWidth : frame.FrameHeight));
    }

    internal static Brush CreateScreenMask(DeviceFrameProfile frame, int turns)
    {
        var mask = frame.Mask;
        var width = frame.ScreenWidth;
        var height = frame.ScreenHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            var source = (frame.ScreenY + y) * frame.FrameWidth + frame.ScreenX;
            for (var x = 0; x < width; x++)
                pixels[(y * width + x) * 4 + 3] = mask[source + x];
        }
        if (turns >= 4)
        {
            var sensor = HiddenSensorBounds(frame, mask);
            for (var y = sensor.Y; y < sensor.Y + sensor.Height; y++)
            for (var x = sensor.X; x < sensor.X + sensor.Width; x++)
                pixels[((y - frame.ScreenY) * width + x - frame.ScreenX) * 4 + 3] = 255;
        }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        if (turns % 4 != 0) image = new TransformedBitmap(image, new RotateTransform(turns % 4 * 90));
        image.Freeze();
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    internal static bool ContainsScreenPoint(DeviceFrameProfile frame, int turns, PreviewPointerEventArgs input,
        byte[]? mask = null, Int32Rect? hiddenSensor = null)
    {
        var bounds = ScreenBounds(frame, turns, input.SurfaceWidth, input.SurfaceHeight);
        if (!bounds.Contains(input.X, input.Y) || input.X >= bounds.Right || input.Y >= bounds.Bottom) return false;
        double u = (input.X - bounds.X) / bounds.Width, v = (input.Y - bounds.Y) / bounds.Height;
        for (int i = 0; i < (turns % 4 + 4) % 4; ++i) (u, v) = (v, 1 - u);
        var x = frame.ScreenX + u * frame.ScreenWidth;
        var y = frame.ScreenY + v * frame.ScreenHeight;
        mask ??= frame.Mask;
        if (turns >= 4)
        {
            var sensor = hiddenSensor ?? HiddenSensorBounds(frame, mask);
            if (x >= sensor.X && x < sensor.X + sensor.Width &&
                y >= sensor.Y && y < sensor.Y + sensor.Height)
                return true;
        }
        return mask[Math.Clamp((int)y, 0, frame.FrameHeight - 1) * frame.FrameWidth +
            Math.Clamp((int)x, 0, frame.FrameWidth - 1)] >= 128;
    }

    internal static PreviewPointerEventArgs MapPointer(DeviceFrameProfile frame, int turns, PreviewPointerEventArgs input)
    {
        var bounds = ScreenBounds(frame, turns, input.SurfaceWidth, input.SurfaceHeight);
        // Keep the screen interval half-open after scaling. Rounding can push
        // its last visible pixel outside a downscaled source.
        return new(input.Kind,
            (int)Math.Floor((input.X - bounds.X) / bounds.Width * input.SourceWidth),
            (int)Math.Floor((input.Y - bounds.Y) / bounds.Height * input.SourceHeight),
            input.Button, input.Wheel, (int)input.SourceWidth, (int)input.SourceHeight,
            input.SourceWidth, input.SourceHeight, input.Rotation, input.TouchId, input.SourceWindow)
        { WindowsButtons = input.WindowsButtons };
    }

    // Same pixel bounds as find_center_sensor and the two-texel sampling
    // margin in D3D11PreviewRenderer. Coordinates are in unrotated artwork.
    internal static Int32Rect HiddenSensorBounds(DeviceFrameProfile frame, byte[] mask)
    {
        if (frame.ScreenWidth < 24 || frame.ScreenHeight < 48) return Int32Rect.Empty;
        var center = frame.ScreenX + frame.ScreenWidth / 2;
        var margin = frame.ScreenWidth / 9;
        var first = frame.ScreenX + margin;
        var last = frame.ScreenX + frame.ScreenWidth - margin;
        var limit = frame.ScreenY + frame.ScreenHeight / 8;
        int left = center, right = center, top = limit, bottom = frame.ScreenY;
        for (var y = frame.ScreenY; y < limit; y++)
        {
            var row = y * frame.FrameWidth;
            if (mask[row + center] >= 240 || mask[row + first] < 240 || mask[row + last - 1] < 240)
                continue;
            int rowLeft = center, rowRight = center + 1;
            while (rowLeft > first && mask[row + rowLeft - 1] < 240) rowLeft--;
            while (rowRight < last && mask[row + rowRight] < 240) rowRight++;
            left = Math.Min(left, rowLeft);
            right = Math.Max(right, rowRight);
            top = Math.Min(top, y);
            bottom = y + 1;
        }
        if (bottom - top < Math.Max(3, frame.ScreenHeight / 100) ||
            bottom >= limit || right - left < frame.ScreenWidth / 20)
            return Int32Rect.Empty;

        left = Math.Max(frame.ScreenX, left - 2);
        top = Math.Max(frame.ScreenY, top - 2);
        right = Math.Min(frame.ScreenX + frame.ScreenWidth, right + 2);
        bottom = Math.Min(frame.ScreenY + frame.ScreenHeight, bottom + 2);
        return new Int32Rect(left, top, right - left, bottom - top);
    }
}
