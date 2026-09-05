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

    /// <summary>
    /// Maps an absolute preview point into the device's normalized coordinate
    /// space using the same rotation, per-orientation direction, and inversion
    /// settings as relative mouse movement.
    /// </summary>
    internal static (double X, double Y) MapNormalized(
        double x, double y, uint displayedWidth, uint displayedHeight,
        int displayRotation, BluetoothMouseDirection portraitDirection,
        BluetoothMouseDirection landscapeDirection, bool reverseHorizontal,
        bool reverseVertical)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        var turns = ((displayRotation % 4) + 4) % 4;
        var sourceWidth = (turns & 1) == 0 ? displayedWidth : displayedHeight;
        var sourceHeight = (turns & 1) == 0 ? displayedHeight : displayedWidth;
        var orientation = Detect(sourceWidth, sourceHeight);
        var direction = orientation == BluetoothDeviceOrientation.Landscape
            ? landscapeDirection : portraitDirection;

        (x, y) = ApplyAbsoluteQuarterTurn(x, y, turns);
        (x, y) = ApplyAbsoluteDirection(x, y, direction);
        if (reverseHorizontal) x = 1 - x;
        if (reverseVertical) y = 1 - y;
        return (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
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

    private static (double X, double Y) ApplyAbsoluteQuarterTurn(
        double x, double y, int turns) => turns switch
    {
        1 => (1 - y, x),
        2 => (1 - x, 1 - y),
        3 => (y, 1 - x),
        _ => (x, y),
    };

    private static (double X, double Y) ApplyAbsoluteDirection(
        double x, double y, BluetoothMouseDirection direction) => direction switch
    {
        BluetoothMouseDirection.Right => (1 - y, x),
        BluetoothMouseDirection.Down => (1 - x, 1 - y),
        BluetoothMouseDirection.Left => (y, 1 - x),
        _ => (x, y),
    };
}
