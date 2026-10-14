namespace DualNet.Core.Engine;

/// <summary>Snapshot of one unfinished segment used for scaling decisions.</summary>
public readonly record struct PendingSegmentInfo(object Handle, long RemainingBytes, int Slot);

/// <summary>
/// Pure decision logic for dynamic (IDM-style) segment scaling.
///
/// A static segment plan (8/16 parts) often cannot saturate a fast combined link:
/// servers commonly cap throughput per connection, so the only way to use the full
/// bandwidth of Ethernet + Wi-Fi together is to keep adding connections while the
/// throughput keeps improving — and to hand work to an adapter that ran out of
/// segments while slower segments are still crawling on the other one.
///
/// The policy answers two questions:
///   1. STEAL — an adapter slot has no active worker and no pending segment
///      (the fast NIC finished its share): split the largest remaining segment
///      so the idle NIC gets fresh work immediately.
///   2. PLATEAU — aggregate throughput stopped growing while segments remain
///      (per-connection server cap): split the largest remaining segments to add
///      connections, preferring the least-busy adapter slots.
///
/// Splitting is only allowed while the resulting pieces stay >= 2 x minSegmentBytes
/// and the total connection count stays under maxSegments.
/// </summary>
public static class AdaptiveScalePolicy
{
    public readonly record struct Split(object Handle, int TargetSlot);

    /// <param name="pending">Unfinished segments (IsComplete == false).</param>
    /// <param name="adapterCount">Number of bound adapters (client slots).</param>
    /// <param name="activeWorkersPerSlot">In-flight workers per adapter slot.</param>
    /// <param name="currentSegmentCount">Total segments currently planned.</param>
    /// <param name="maxSegments">Hard cap on total segments.</param>
    /// <param name="minSegmentBytes">Smallest permissible piece size.</param>
    /// <param name="throughputPlateau">True when speed stopped improving over recent samples.</param>
    /// <param name="maxPlateauSplits">Upper bound on splits issued in one plateau/ramp round.</param>
    public static IReadOnlyList<Split> PlanSplits(
        IReadOnlyList<PendingSegmentInfo> pending,
        int adapterCount,
        IReadOnlyList<int> activeWorkersPerSlot,
        int currentSegmentCount,
        int maxSegments,
        long minSegmentBytes,
        bool throughputPlateau,
        int maxPlateauSplits = 2)
    {
        var splits = new List<Split>();
        if (adapterCount <= 0 || pending.Count == 0 || currentSegmentCount >= maxSegments)
            return splits;

        int budget = maxSegments - currentSegmentCount;

        // Largest remaining segments that are still splittable.
        var candidates = pending
            .Where(s => s.RemainingBytes >= 2 * minSegmentBytes)
            .OrderByDescending(s => s.RemainingBytes)
            .ToList();

        // 1) Idle adapter slots — nothing in flight, nothing queued for them.
        var pendingSlots = new HashSet<int>(pending.Select(s => s.Slot));
        var idleSlots = new List<int>();
        for (int slot = 0; slot < adapterCount; slot++)
        {
            if (slot < activeWorkersPerSlot.Count &&
                activeWorkersPerSlot[slot] <= 0 &&
                !pendingSlots.Contains(slot))
                idleSlots.Add(slot);
        }

        foreach (var slot in idleSlots)
        {
            if (budget <= 0 || candidates.Count == 0)
                break;
            // Never split a segment that lives on the idle slot itself (it has none),
            // and never split one already chosen in this pass.
            var source = candidates.FirstOrDefault(c => c.Slot != slot);
            if (source.Handle is null)
                break;
            candidates.Remove(source);
            splits.Add(new Split(source.Handle, slot));
            budget--;
        }

        // 2) Throughput plateau — add connections to keep pulling more bandwidth.
        if (splits.Count == 0 && throughputPlateau)
        {
            // Prefer slots with the fewest in-flight workers so connections spread evenly.
            var slotOrder = Enumerable.Range(0, Math.Max(1, adapterCount))
                .OrderBy(s => s < activeWorkersPerSlot.Count ? activeWorkersPerSlot[s] : 0)
                .ThenBy(s => s)
                .ToList();

            int cursor = 0;
            while (budget > 0 && candidates.Count > 0 && splits.Count < maxPlateauSplits)
            {
                var source = candidates[0];
                candidates.RemoveAt(0);
                var slot = slotOrder[cursor % slotOrder.Count];
                cursor++;
                splits.Add(new Split(source.Handle, slot));
                budget--;
            }
        }

        return splits;
    }
}

/// <summary>What the scaler wants the engine to do on this tick.</summary>
public enum ScaleImpulse
{
    /// <summary>Nothing — keep sampling.</summary>
    None,
    /// <summary>Add connections (plateau detected or the previous round gained speed).</summary>
    AddConnections,
    /// <summary>Last stand-down was premature / server may have freed up — try a small probe.</summary>
    ReProbe
}

/// <summary>A scaler decision for one tick: the impulse and how many splits it wants.</summary>
public readonly record struct ScaleDecision(ScaleImpulse Impulse, int Splits)
{
    public static ScaleDecision None => new(ScaleImpulse.None, 0);
}

/// <summary>
/// State machine that drives WHEN the engine adds connections.
///
/// v2 design goals (v1 failed real-world downloads by declaring "exhausted" after a single
/// 2-second evaluation — new TCP connections had not even finished slow-start at that point,
/// so scaling stood down forever and combined downloads never left the initial ~100 Mbps):
///
///  - every split round is evaluated over a multi-tick window (≈6 s) of EMA-smoothed speed,
///    giving fresh connections time to ramp before the verdict;
///  - standing down requires TWO consecutive failed rounds, not one bad window;
///  - a stood-down scaler re-probes periodically — servers free up, per-connection caps
///    change, and a paused scaler must notice on its own;
///  - successful rounds escalate (more splits per round) while they keep buying bandwidth.
/// </summary>
public sealed class ScaleController
{
    /// <summary>Ticks (≈2 s each) to observe after a split round before judging it.</summary>
    public const int EvalTicks = 3;

    /// <summary>Average gain (vs. round start) that counts as "connections are still buying bandwidth".</summary>
    public const double RampGainFactor = 1.04;

    /// <summary>Avg gain at or above which the next round escalates.</summary>
    public const double AggressiveGainFactor = 1.25;

    /// <summary>Failed evaluations in a row before the scaler stands down.</summary>
    public const int MaxFailedRounds = 2;

    /// <summary>Cooldown ticks (≈30 s) after standing down before the next re-probe.</summary>
    public const int ReProbeCooldownTicks = 15;

    public const int RampSplitsPerRound = 4;
    public const int AggressiveSplitsPerRound = 6;
    public const int ProbeSplitsPerRound = 2;

    /// <summary>Growth across the plateau window below this factor counts as a plateau.</summary>
    public const double PlateauGrowthFactor = 1.10;

    public const int PlateauWindowTicks = 4;
    public const double EmaAlpha = 0.45;

    private double? _ema;
    private readonly Queue<double> _history = new();
    private int _evalTicksLeft;
    private readonly List<double> _evalSamples = new();
    private double _speedAtRoundStart = 1;
    private int _failedRounds;
    private int _reprobeCountdown;

    /// <summary>True after repeated failed rounds; the controller still re-probes on cooldown.</summary>
    public bool Exhausted { get; private set; }

    /// <summary>Ticks left before the pending split round is judged (0 when none pending).</summary>
    public int PendingEvalTicks => _evalTicksLeft;

    /// <summary>
    /// Feed one aggregate-speed sample (bytes/s). Returns what the engine should do.
    /// The caller must confirm via <see cref="ConfirmRound"/> when it actually applied
    /// splits, or <see cref="CancelRound"/> when it could not (cap/size limits).
    /// </summary>
    public ScaleDecision OnTick(double speedBps)
    {
        // EMA-smooth the raw 400 ms speed samples so single noisy ticks never flip a verdict.
        _ema = _ema is { } prev ? prev + EmaAlpha * (speedBps - prev) : speedBps;
        _history.Enqueue(_ema.Value);
        while (_history.Count > PlateauWindowTicks)
            _history.Dequeue();

        if (Exhausted)
        {
            if (_reprobeCountdown > 0)
                _reprobeCountdown--;
            if (_reprobeCountdown == 0)
            {
                Exhausted = false;
                _failedRounds = 0;
                return new ScaleDecision(ScaleImpulse.ReProbe, ProbeSplitsPerRound);
            }
            return ScaleDecision.None;
        }

        if (_evalTicksLeft > 0)
        {
            _evalSamples.Add(_ema.Value);
            if (--_evalTicksLeft > 0)
                return ScaleDecision.None;

            double avg = _evalSamples.Average();
            _evalSamples.Clear();
            double baseline = Math.Max(1, _speedAtRoundStart);

            if (avg >= baseline * RampGainFactor)
            {
                // Connections are still buying bandwidth — escalate while it keeps working.
                _failedRounds = 0;
                int splits = avg >= baseline * AggressiveGainFactor
                    ? AggressiveSplitsPerRound
                    : RampSplitsPerRound;
                return new ScaleDecision(ScaleImpulse.AddConnections, splits);
            }

            _failedRounds++;
            if (_failedRounds >= MaxFailedRounds)
            {
                Exhausted = true;
                _reprobeCountdown = ReProbeCooldownTicks;
                return ScaleDecision.None;
            }
            return new ScaleDecision(ScaleImpulse.ReProbe, ProbeSplitsPerRound);
        }

        // No round pending — watch for a throughput plateau (speed flat over the window
        // while work remains). Early download ramp shows strong growth, so this only
        // fires once the curve has genuinely flattened.
        if (_history.Count == PlateauWindowTicks && _ema.Value > 0 &&
            _ema.Value < _history.Peek() * PlateauGrowthFactor)
        {
            return new ScaleDecision(ScaleImpulse.AddConnections, ProbeSplitsPerRound);
        }

        return ScaleDecision.None;
    }

    /// <summary>Call after applying <paramref name="splitsApplied"/> splits; starts the evaluation window.</summary>
    public void ConfirmRound(int splitsApplied, double speedAtApply)
    {
        if (splitsApplied <= 0)
        {
            CancelRound();
            return;
        }
        _speedAtRoundStart = speedAtApply > 0 ? speedAtApply : 1;
        _evalSamples.Clear();
        _evalTicksLeft = EvalTicks;
    }

    /// <summary>Call when a requested round could not be applied (cap reached, segments too small).</summary>
    public void CancelRound()
    {
        _evalSamples.Clear();
        _evalTicksLeft = 0;
    }
}
