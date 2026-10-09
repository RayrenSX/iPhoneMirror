using System.Diagnostics;
using System.Security.Cryptography;

namespace IPhoneMirror.App.Services.Automation;

// One registry per App, keyed by existing binding profile / physical identity.
// API leases are input ownership only: they never create transport sessions.
internal sealed class AutomationOwnership
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _epochs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<Lease?> _caller = new();
    internal IDisposable Use(Lease lease)
    {
        var previous = _caller.Value;
        _caller.Value = lease;
        return new Exit(() => _caller.Value = previous);
    }
    internal IDisposable EnterInput(string identity)
    {
        lock (_sync)
        {
            var owner = _leases.GetValueOrDefault(identity);
            if (owner != _caller.Value || (owner is not null && owner.PhysicalId != identity))
                throw new OperationCanceledException("Input ownership changed.");
            _inFlight[identity] = _inFlight.GetValueOrDefault(identity) + 1;
        }
        return new Exit(() => { lock (_sync) _inFlight[identity]--; });
    }
    private sealed class Exit(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    internal sealed class Lease(string physicalId, string principal, string token, AutomationTarget target)
    {
        internal string PhysicalId { get; } = physicalId;
        internal string Principal { get; } = principal;
        internal string Token { get; } = token;
        internal AutomationTarget Target { get; } = target;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal SemaphoreSlim Operation { get; } = new(1, 1);
        internal SemaphoreSlim ReleaseGate { get; } = new(1, 1);
        internal long Deadline;
        internal DateTimeOffset ExpiresAt;
        internal bool Releasing;
        internal MappedTouchRoute? Touch;
        internal string? TouchId;
        internal (double X, double Y) Position;
        internal long TouchDeadline;
    }

    internal bool HumanAllowed(string identity)
    { lock (_sync) return !_leases.ContainsKey(identity); }
    internal Func<bool> CaptureHumanGuard(string identity)
    {
        long epoch;
        lock (_sync) epoch = _epochs.GetValueOrDefault(identity);
        return () => { lock (_sync) return !_leases.ContainsKey(identity) && _epochs.GetValueOrDefault(identity) == epoch; };
    }
    internal Lease Acquire(AutomationTarget target, string principal, string? token, int seconds)
    {
        lock (_sync)
        {
            if (!target.IsCurrent())
                throw new AutomationException("CONTROL_NOT_AVAILABLE", "The control route changed; refresh device status.");
            if (_leases.TryGetValue(target.PhysicalId, out var existing))
            {
                if (existing.Principal != principal || existing.Token != token || existing.Releasing ||
                    Stopwatch.GetTimestamp() >= existing.Deadline || !existing.Target.IsCurrent())
                    throw new AutomationException("CONTROL_LOCKED", "The device is owned by another control session.");
                Renew(existing, seconds);
                return existing;
            }
            if (_inFlight.GetValueOrDefault(target.PhysicalId) != 0 || target.HardwareBusy?.Invoke() == true)
                throw new AutomationException("CONTROL_LOCKED", "An existing input write has not completed.");
            var lease = new Lease(target.PhysicalId, principal,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), target);
            Renew(lease, seconds);
            _epochs[target.PhysicalId] = _epochs.GetValueOrDefault(target.PhysicalId) + 1;
            _leases.Add(target.PhysicalId, lease);
            return lease;
        }
    }
    private static void Renew(Lease lease, int seconds)
    {
        lease.Deadline = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        lease.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }
    internal Lease Require(string identity, string principal, string? token)
    {
        lock (_sync)
        {
            if (!_leases.TryGetValue(identity, out var lease) || lease.Principal != principal || lease.Token != token ||
                !IsCurrentUnsafe(lease))
                throw new AutomationException("CONTROL_LOCKED", "Acquire a current control session before sending input.");
            return lease;
        }
    }
    internal bool IsCurrent(Lease lease) { lock (_sync) return IsCurrentUnsafe(lease); }
    private bool IsCurrentUnsafe(Lease lease) => _leases.GetValueOrDefault(lease.PhysicalId) == lease &&
        !lease.Releasing && Stopwatch.GetTimestamp() < lease.Deadline && lease.Target.IsCurrent();
    internal Lease[] Snapshot() { lock (_sync) return _leases.Values.ToArray(); }
    internal bool BeginRelease(Lease lease)
    {
        lock (_sync)
        {
            if (lease.Releasing || _leases.GetValueOrDefault(lease.PhysicalId) != lease) return false;
            lease.Releasing = true;
        }
        lease.Cancellation.Cancel();
        return true;
    }
    internal void FinishRelease(Lease lease)
    {
        lock (_sync)
        {
            if (_leases.GetValueOrDefault(lease.PhysicalId) == lease)
            {
                _leases.Remove(lease.PhysicalId);
                _epochs[lease.PhysicalId] = _epochs.GetValueOrDefault(lease.PhysicalId) + 1;
            }
        }
    }
}
