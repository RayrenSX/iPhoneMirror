namespace IPhoneMirror.App.Services;

internal sealed record MappingPreviewSurface(nint Window, nint Owner, string Device, ulong Session,
    uint Width, uint Height, int Rotation, BluetoothMouseDirection Portrait,
    BluetoothMouseDirection Landscape, bool ReverseX, bool ReverseY)
{
    internal (double X, double Y) ToDevice(double x, double y) =>
        BluetoothMouseOrientationMapper.MapNormalized(x, y, Width, Height, Rotation,
            Portrait, Landscape, ReverseX, ReverseY);
    internal (double X, double Y) ToPreview(double x, double y) =>
        BluetoothMouseOrientationMapper.UnmapNormalized(x, y, Width, Height, Rotation,
            Portrait, Landscape, ReverseX, ReverseY);
}

internal sealed record MappingPositionPick(KeyboardMappingEntry Entry, MappingPreviewSurface Surface,
    Action<KeyboardMappingEntry?> Completed);
