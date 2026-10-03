using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace IPhoneMirror.App.Services;

// Cross-process UI Automation can block. Query it on a bounded background
// worker, never from WH_KEYBOARD_LL. Unknown/stale focus is fail-closed.
internal sealed class KeyboardMappingFocusGuard : IDisposable
{
    private readonly Timer _timer;
    private int _checking;
    private int _disposed;
    private long _focusGeneration;
    private readonly WinEventProc _focusChanged;
    private readonly nint _focusHook;
    private readonly nint _foregroundHook;
    private Snapshot _snapshot = new(0, 0, 0, false);
    private sealed record Snapshot(nint Window, nint Focus, long Time, bool Allowed, long Generation = -1);

    internal KeyboardMappingFocusGuard()
    {
        _focusChanged = (_, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref _focusGeneration);
            Volatile.Write(ref _snapshot, new(0, 0, 0, false));
        };
        _focusHook = SetWinEventHook(0x8005, 0x8005, 0, _focusChanged, 0, 0, 0);
        _foregroundHook = SetWinEventHook(3, 3, 0, _focusChanged, 0, 0, 0);
        _timer = new Timer(_ => Check(), null, 0, 100);
    }

    internal bool Allows(nint foreground)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (foreground == 0 || !GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info) ||
            info.Caret != 0 || (info.Flags & 0x1C) != 0) return false;
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Allowed && snapshot.Window == foreground && snapshot.Focus == info.Focus &&
            snapshot.Generation == Interlocked.Read(ref _focusGeneration) &&
            Stopwatch.GetElapsedTime(snapshot.Time) < TimeSpan.FromMilliseconds(350);
    }

    private void Check()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            var generation = Interlocked.Read(ref _focusGeneration);
            var foreground = GetForegroundWindow();
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            var allowed = GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info) && info.Caret == 0;
            var element = allowed ? AutomationElement.FocusedElement : null;
            allowed = element is not null && !element.Current.IsPassword && element.Current.IsEnabled;
            if (allowed && element is not null)
            {
                GetWindowThreadProcessId(foreground, out var processId);
                if (element.Current.ProcessId != processId) allowed = false;
                var type = element.Current.ControlType;
                if (type == ControlType.Edit || type == ControlType.Document || type == ControlType.ComboBox)
                    allowed = false;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                    !((ValuePattern)pattern).Current.IsReadOnly) allowed = false;
                // UIA providers can omit Edit while exposing rich editable text.
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out _)) allowed = false;
            }
            if (GetForegroundWindow() != foreground || generation != Interlocked.Read(ref _focusGeneration) ||
                _focusHook == 0 || _foregroundHook == 0) allowed = false;
            Volatile.Write(ref _snapshot, new(foreground, info.Focus, Stopwatch.GetTimestamp(), allowed, generation));
        }
        catch
        {
            Volatile.Write(ref _snapshot, new(0, 0, Stopwatch.GetTimestamp(), false));
        }
        finally { Volatile.Write(ref _checking, 0); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
        if (_focusHook != 0) UnhookWinEvent(_focusHook);
        if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook);
    }
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module,
        WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal int Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
