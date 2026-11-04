// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;

namespace Omabot.Gateway;

/// <summary>Thrown when a background request is dropped because the queue is longer than S.</summary>
public sealed class QueueDroppedException(string message) : Exception(message);

/// <summary>A held lane. Dispose it to free the lane; disposing twice is safe.</summary>
public sealed class GatewayLease : IDisposable
{
    private readonly Action<GatewayLease> _release;
    private int _released;

    internal GatewayLease(string bot, Priority priority, int lane, TimeSpan waited, Action<GatewayLease> release)
    {
        Bot = bot;
        Priority = priority;
        Lane = lane;
        Waited = waited;
        _release = release;
    }

    public string Bot { get; }
    public Priority Priority { get; }
    /// <summary>Lane index 0..S-1. Sent as id_slot to llama-server when slot affinity is on.</summary>
    public int Lane { get; }
    public TimeSpan Waited { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _release(this);
    }
}

/// <summary>Snapshot for `oma-bot gateway status` and the health bot.</summary>
public sealed record LaneSnapshot(int Slots, int InFlight, int Waiting, IReadOnlyDictionary<string, int> WaitingByPriority, IReadOnlyDictionary<string, int> InFlightByBot, double OldestWaitSeconds);

/// <summary>
/// The gateway queue for one backend (plan 7.9). Holds at most S requests in
/// flight, at most perBotCap per bot, serves the highest priority first and
/// round robin by bot within a priority. Background requests are dropped when
/// the queue would grow longer than S. Each bot keeps its last lane when it is
/// free (sticky slots).
/// </summary>
public sealed class LaneQueue
{
    private sealed class Waiter
    {
        public required string Bot { get; init; }
        public required Priority Priority { get; init; }
        public required DateTimeOffset Since { get; init; }
        public TaskCompletionSource<GatewayLease> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Reg { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly IClock _clock;
    private readonly int _perBotCap;
    private readonly LinkedList<string>[] _order = [.. Enumerable.Range(0, Priorities.Count).Select(_ => new LinkedList<string>())];
    private readonly Dictionary<string, Queue<Waiter>>[] _byBot = [.. Enumerable.Range(0, Priorities.Count).Select(_ => new Dictionary<string, Queue<Waiter>>(StringComparer.Ordinal))];
    private readonly Dictionary<string, int> _botInFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastLane = new(StringComparer.Ordinal);
    private bool[] _laneBusy;
    private int _inFlight;
    private int _waiting;

    public LaneQueue(int slots, int perBotCap, IClock clock)
    {
        if (slots < 1) throw new ArgumentOutOfRangeException(nameof(slots), "At least one lane.");
        if (perBotCap < 1) throw new ArgumentOutOfRangeException(nameof(perBotCap));
        _laneBusy = new bool[slots];
        _perBotCap = perBotCap;
        _clock = clock;
    }

    public int Slots { get { lock (_lock) return EffectiveSlots; } }
    public int InFlight { get { lock (_lock) return _inFlight; } }
    public int Waiting { get { lock (_lock) return _waiting; } }
    /// <summary>Highest in-flight count ever seen. Tests use it to prove the server never saw more than S.</summary>
    public int PeakInFlight { get; private set; }

    /// <summary>Change S after discovery or a measured override. Lanes in use finish normally.</summary>
    public void SetSlots(int slots)
    {
        if (slots < 1) throw new ArgumentOutOfRangeException(nameof(slots));
        List<(Waiter, GatewayLease)> grants;
        lock (_lock)
        {
            if (slots >= _laneBusy.Length)
            {
                var grown = new bool[slots];
                Array.Copy(_laneBusy, grown, _laneBusy.Length);
                _laneBusy = grown;
                _targetSlots = null;
            }
            else if (!_laneBusy.Skip(slots).Any(b => b))
            {
                _laneBusy = _laneBusy.Take(slots).ToArray();
                _targetSlots = null;
            }
            else
            {
                // Busy lanes above the new count finish first; Release trims them.
                _targetSlots = slots;
            }
            grants = PumpLocked();
        }
        Complete(grants);
    }

    private int? _targetSlots;
    private int EffectiveSlots => _targetSlots ?? _laneBusy.Length;

    public Task<GatewayLease> EnterAsync(string bot, Priority priority, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        List<(Waiter, GatewayLease)> grants;
        Waiter w;
        lock (_lock)
        {
            if (priority == Priority.Background && _waiting + 1 > EffectiveSlots && !CanGrantNow(bot))
                throw new QueueDroppedException($"Background request from {bot} dropped: {_waiting} waiting, {EffectiveSlots} lanes.");
            w = new Waiter { Bot = bot, Priority = priority, Since = _clock.UtcNow };
            var p = (int)priority;
            if (!_byBot[p].TryGetValue(bot, out var q))
            {
                q = new Queue<Waiter>();
                _byBot[p][bot] = q;
                _order[p].AddLast(bot);
            }
            q.Enqueue(w);
            _waiting++;
            grants = PumpLocked();
        }
        if (!w.Tcs.Task.IsCompleted && ct.CanBeCanceled)
            w.Reg = ct.Register(() => Cancel(w, ct));
        Complete(grants);
        return w.Tcs.Task;
    }

    private bool CanGrantNow(string bot)
        => _waiting == 0 && _inFlight < EffectiveSlots && _botInFlight.GetValueOrDefault(bot) < _perBotCap;

    private void Cancel(Waiter w, CancellationToken ct)
    {
        lock (_lock)
        {
            var p = (int)w.Priority;
            if (!_byBot[p].TryGetValue(w.Bot, out var q) || !q.Contains(w)) return;
            var rest = new Queue<Waiter>(q.Where(x => !ReferenceEquals(x, w)));
            _waiting--;
            if (rest.Count == 0)
            {
                _byBot[p].Remove(w.Bot);
                _order[p].Remove(w.Bot);
            }
            else _byBot[p][w.Bot] = rest;
        }
        w.Tcs.TrySetCanceled(ct);
    }

    private List<(Waiter, GatewayLease)> PumpLocked()
    {
        var grants = new List<(Waiter, GatewayLease)>();
        while (_inFlight < EffectiveSlots)
        {
            Waiter? chosen = null;
            for (var p = 0; p < Priorities.Count && chosen is null; p++)
            {
                for (var node = _order[p].First; node is not null; node = node.Next)
                {
                    var bot = node.Value;
                    if (_botInFlight.GetValueOrDefault(bot) >= _perBotCap) continue;
                    var q = _byBot[p][bot];
                    chosen = q.Dequeue();
                    _order[p].Remove(node);
                    if (q.Count == 0) _byBot[p].Remove(bot);
                    else _order[p].AddLast(bot); // round robin: this bot goes to the back
                    break;
                }
            }
            if (chosen is null) break;
            _waiting--;
            var lane = PickLane(chosen.Bot);
            _laneBusy[lane] = true;
            _inFlight++;
            if (_inFlight > PeakInFlight) PeakInFlight = _inFlight;
            _botInFlight[chosen.Bot] = _botInFlight.GetValueOrDefault(chosen.Bot) + 1;
            _lastLane[chosen.Bot] = lane;
            grants.Add((chosen, new GatewayLease(chosen.Bot, chosen.Priority, lane, _clock.UtcNow - chosen.Since, Release)));
        }
        return grants;
    }

    private int PickLane(string bot)
    {
        var limit = EffectiveSlots;
        if (_lastLane.TryGetValue(bot, out var last) && last < limit && !_laneBusy[last]) return last;
        for (var i = 0; i < limit; i++) if (!_laneBusy[i]) return i;
        throw new InvalidOperationException("No free lane although in-flight is below S.");
    }

    private void Release(GatewayLease lease)
    {
        List<(Waiter, GatewayLease)> grants;
        lock (_lock)
        {
            _laneBusy[lease.Lane] = false;
            _inFlight--;
            var n = _botInFlight.GetValueOrDefault(lease.Bot) - 1;
            if (n <= 0) _botInFlight.Remove(lease.Bot); else _botInFlight[lease.Bot] = n;
            if (_targetSlots is { } t && t < _laneBusy.Length && !_laneBusy.Skip(t).Any(b => b))
            {
                _laneBusy = _laneBusy.Take(t).ToArray();
                _targetSlots = null;
            }
            grants = PumpLocked();
        }
        Complete(grants);
    }

    private static void Complete(List<(Waiter W, GatewayLease L)> grants)
    {
        foreach (var (w, l) in grants)
        {
            w.Reg.Dispose();
            if (!w.Tcs.TrySetResult(l)) l.Dispose();
        }
    }

    public LaneSnapshot Snapshot()
    {
        lock (_lock)
        {
            var byP = new Dictionary<string, int>(StringComparer.Ordinal);
            var oldest = TimeSpan.Zero;
            for (var p = 0; p < Priorities.Count; p++)
            {
                var n = 0;
                foreach (var q in _byBot[p].Values)
                {
                    n += q.Count;
                    foreach (var w in q) if (_clock.UtcNow - w.Since > oldest) oldest = _clock.UtcNow - w.Since;
                }
                byP[Priorities.Name((Priority)p)] = n;
            }
            return new LaneSnapshot(EffectiveSlots, _inFlight, _waiting, byP, new Dictionary<string, int>(_botInFlight), oldest.TotalSeconds);
        }
    }
}
