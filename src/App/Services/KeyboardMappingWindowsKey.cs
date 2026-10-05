using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Services;

// Delay a mapped Windows key until release so a standalone press can suppress
// Start without firing a mapping for Win+D/Tab/etc. On a chord, replay the
// delayed down and the chord key together, then let their real ups through.
internal sealed class KeyboardMappingWindowsKey
{
    private MappedKey? _held;
    private KeyboardMappingEntry? _candidate;
    private bool _replayed;
    internal bool HasHeldKey => _held is not null;
    internal void Cancel() => _candidate = null;

    internal MappingKeyResult Process(MappedKey key, bool down, bool allowed, bool modifierHeld,
        IReadOnlyList<KeyboardMappingEntry> mappings, Func<MappedKey[], bool> replay)
    {
        if (_held is { } held)
        {
            if (key.SamePhysicalKey(held))
            {
                if (down) return new(null, !_replayed);
                var result = new MappingKeyResult(allowed && !modifierHeld ? _candidate : null, !_replayed);
                _held = null;
                _candidate = null;
                _replayed = false;
                return result;
            }
            if (down)
            {
                _candidate = null;
                if (!_replayed)
                {
                    // A failed replay must not leak a chord as an unmodified
                    // key or execute the standalone mapping.
                    _replayed = replay([held, key]);
                    return new(null, true);
                }
            }
            return default;
        }
        if (!down || !key.IsWindows || !allowed || modifierHeld) return default;
        var mapping = mappings.FirstOrDefault(m => m.Enabled && m.Key!.SamePhysicalKey(key));
        if (mapping is null) return default;
        _held = key;
        _candidate = mapping.Action == MappedTouchAction.HoldUntilRelease ? null : mapping;
        return new(mapping.Action == MappedTouchAction.HoldUntilRelease ? mapping : null, true);
    }

    internal static bool Replay(MappedKey[] keys)
    {
        var inputs = keys.Select(key => new NativeInput
        {
            Type = 1,
            VirtualKey = (ushort)key.VirtualKey,
            Scan = (ushort)key.ScanCode,
            Flags = key.Extended ? 1u : 0u,
        }).ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent == inputs.Length) return true;
        if (sent > 0)
        {
            var releases = inputs.Take((int)sent).Reverse().ToArray();
            for (var i = 0; i < releases.Length; i++) releases[i].Flags |= 2;
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeInput>());
        }
        return false;
    }

    // INPUT is 40 bytes on the application's x64 target; KEYBDINPUT starts at 8.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal ushort VirtualKey;
        [FieldOffset(10)] internal ushort Scan;
        [FieldOffset(12)] internal uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
