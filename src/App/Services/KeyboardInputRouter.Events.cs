using System.Diagnostics;

namespace IPhoneMirror.App.Services;

internal enum KeyboardEventOwner { Windows, ShortcutCandidate, Shortcut, Mapping, Direct, Retired }

// A physical press captures one destination and its matching release.
internal sealed record KeyboardPressRoute(KeyboardEventOwner Owner, bool Suppress,
    Action<bool>? Dispatch = null);

internal readonly record struct KeyboardRouteDecision(string Reason,
    KeyboardEventOwner? Owner = null, string? Shortcut = null);

internal sealed partial class KeyboardInputRouter
{
    private sealed class Press(MappedKey key,
        Func<MappedKey, bool, KeyboardPressRoute> resolve, Action<MappedKey, bool> replay)
    {
        internal readonly MappedKey Key = key;
        internal readonly Func<MappedKey, bool, KeyboardPressRoute> Resolve = resolve;
        internal readonly Action<MappedKey, bool> Replay = replay;
        internal KeyboardPressRoute Route = new(KeyboardEventOwner.ShortcutCandidate, true);
        internal uint? LastDownMessageTime;
        internal bool WasShortcut;
        internal bool WindowDownObserved;
    }
    private readonly Dictionary<(int Code, bool Extended), Press> _presses = [];
    private readonly uint?[] _lastKeyMessageTimes = new uint?[512];
    private readonly bool?[] _lastKeyMessageDown = new bool?[512];
    private uint? _windowShortcutBarrierTime;
    internal KeyboardShortcutRecognizer Shortcuts { get; } = new();
    internal int PressedKeyCount => _presses.Count;
    internal bool HasPressedKey(MappedKey key) => _presses.Values.Any(press => press.Key.SamePhysicalKey(key)) ||
        _retired.Contains(key.VirtualKey);
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
        Action<MappedKey, bool> replay, bool deferStandaloneModifier = false, bool bufferShortcutModifiers = true,
        Action<KeyboardRouteDecision>? diagnose = null, uint? hookTime = null)
    {
        if (hookTime is { } observedTime) ObserveKeyMessageTime(key, observedTime, down);
        var id = PhysicalKey(key);
        if (!_presses.ContainsKey(id))
            foreach (var pair in _presses)
                if (pair.Value.Key.SamePhysicalKey(key)) { id = pair.Key; break; }
        if (_presses.TryGetValue(id, out var existing))
        {
            if (down)
            {
                if (hookTime is not null) existing.LastDownMessageTime = hookTime;
                diagnose?.Invoke(new("repeated_down", existing.Route.Owner));
                return existing.Route.Suppress;
            }
            var wasCandidate = existing.Route.Owner == KeyboardEventOwner.ShortcutCandidate;
            if (wasCandidate) Deliver(existing, chord: _presses.Count > 1, delayed: true);
            _presses.Remove(id); // remove before callbacks that may reset/close
            _retired.Remove(key.VirtualKey);
            existing.Route.Dispatch?.Invoke(false);
            TraceEvent("KeyUp", existing);
            diagnose?.Invoke(new("key_released", existing.Route.Owner));
            if (wasCandidate && !existing.Route.Suppress)
            {
                existing.Replay(existing.Key, false);
                return true; // replay down/up in order; original up must not overtake replay down
            }
            // A recovered down already reached Windows before the preview
            // saw it. Let that physical up reach Windows as well, otherwise
            // its key state remains down and the next fresh press is marked
            // as an autorepeat. The window adapter still consumes the up.
            return existing.WindowDownObserved ? false : existing.Route.Suppress;
        }
        if (!down)
        {
            var retired = _retired.Remove(key.VirtualKey);
            diagnose?.Invoke(new(retired ? "retired_key_released" : "untracked_key_up"));
            return false;
        }
        var press = new Press(key, resolve, replay) { LastDownMessageTime = hookTime };
        _presses.Add(id, press);
        if (_retired.Contains(key.VirtualKey) || (Mode == KeyboardInputMode.None && RequestedMode != KeyboardInputMode.None))
        {
            press.Route = new(KeyboardEventOwner.Retired, false);
            diagnose?.Invoke(new(_retired.Contains(key.VirtualKey) ? "retired_key" : "handoff_pending", press.Route.Owner));
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
        var match = MatchOwnedShortcut(key.VirtualKey, out var reason);
        if (match is not null)
        {
            foreach (var held in _presses.Values)
                if (held.Route.Owner == KeyboardEventOwner.ShortcutCandidate)
                    held.Route = new(KeyboardEventOwner.Shortcut, true);
            TraceEvent("ShortcutMatched:" + match.Name, press);
            press.WasShortcut = true;
            diagnose?.Invoke(new(reason, press.Route.Owner, match.Name));
            match.Execute(); // disposition committed before reentrant UI work
            return true;
        }
        var replayed = FlushCandidates(chord: true, except: press);
        Deliver(press, chord: modifiers != 0, delayed: replayed);
        diagnose?.Invoke(new(reason, press.Route.Owner));
        return replayed || press.Route.Suppress;
    }

    private RoutedShortcut? MatchOwnedShortcut(int virtualKey, out string reason)
    {
        reason = "retired_modifier";
        if (_retired.Any(key => KeyboardShortcutRecognizer.Modifier(key) != 0)) return null;
        reason = "modifier_owned_elsewhere";
        foreach (var held in _presses.Values)
            if (KeyboardShortcutRecognizer.Modifier(held.Key.VirtualKey) != 0 &&
                held.Route.Owner is not (KeyboardEventOwner.ShortcutCandidate or KeyboardEventOwner.Shortcut or KeyboardEventOwner.Windows))
                return null;
        return Shortcuts.Match(virtualKey, PressedModifiers, out reason);
    }

    internal void InvalidateWindowShortcutMessages(uint time) => _windowShortcutBarrierTime = time;

    private static int KeyMessageIndex(MappedKey key) => key.VirtualKey * 2 + (key.Extended ? 1 : 0);

    private void ObserveKeyMessageTime(MappedKey key, uint time, bool down)
    {
        if (key.VirtualKey is < 0 or > 255) return;
        var index = KeyMessageIndex(key);
        var previous = _lastKeyMessageTimes[index];
        if (previous is null || unchecked((int)(time - previous.Value)) >= 0)
        {
            _lastKeyMessageTimes[index] = time;
            _lastKeyMessageDown[index] = down;
        }
    }

    // An original window message can survive a missed hook callback. Recover
    // only a fresh shortcut; never replay ordinary input or a held modifier.
    internal bool RecoverShortcutPressFromWindow(MappedKey key, uint messageTime, uint now,
        bool repeat, Action<KeyboardRouteDecision>? diagnose = null)
    {
        bool Ignored(string reason)
        {
            diagnose?.Invoke(new(reason));
            return false;
        }
        var age = unchecked((int)(now - messageTime));
        if (key.Validate() is not null || key.IsModifier || key.IsWindows) return false;
        if (age < 0) return Ignored("window_press_future");
        if (age > 1000) return Ignored("window_press_stale");
        if (HasPressedKey(key)) return Ignored("window_press_already_owned");
        if (Mode == KeyboardInputMode.None && RequestedMode != KeyboardInputMode.None)
            return Ignored("window_press_handoff");
        if (_windowShortcutBarrierTime is { } barrier && unchecked((int)(messageTime - barrier)) < 0)
            return Ignored("window_press_previous_context");
        if (_lastKeyMessageTimes[KeyMessageIndex(key)] is { } previous && unchecked((int)(messageTime - previous)) <= 0)
            return Ignored("window_press_already_observed");
        // Windows can leave bit 30 set after a hook suppressed the previous
        // key-up. A witnessed release in this same context, followed by a
        // strictly newer message, proves that this is a new lifetime. Without
        // that evidence, an unknown held key stays quarantined until its up.
        if (repeat && (_lastKeyMessageDown[KeyMessageIndex(key)] != false ||
            (_windowShortcutBarrierTime is { } context &&
             unchecked((int)(_lastKeyMessageTimes[KeyMessageIndex(key)]!.Value - context)) < 0)))
        {
            ObserveKeyMessageTime(key, messageTime, true);
            return Ignored("window_press_repeat");
        }
        ObserveKeyMessageTime(key, messageTime, true);
        var match = MatchOwnedShortcut(key.VirtualKey, out _);
        if (match is null) return false;
        var press = new Press(key, (_, _) => new(KeyboardEventOwner.Windows, false), (_, _) => { })
        {
            LastDownMessageTime = messageTime,
            WasShortcut = true,
            WindowDownObserved = true,
            Route = new(KeyboardEventOwner.Shortcut, true),
        };
        _presses.Add(PhysicalKey(key), press);
        foreach (var held in _presses.Values)
            if (held.Route.Owner == KeyboardEventOwner.ShortcutCandidate)
                held.Route = new(KeyboardEventOwner.Shortcut, true);
        diagnose?.Invoke(new("shortcut_press_recovered", KeyboardEventOwner.Shortcut, match.Name));
        match.Execute();
        return true;
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
    // Called outside the hook callback, once Windows has updated async key
    // state. Drop lost ups without replaying buffered shortcut candidates or
    // retiring unrelated keys that are still physically held.
    internal int ReconcileReleasedKeys(IReadOnlySet<int> physicallyHeld)
    {
        // Suppressed downs never update Windows async state. A false sample
        // cannot prove those keys were released; keep their real up as owner.
        var protectedKeys = _presses.Values.Where(press => press.Route.Suppress)
            .Select(press => press.Key.VirtualKey).ToHashSet();
        var released = _presses.Where(pair => !pair.Value.Route.Suppress &&
            !physicallyHeld.Contains(pair.Value.Key.VirtualKey)).ToArray();
        var dispatches = released.Select(pair => pair.Value.Route.Dispatch).ToArray();
        foreach (var (id, press) in released)
        {
            _presses.Remove(id);
            press.Route = new(KeyboardEventOwner.Retired, press.Route.Suppress);
        }
        var retired = _retired.RemoveWhere(key => !physicallyHeld.Contains(key) && !protectedKeys.Contains(key));
        foreach (var dispatch in dispatches)
        {
            try { dispatch?.Invoke(false); }
            catch (Exception error)
            {
                DiagnosticLogger.ReverseControlWarning("keyboard_input", "key_release_failed", ("error", error.Message));
            }
        }
        return released.Length + retired;
    }

    // Only after BeginHandoff has released every old owner. Do not dispatch
    // synthetic key-ups here: that could execute a buffered shortcut candidate.
    internal void ReconcileRetiredKeys(IReadOnlySet<int> physicallyHeld)
    {
        foreach (var (id, press) in _presses.ToArray())
            if (press.Route.Owner == KeyboardEventOwner.Retired && !physicallyHeld.Contains(press.Key.VirtualKey))
                _presses.Remove(id);
        _retired.RemoveWhere(key => !physicallyHeld.Contains(key));
        _retired.UnionWith(physicallyHeld);
    }

    internal void ReleaseCapturedKey(MappedKey key)
    {
        _retired.Remove(key.VirtualKey);
        foreach (var (id, press) in _presses.ToArray())
            if (press.Route.Owner == KeyboardEventOwner.Retired && press.Key.SamePhysicalKey(key))
                _presses.Remove(id);
    }

    // A hook can miss up while the foreground window still receives it.
    // Only forget a completed shortcut lifetime: never synthesize a direct
    // key release, execute a buffered modifier, or reuse a delayed old up.
    internal bool ReleaseShortcutFromWindow(MappedKey key, uint messageTime)
    {
        ObserveKeyMessageTime(key, messageTime, false);
        foreach (var (id, press) in _presses)
        {
            if (!press.Key.SamePhysicalKey(key) || !press.WasShortcut ||
                press.LastDownMessageTime is not { } downTime ||
                unchecked((int)(messageTime - downTime)) <= 0) continue;
            _presses.Remove(id);
            _retired.Remove(key.VirtualKey);
            return true;
        }
        return false;
    }

    [Conditional("DEBUG")]
    private void TraceEvent(string name, Press press)
    {
        if (TraceEnabled)
            Debug.WriteLine($"[KeyboardRouter] {name} key={press.Key.VirtualKey:X2} scan={press.Key.ScanCode:X2} owner={press.Route.Owner}");
    }
}
