using System.Runtime.CompilerServices;

namespace IPhoneMirror.App.Services.Automation;

// The same session gate is used by UI direct keys, device shortcuts and API
// commands. Transport serialization alone cannot protect a complete chord.
internal sealed class DeviceInputService
{
    private readonly ConditionalWeakTable<object, SemaphoreSlim> _keyGates = new();
    internal SemaphoreSlim KeyboardGate(DirectKeyboardRoute route) =>
        _keyGates.GetValue(route.Session, _ => new SemaphoreSlim(1, 1));

    internal async Task KeyAsync(DirectKeyboardRoute route, byte modifier, byte usage,
        Func<bool> current, CancellationToken cancellation)
    {
        var gate = KeyboardGate(route);
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        var pressed = false;
        try
        {
            if (!route.IsCurrent() || !current()) throw new OperationCanceledException();
            pressed = true;
            byte[] usages = usage == 0 ? [] : [usage];
            if (modifier != 0 && route.Transport != "BluetoothDirect")
                usages = usages.Concat(Enumerable.Range(0, 8).Where(i => (modifier & (1 << i)) != 0)
                    .Select(i => (byte)(0xE0 + i))).ToArray();
            await route.SendAsync(modifier, usages, current).ConfigureAwait(false);
            await Task.Delay(40, cancellation).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (pressed && route.IsCurrent())
                    await route.SendAsync(0, [], null).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
    }
}
