namespace IPhoneMirror.App.Models;

internal sealed record IndependentWindowLayout(int? X = null, int? Y = null,
    int? Width = null, int? Height = null, bool Fixed = false)
{
    internal bool HasPosition => X.HasValue && Y.HasValue;
    internal bool HasSize => Width.HasValue && Height.HasValue;
    internal bool IsEmpty => !HasPosition && !HasSize && !Fixed;
    internal bool IsValid =>
        X.HasValue == Y.HasValue && Width.HasValue == Height.HasValue &&
        X is null or (>= -1_000_000 and <= 1_000_000) &&
        Y is null or (>= -1_000_000 and <= 1_000_000) &&
        Width is null or (>= 96 and <= 32767) && Height is null or (>= 96 and <= 32767);
}

internal enum IndependentWindowLayoutMode { Automatic, Last, Preset, Custom }

internal sealed record IndependentWindowLayoutHistory(bool UseLastLayout = false,
    IndependentWindowLayout? LastLayout = null, IndependentWindowLayoutMode? Mode = null,
    IndependentWindowLayout? CustomLayout = null, bool? AutoFix = null);
