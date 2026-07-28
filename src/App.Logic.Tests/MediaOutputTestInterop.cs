namespace IPhoneMirror.App.Interop;

public sealed record VideoFrame(
    uint Width, uint Height, uint Stride, long Timestamp100Ns, byte[] Pixels);
internal sealed record AudioPacket(ulong Sequence, uint SampleRate,
    ushort Channels, ushort BitsPerSample, byte[] Pcm);
