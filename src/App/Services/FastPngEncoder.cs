using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

// The API needs lossless pixels at low CPU cost. The WPF encoder optimizes
// file size; use fast deflate and a fixed PNG Sub filter for repeated captures.
internal static class FastPngEncoder
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static byte[] Encode(VideoFrame frame, CancellationToken cancellation)
    {
        ScreenshotService.ValidateFrame(frame);
        cancellation.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], frame.Height);
        header[8] = 8;
        header[9] = 6; // RGBA, 8 bits/channel; retain alpha and full resolution.
        WriteChunk(output, "IHDR"u8, header);
        using (var chunks = new IdatStream(output))
        {
            using var deflate = new ZLibStream(chunks, CompressionLevel.Fastest, leaveOpen: true);
            var rowBytes = checked((int)frame.Width * 4);
            var row = ArrayPool<byte>.Shared.Rent(rowBytes + 1);
            try
            {
                for (var y = 0; y < frame.Height; y++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var source = frame.Pixels.AsSpan(checked((int)(y * frame.Stride)), rowBytes);
                    row[0] = 1; // Sub: subtract the preceding pixel, modulo 256.
                    byte red = 0, green = 0, blue = 0, alpha = 0;
                    for (var x = 0; x < rowBytes; x += 4)
                    {
                        row[x + 1] = unchecked((byte)(source[x + 2] - red));
                        row[x + 2] = unchecked((byte)(source[x + 1] - green));
                        row[x + 3] = unchecked((byte)(source[x] - blue));
                        row[x + 4] = unchecked((byte)(source[x + 3] - alpha));
                        red = source[x + 2]; green = source[x + 1];
                        blue = source[x]; alpha = source[x + 3];
                    }
                    deflate.Write(row.AsSpan(0, rowBytes + 1));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(row); }
        }
        cancellation.ThrowIfCancellationRequested();
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(field, data.Length);
        stream.Write(field); stream.Write(type); stream.Write(data);
        var crc = uint.MaxValue;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(field, ~crc);
        stream.Write(field);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var crc = n;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0);
            table[n] = crc;
        }
        return table;
    }

    // Bound compression scratch space and write IDAT chunks directly, without
    // keeping a second full compressed image alongside the HTTP response.
    private sealed class IdatStream(Stream destination) : Stream
    {
        private readonly byte[] _buffer = new byte[65536];
        private int _count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                var size = Math.Min(data.Length, _buffer.Length - _count);
                data[..size].CopyTo(_buffer.AsSpan(_count));
                data = data[size..]; _count += size;
                if (_count == _buffer.Length) Flush();
            }
        }
        public override void Flush()
        {
            if (_count == 0) return;
            WriteChunk(destination, "IDAT"u8, _buffer.AsSpan(0, _count));
            _count = 0;
        }
        protected override void Dispose(bool disposing) { if (disposing) Flush(); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
