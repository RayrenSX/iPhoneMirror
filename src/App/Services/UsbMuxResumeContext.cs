namespace IPhoneMirror.App.Services;

// Owned by one native capture lifetime, never persisted to disk. A consumed
// checkpoint can only be replaced by a bridge which has fully exited.
internal sealed class UsbMuxResumeContext
{
    private string? _checkpoint;
    internal string? Take() => Interlocked.Exchange(ref _checkpoint, null);
    internal void Save(string checkpoint) => Volatile.Write(ref _checkpoint, checkpoint);
}
