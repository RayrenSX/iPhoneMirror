using System.IO;

namespace IPhoneMirror.App.Services.ExplorerMapping;

// Paths are relative to the selected phone file service, using '/' separators.
// An implementation must reject symlinks and recheck the expected snapshot for
// every read; the server never caches a complete phone file on the computer.
internal sealed record ExplorerFileEntry(string Path, string Name, bool IsDirectory,
    long Length, DateTimeOffset Modified, ExplorerFileProperties? WindowsProperties = null, bool IsReadOnly = false);

internal sealed record ExplorerStorageInfo(long TotalBytes, long AvailableBytes)
{
    internal bool IsValid => TotalBytes > 0 && AvailableBytes >= 0 && AvailableBytes <= TotalBytes;
    internal long UsedBytes => TotalBytes - AvailableBytes;
}

// Windows-specific metadata belongs to the mapped filesystem. Backends must
// persist supported properties before reporting a successful PROPPATCH. The
// last-modified time must also be applied to the underlying phone file.
internal sealed record ExplorerFileProperties(DateTimeOffset? CreationTime = null,
    DateTimeOffset? LastAccessTime = null, DateTimeOffset? LastModifiedTime = null,
    uint? Win32Attributes = null);

internal interface IExplorerFileStore
{
    bool IsReadOnly => true;
    Task<ExplorerStorageInfo?> GetStorageAsync(CancellationToken cancellation) => Task.FromResult<ExplorerStorageInfo?>(null);
    Task<ExplorerFileEntry?> StatAsync(string path, CancellationToken cancellation);
    // Directory previews may be cached. Content headers and mutations must use
    // a live snapshot, even when the displayed tree came from a previous run.
    Task<ExplorerFileEntry?> StatFreshAsync(string path, CancellationToken cancellation) => StatAsync(path, cancellation);
    Task<IReadOnlyList<ExplorerFileEntry>> ListAsync(string path, CancellationToken cancellation);
    Task<ReadOnlyMemory<byte>> ReadAsync(ExplorerFileEntry expected, long offset, int count,
        CancellationToken cancellation);
    Task PutAsync(string path, string localSourcePath, ExplorerFileEntry? expected, CancellationToken cancellation) =>
        Task.FromException(new UnauthorizedAccessException("The mapping is read-only."));
    Task MakeDirectoryAsync(string path, CancellationToken cancellation) =>
        Task.FromException(new UnauthorizedAccessException("The mapping is read-only."));
    Task MoveAsync(ExplorerFileEntry expectedSource, string destination, ExplorerFileEntry? expectedDestination,
        CancellationToken cancellation) => Task.FromException(new UnauthorizedAccessException("The mapping is read-only."));
    Task DeleteAsync(ExplorerFileEntry expected, CancellationToken cancellation) =>
        Task.FromException(new UnauthorizedAccessException("The mapping is read-only."));
    Task UpdatePropertiesAsync(ExplorerFileEntry expected, ExplorerFileProperties properties,
        CancellationToken cancellation) => Task.FromException(new UnauthorizedAccessException("Mapped metadata updates are not supported."));
}

internal sealed class ExplorerFileConflictException(string message) : IOException(message);
