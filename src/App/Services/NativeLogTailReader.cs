using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IPhoneMirror.App.Services;

internal sealed class NativeLogTailReader
{
    private const int MaximumReadBytes = 256 * 1024;
    private const int MaximumLineCharacters = 16 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private (ulong Volume, ulong IdLow, ulong IdHigh)? _fileIdentity;
    private long _position;
    private string _partialLine = string.Empty;
    private bool _skipPartialLine;

    internal NativeLogTailReader(string? path = null)
    {
        Path = path ?? Environment.GetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE")
            ?? DiagnosticLogger.NativeLogPath;
    }

    internal string Path { get; }

    /// <summary>
    /// UI actions are echoed by the native logger with a variable structured
    /// prefix (for example sequence/level/category fields). Keep the live UI
    /// from displaying that echo twice regardless of the prefix format.
    /// </summary>
    internal static bool IsUiEventLine(string? line) =>
        !string.IsNullOrWhiteSpace(line) &&
        (line.StartsWith("ui_event ", StringComparison.OrdinalIgnoreCase) ||
         line.Contains(" ui_event ", StringComparison.OrdinalIgnoreCase));

    internal async Task<IReadOnlyList<string>> ReadNewLinesAsync()
    {
        if (!await _gate.WaitAsync(0)) return [];
        IReadOnlyList<string> archived = [];
        try
        {
            await using var stream = Open(Path);
            // The replacement can already be larger than the old read offset.
            // Check the open handle so rotation can never splice two files.
            var identity = GetIdentity(stream.SafeFileHandle);
            var replaced = identity.HasValue && _fileIdentity.HasValue && identity != _fileIdentity;
            if (replaced) archived = await ReadArchivedLinesAsync(_fileIdentity!.Value);
            if (stream.Length < _position || replaced)
            {
                _position = 0;
                _partialLine = string.Empty;
                _skipPartialLine = false;
                _decoder.Reset();
            }
            _fileIdentity = identity ?? _fileIdentity;
            var active = await ReadLinesAsync(stream);
            return archived.Count == 0 ? active : [.. archived, .. active];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return archived;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> ReadArchivedLinesAsync(
        (ulong Volume, ulong IdLow, ulong IdHigh) identity)
    {
        for (var index = 1; index <= 2; ++index)
        {
            try
            {
                await using var stream = Open($"{Path}.{index}");
                if (GetIdentity(stream.SafeFileHandle) == identity)
                    return await ReadLinesAsync(stream);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An archive can be cleaned or locked by a different reader.
            }
        }
        return [];
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 32 * 1024, useAsync: true);

    private async Task<IReadOnlyList<string>> ReadLinesAsync(FileStream stream)
    {
        var length = stream.Length;
        if (_position == 0 && length > MaximumReadBytes)
        {
            _position = length - MaximumReadBytes;
            stream.Position = _position - 1;
            _skipPartialLine = stream.ReadByte() != '\n';
        }
        if (length <= _position) return [];

        stream.Position = _position;
        var bytesToRead = (int)Math.Min(length - _position, MaximumReadBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(bytesToRead);
        try
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead));
            if (bytesRead == 0) return [];
            _position += bytesRead;

            var maximumCharacters = Encoding.UTF8.GetMaxCharCount(bytesRead);
            var characters = ArrayPool<char>.Shared.Rent(maximumCharacters);
            try
            {
                _decoder.Convert(buffer, 0, bytesRead, characters, 0, maximumCharacters,
                    flush: false, out _, out var charactersUsed, out _);
                var text = (_partialLine + new string(characters, 0, charactersUsed))
                    .Replace("\r\n", "\n", StringComparison.Ordinal);
                var lines = text.Split('\n');
                if (_skipPartialLine && lines.Length == 1) return [];
                var firstLine = _skipPartialLine ? 1 : 0;
                _skipPartialLine = false;
                _partialLine = LimitLine(lines[^1]);
                return lines.Skip(firstLine).Take(lines.Length - firstLine - 1)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(LimitLine)
                    .ToArray();
            }
            finally
            {
                ArrayPool<char>.Shared.Return(characters);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (ulong Volume, ulong IdLow, ulong IdHigh)? GetIdentity(SafeFileHandle handle)
    {
        const int FileIdInfo = 18;
        if (GetFileInformationByHandleEx(handle, FileIdInfo, out var info,
                (uint)Marshal.SizeOf<FileId>()))
            return (info.Volume, info.IdLow, info.IdHigh);
        if (GetFileInformationByHandle(handle, out var legacy))
            return (legacy.Volume, ((ulong)legacy.IdHigh << 32) | legacy.IdLow, 0);
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId
    {
        public ulong Volume;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyFileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created;
        public System.Runtime.InteropServices.ComTypes.FILETIME Accessed;
        public System.Runtime.InteropServices.ComTypes.FILETIME Modified;
        public uint Volume;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Links;
        public uint IdHigh;
        public uint IdLow;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,
        int informationClass, out FileId information, uint bufferSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,
        out LegacyFileInfo information);

    private static string LimitLine(string line) => line.Length <= MaximumLineCharacters
        ? line
        : line[..(MaximumLineCharacters - 1)] + '…';
}
