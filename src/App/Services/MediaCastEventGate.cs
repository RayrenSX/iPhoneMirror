namespace IPhoneMirror.App.Services;

internal sealed class MediaCastEventGate
{
    private long _generation;
    private object? _boundSource;

    internal long CurrentGeneration => _generation;

    internal long BeginGeneration()
    {
        _boundSource = null;
        return ++_generation;
    }

    internal bool TryBind(long generation, object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (generation != _generation) return false;
        _boundSource = source;
        return true;
    }

    internal void Invalidate()
    {
        _boundSource = null;
        ++_generation;
    }

    internal bool IsCurrent(long generation, object? source) =>
        source is not null && generation == _generation &&
        ReferenceEquals(source, _boundSource);
}
