using System.IO;
using System.Text;

namespace IPhoneMirror.App.Services;

internal sealed class NativeLogTailReader
{
    private const int MaximumReadBytes = 256 * 1024;
    private const int MaximumLineCharacters = 16 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _position;
    private string _partialLine = string.Empty;

    internal NativeLogTailReader(string? path = null)
    {
        Path = path ?? Environment.GetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE")
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iPhoneMirror-capture.log");
    }

    internal string Path { get; }

    internal async Task<IReadOnlyList<string>> ReadNewLinesAsync()
    {
        if (!await _gate.WaitAsync(0)) return [];
        try
        {
            if (!File.Exists(Path)) return [];
            await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 32 * 1024, useAsync: true);
            if (stream.Length < _position)
            {
                _position = 0;
                _partialLine = string.Empty;
                _decoder.Reset();
            }
            if (_position == 0 && stream.Length > MaximumReadBytes)
                _position = stream.Length - MaximumReadBytes;
            if (stream.Length <= _position) return [];

            stream.Position = _position;
            var bytesToRead = (int)Math.Min(stream.Length - _position, MaximumReadBytes);
            var buffer = new byte[bytesToRead];
            var bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0) return [];
            _position += bytesRead;

            var characters = new char[Encoding.UTF8.GetMaxCharCount(bytesRead)];
            _decoder.Convert(buffer, 0, bytesRead, characters, 0, characters.Length,
                flush: false, out _, out var charactersUsed, out _);
            var text = (_partialLine + new string(characters, 0, charactersUsed))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            var lines = text.Split('\n');
            _partialLine = LimitLine(lines[^1]);
            return lines.Take(lines.Length - 1)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(LimitLine)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string LimitLine(string line) => line.Length <= MaximumLineCharacters
        ? line
        : line[..(MaximumLineCharacters - 1)] + '…';
}
