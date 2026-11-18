using System.Windows;
using IPhoneMirror.App.Controls;

namespace IPhoneMirror.App.Services;

// Only an enabled preview retains its decoded hit-test mask. The catalog's
// weak cache can release inactive finishes without moving decoding onto clicks.
internal sealed class DeviceFrameHitTest(DeviceFrameProfile frame)
{
    private readonly byte[] _mask = frame.Mask;

    internal bool Contains(int turns, PreviewPointerEventArgs input) =>
        PhoneFrameGeometry.ContainsScreenPoint(frame, turns, input, _mask);
}

internal static class PhoneFrameGeometry
{
    internal static Rect ScreenBounds(DeviceFrameProfile frame, int turns, int surfaceWidth, int surfaceHeight)
    {
        bool landscape = (turns & 1) != 0;
        var width = landscape ? frame.FrameHeight : frame.FrameWidth;
        var height = landscape ? frame.FrameWidth : frame.FrameHeight;
        var scale = Math.Min(surfaceWidth / (double)width, surfaceHeight / (double)height);
        var bounds = frame.RotatedScreen(turns);
        return new Rect((surfaceWidth - width * scale) * 0.5 + bounds.X * width * scale,
            (surfaceHeight - height * scale) * 0.5 + bounds.Y * height * scale,
            bounds.Width * width * scale, bounds.Height * height * scale);
    }

    internal static bool ContainsScreenPoint(DeviceFrameProfile frame, int turns, PreviewPointerEventArgs input,
        byte[]? mask = null)
    {
        var bounds = ScreenBounds(frame, turns, input.SurfaceWidth, input.SurfaceHeight);
        if (!bounds.Contains(input.X, input.Y)) return false;
        double u = (input.X - bounds.X) / bounds.Width, v = (input.Y - bounds.Y) / bounds.Height;
        for (int i = 0; i < (turns % 4 + 4) % 4; ++i) (u, v) = (v, 1 - u);
        // The renderer suppresses the centered sensor when the source side is
        // ambiguous. That interior strip is then part of the visible screen.
        if (turns >= 4 && u > 1.0 / 9 && u < 8.0 / 9 && v > 0 && v < 0.125)
            return true;
        var x = Math.Clamp((int)(frame.ScreenX + u * frame.ScreenWidth), 0, frame.FrameWidth - 1);
        var y = Math.Clamp((int)(frame.ScreenY + v * frame.ScreenHeight), 0, frame.FrameHeight - 1);
        return (mask ?? frame.Mask)[y * frame.FrameWidth + x] >= 128;
    }

    internal static PreviewPointerEventArgs MapPointer(DeviceFrameProfile frame, int turns, PreviewPointerEventArgs input)
    {
        var bounds = ScreenBounds(frame, turns, input.SurfaceWidth, input.SurfaceHeight);
        return new(input.Kind,
            (int)Math.Round((input.X - bounds.X) / bounds.Width * input.SourceWidth),
            (int)Math.Round((input.Y - bounds.Y) / bounds.Height * input.SourceHeight),
            input.Button, input.Wheel, (int)input.SourceWidth, (int)input.SourceHeight,
            input.SourceWidth, input.SourceHeight, input.Rotation, input.TouchId, input.SourceWindow)
        { WindowsButtons = input.WindowsButtons };
    }
}
