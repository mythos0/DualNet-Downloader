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
    public static IReadOnlyList<Split> PlanSplits(
        IReadOnlyList<PendingSegmentInfo> pending,
        int adapterCount,
        IReadOnlyList<int> activeWorkersPerSlot,
        int currentSegmentCount,
        int maxSegments,
        long minSegmentBytes,
        bool throughputPlateau)
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
            while (budget > 0 && candidates.Count > 0 && splits.Count < 2)
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
