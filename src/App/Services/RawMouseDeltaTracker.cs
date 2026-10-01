namespace IPhoneMirror.App.Services;

internal sealed class RawMouseDeltaTracker
{
    private (nint Device, bool VirtualDesktop, int Width, int Height, int X, int Y)? _absolute;
    internal void Reset() => _absolute = null;

    internal (double X, double Y) Translate(nint device, ushort flags, int x, int y,
        int screenWidth, int screenHeight)
    {
        if ((flags & 1) == 0)
        {
            Reset();
            return (x, y);
        }
        var virtualDesktop = (flags & 2) != 0;
        var previous = _absolute;
        _absolute = (device, virtualDesktop, screenWidth, screenHeight, x, y);
        if (x is < 0 or > 65535 || y is < 0 or > 65535 || screenWidth <= 1 || screenHeight <= 1)
        {
            Reset();
            return (0, 0);
        }
        if (previous is not { } last || last.Device != device || last.VirtualDesktop != virtualDesktop ||
            last.Width != screenWidth || last.Height != screenHeight) return (0, 0);
        // RDP sends absolute positions in 0..65535. Converting the difference
        // to desktop pixels avoids pinning iOS's pointer in the bottom corner.
        return ((x - last.X) * (screenWidth - 1.0) / 65535,
            (y - last.Y) * (screenHeight - 1.0) / 65535);
    }
}
