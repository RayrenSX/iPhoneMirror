using System.Windows;

namespace IPhoneMirror.App.Services;

// Shared by mouse reverse control, visual picking and mapping markers. All
// dimensions here use one coordinate unit (native client pixels in production).
internal static class PreviewCoordinateMapper
{
    internal static Rect ContentRect(double width, double height, uint videoWidth, uint videoHeight)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            videoWidth == 0 || videoHeight == 0) return Rect.Empty;
        var aspect = (double)videoWidth / videoHeight;
        var imageWidth = Math.Min(width, height * aspect);
        var imageHeight = Math.Min(height, width / aspect);
        // Match D3D11PreviewRenderer's integer-window rounding tolerance.
        if (width - imageWidth < 1 && height - imageHeight < 1 &&
            Math.Abs(width / height - aspect) / aspect <= 1 / Math.Max(width, height))
            return new Rect(0, 0, width, height);
        return new Rect((width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight);
    }

    internal static (double X, double Y)? Normalize(double x, double y, double width, double height,
        uint videoWidth, uint videoHeight)
    {
        var rect = ContentRect(width, height, videoWidth, videoHeight);
        if (rect.IsEmpty || !double.IsFinite(x) || !double.IsFinite(y) ||
            x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom) return null;
        return ((x - rect.Left) / rect.Width, (y - rect.Top) / rect.Height);
    }

    internal static Point Project(double x, double y, double width, double height,
        uint videoWidth, uint videoHeight)
    {
        var rect = ContentRect(width, height, videoWidth, videoHeight);
        return rect.IsEmpty ? new Point() : new Point(rect.Left + x * rect.Width, rect.Top + y * rect.Height);
    }
}
