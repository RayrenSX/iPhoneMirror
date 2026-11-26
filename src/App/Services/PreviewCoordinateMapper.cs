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

    internal static Rect PlaceOverlayToolbar(Rect preview, Rect picture, Rect workArea, Size size, double gap)
    {
        var slots = new[] {
            new Rect(preview.Left, preview.Top, Math.Max(0, picture.Left - preview.Left), preview.Height),
            new Rect(picture.Right, preview.Top, Math.Max(0, preview.Right - picture.Right), preview.Height),
            new Rect(preview.Left, preview.Top, preview.Width, Math.Max(0, picture.Top - preview.Top)),
            new Rect(preview.Left, picture.Bottom, preview.Width, Math.Max(0, preview.Bottom - picture.Bottom)) };
        foreach (var candidate in slots)
        {
            var slot = Rect.Intersect(candidate, workArea);
            if (slot.IsEmpty) continue;
            if (slot.Width >= size.Width + gap * 2 && slot.Height >= size.Height + gap * 2)
                return new Rect(slot.Left + gap, slot.Top + gap, size.Width, size.Height);
        }
        var x = Math.Clamp(preview.Left + (preview.Width - size.Width) / 2,
            workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width));
        if (preview.Top - size.Height - gap >= workArea.Top)
            return new Rect(x, preview.Top - size.Height - gap, size.Width, size.Height);
        if (preview.Bottom + size.Height + gap <= workArea.Bottom)
            return new Rect(x, preview.Bottom + gap, size.Width, size.Height);
        var y = Math.Clamp(preview.Top + gap, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height));
        if (preview.Left - size.Width - gap >= workArea.Left)
            return new Rect(preview.Left - size.Width - gap, y, size.Width, size.Height);
        if (preview.Right + size.Width + gap <= workArea.Right)
            return new Rect(preview.Right + gap, y, size.Width, size.Height);
        // An edge-to-edge fullscreen picture leaves no outside space. This
        // last-resort position leaves the preview geometry untouched.
        return new Rect(x, Math.Clamp(preview.Bottom - size.Height - gap,
            workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)), size.Width, size.Height);
    }
}
