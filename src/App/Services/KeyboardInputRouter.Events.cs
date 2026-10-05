using System.Diagnostics;

namespace IPhoneMirror.App.Services;

internal enum KeyboardEventOwner { Windows, ShortcutCandidate, Shortcut, Mapping, Direct, Retired }

// A physical press captures one destination and its matching release.
internal sealed record KeyboardPressRoute(KeyboardEventOwner Owner, bool Suppress,
    Action<bool>? Dispatch = null);

internal sealed partial class KeyboardInputRouter
{
    private sealed class Press(MappedKey key,
        Func<MappedKey, bool, KeyboardPressRoute> resolve, Action<MappedKey, bool> replay)
    {
        internal readonly MappedKey Key = key;
        internal readonly Func<MappedKey, bool, KeyboardPressRoute> Resolve = resolve;
        internal readonly Action<MappedKey, bool> Replay = replay;
        internal KeyboardPressRoute Route = new(KeyboardEventOwner.ShortcutCandidate, true);
    }
    private readonly Dictionary<(int Code, bool Extended), Press> _presses = [];
    internal KeyboardShortcutRecognizer Shortcuts { get; } = new();
    internal int PressedKeyCount => _presses.Count;
    internal bool TraceEnabled { get; set; }
    internal uint PressedModifiers
    {
        get
        {
            uint result = 0;
            foreach (var retired in _retired) result |= KeyboardShortcutRecognizer.Modifier(retired);
            foreach (var press in _presses.Values) result |= KeyboardShortcutRecognizer.Modifier(press.Key.VirtualKey);
            return result;
        }
    }
    private static (int, bool) PhysicalKey(MappedKey key) =>
        (key.ScanCode == 0 ? key.VirtualKey + 0x1000 : key.ScanCode, key.Extended);

    // UI dispatcher only. Hook is the sole production source; tests/window
    // fallback can use this same method when no hook is installed.
    internal bool RouteEvent(MappedKey key, bool down,
        Func<MappedKey, bool, KeyboardPressRoute> resolve,
        Action<MappedKey, bool> replay, bool deferStandaloneModifier = false, bool bufferShortcutModifiers = true)
    {
        var id = PhysicalKey(key);
        if (!_presses.ContainsKey(id))
            foreach (var pair in _presses)
                if (pair.Value.Key.SamePhysicalKey(key)) { id = pair.Key; break; }
        if (_presses.TryGetValue(id, out var existing))
        {
            if (down) return existing.Route.Suppress;
            var wasCandidate = existing.Route.Owner == KeyboardEventOwner.ShortcutCandidate;
            if (wasCandidate) Deliver(existing, chord: _presses.Count > 1, delayed: true);
            _presses.Remove(id); // remove before callbacks that may reset/close
            _retired.Remove(key.VirtualKey);
            existing.Route.Dispatch?.Invoke(false);
            TraceEvent("KeyUp", existing);
            if (wasCandidate && !existing.Route.Suppress)
            {
                existing.Replay(existing.Key, false);
                return true; // replay down/up in order; original up must not overtake replay down
            }
            return existing.Route.Suppress;
        }
        if (!down) { _retired.Remove(key.VirtualKey); return false; }
        var press = new Press(key, resolve, replay);
        _presses.Add(id, press);
        if (_retired.Contains(key.VirtualKey) || (Mode == KeyboardInputMode.None && RequestedMode != KeyboardInputMode.None))
        {
            press.Route = new(KeyboardEventOwner.Retired, false);
            return false;
        }
        var modifiers = PressedModifiers;
        if (KeyboardShortcutRecognizer.Modifier(key.VirtualKey) != 0)
        {
            if ((bufferShortcutModifiers && Shortcuts.IsCandidate(modifiers)) ||
                (deferStandaloneModifier && _presses.Count == 1))
            {
                TraceEvent("ShortcutCandidate", press);
                return true;
            }
            var replayedPrefix = FlushCandidates(chord: _presses.Count > 1, except: press);
            Deliver(press, chord: _presses.Count > 1, delayed: replayedPrefix);
            return replayedPrefix || press.Route.Suppress;
        }
        // Never steal a modifier whose down already reached another owner.
        var canMatch = !_retired.Any(k => KeyboardShortcutRecognizer.Modifier(k) != 0);
        foreach (var held in _presses.Values)
            if (KeyboardShortcutRecognizer.Modifier(held.Key.VirtualKey) != 0 &&
                held.Route.Owner is not (KeyboardEventOwner.ShortcutCandidate or KeyboardEventOwner.Shortcut or KeyboardEventOwner.Windows))
                canMatch = false;
        var match = canMatch ? Shortcuts.Match(key.VirtualKey, modifiers) : null;
        if (match is not null)
        {
            foreach (var held in _presses.Values)
                if (held.Route.Owner == KeyboardEventOwner.ShortcutCandidate)
                    held.Route = new(KeyboardEventOwner.Shortcut, true);
            TraceEvent("ShortcutMatched:" + match.Name, press);
            match.Execute(); // disposition committed before reentrant UI work
            return true;
        }
        var replayed = FlushCandidates(chord: true, except: press);
        Deliver(press, chord: modifiers != 0, delayed: replayed);
        return replayed || press.Route.Suppress;
    }
    // Mouse shortcut triggers share the physical keyboard modifier state.
    // They never read WPF/async modifier state that may omit buffered prefixes.
    internal bool RouteShortcutTrigger(int virtualKey)
    {
        if (_retired.Any(k => KeyboardShortcutRecognizer.Modifier(k) != 0)) return false;
        foreach (var held in _presses.Values)
            if (KeyboardShortcutRecognizer.Modifier(held.Key.VirtualKey) != 0 &&
                held.Route.Owner is not (KeyboardEventOwner.ShortcutCandidate or KeyboardEventOwner.Shortcut or KeyboardEventOwner.Windows)) return false;
        var match = Shortcuts.Match(virtualKey, PressedModifiers);
        if (match is null) return false;
        foreach (var held in _presses.Values)
            if (held.Route.Owner == KeyboardEventOwner.ShortcutCandidate)
                held.Route = new(KeyboardEventOwner.Shortcut, true);
        match.Execute();
        return true;
    }

    private bool FlushCandidates(bool chord, Press? except = null)
    {
        var replayed = false;
        foreach (var held in _presses.Values.ToArray())
            if (!ReferenceEquals(held, except) && held.Route.Owner == KeyboardEventOwner.ShortcutCandidate)
            {
                Deliver(held, chord, delayed: true);
                replayed |= !held.Route.Suppress;
            }
        return replayed;
    }
    private void Deliver(Press press, bool chord, bool delayed)
    {
        press.Route = press.Resolve(press.Key, chord);
        press.Route.Dispatch?.Invoke(true);
        // Replays never re-enter routing: injected hook events are ignored and
        // legacy window adapters do not forward while the hook is active.
        if (delayed && !press.Route.Suppress) press.Replay(press.Key, true);
        TraceEvent("InputDispatched", press);
    }
    internal void ReleaseAllPressedKeys()
    {
        // Retire every lifetime before calling external dispatchers: cleanup
        // can reenter the router or throw, but may never skip another release.
        var releases = _presses.Values.Select(press => (Press: press, Dispatch: press.Route.Dispatch)).ToArray();
        foreach (var (press, _) in releases)
            press.Route = new(KeyboardEventOwner.Retired, press.Route.Suppress);
        foreach (var (press, dispatch) in releases)
        {
            try { dispatch?.Invoke(false); }
            catch (Exception error)
            {
                DiagnosticLogger.ReverseControlWarning("keyboard_input", "key_release_failed", ("error", error.Message));
            }
            TraceEvent("InputReleased", press);
        }
        // Tombstones survive until physical up, so repeats and releases can
        // never acquire another device or backend after focus/session changes.
    }
    [Conditional("DEBUG")]
    private void TraceEvent(string name, Press press)
    {
        if (TraceEnabled)
            Debug.WriteLine($"[KeyboardRouter] {name} key={press.Key.VirtualKey:X2} scan={press.Key.ScanCode:X2} owner={press.Route.Owner}");
    }
}
