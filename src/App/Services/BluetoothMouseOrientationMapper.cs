namespace IPhoneMirror.App.Services;

internal enum BluetoothDeviceOrientation
{
    Unknown,
    Portrait,
    Landscape,
}

internal enum BluetoothMouseDirection
{
    Up,
    Right,
    Down,
    Left,
}

internal static class BluetoothMouseOrientationMapper
{
    internal static BluetoothDeviceOrientation Detect(uint width, uint height)
    {
        if (width == 0 || height == 0 || width == height)
            return BluetoothDeviceOrientation.Unknown;
        return width > height
            ? BluetoothDeviceOrientation.Landscape
            : BluetoothDeviceOrientation.Portrait;
    }

    internal static (double X, double Y) Map(
        double dx, double dy, uint displayedWidth, uint displayedHeight,
        int displayRotation, BluetoothMouseDirection portraitDirection,
        BluetoothMouseDirection landscapeDirection, bool reverseHorizontal,
        bool reverseVertical)
    {
        var turns = ((displayRotation % 4) + 4) % 4;
        var sourceWidth = (turns & 1) == 0 ? displayedWidth : displayedHeight;
        var sourceHeight = (turns & 1) == 0 ? displayedHeight : displayedWidth;
        var orientation = Detect(sourceWidth, sourceHeight);
        var direction = orientation == BluetoothDeviceOrientation.Landscape
            ? landscapeDirection : portraitDirection;

        // Convert movement in a manually rotated preview back to the device
        // coordinate space before applying the user's per-orientation mapping.
        (dx, dy) = ApplyQuarterTurn(dx, dy, turns);
        (dx, dy) = ApplyDirection(dx, dy, direction);
        if (reverseHorizontal) dx = -dx;
        if (reverseVertical) dy = -dy;
        return (dx, dy);
    }

    private static (double X, double Y) ApplyQuarterTurn(double dx, double dy,
        int turns) => turns switch
    {
        1 => (-dy, dx),
        2 => (-dx, -dy),
        3 => (dy, -dx),
        _ => (dx, dy),
    };

    private static (double X, double Y) ApplyDirection(double dx, double dy,
        BluetoothMouseDirection direction) => direction switch
    {
        BluetoothMouseDirection.Right => (-dy, dx),
        BluetoothMouseDirection.Down => (-dx, -dy),
        BluetoothMouseDirection.Left => (dy, -dx),
        _ => (dx, dy),
    };
}
