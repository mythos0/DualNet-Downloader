using DualNet.Core.Engine;
using DualNet.Core.Models;
using Xunit;

namespace DualNet.Tests;

/// <summary>Pure decision-logic tests for dynamic (IDM-style) segment scaling.</summary>
public class AdaptiveScalePolicyTests
{
    private static PendingSegmentInfo Seg(object handle, long remaining, int slot) =>
        new(handle, remaining, slot);

    [Fact]
    public void IdleAdapter_GetsWorkStolen_FromBusyAdapter()
    {
        // Slot 1 finished everything; slot 0 still crawls with two big segments.
        var s1 = new object(); var s2 = new object();
        var pending = new List<PendingSegmentInfo>
        {
            Seg(s1, 8_000_000, slot: 0),
            Seg(s2, 5_000_000, slot: 0),
        };
        var active = new[] { 2, 0 };   // two workers on slot 0, none on slot 1

        var splits = AdaptiveScalePolicy.PlanSplits(
            pending, adapterCount: 2, active, currentSegmentCount: 2,
            maxSegments: 32, minSegmentBytes: 2_000_000, throughputPlateau: false);

        Assert.NotEmpty(splits);
        Assert.All(splits, s => Assert.Equal(1, s.TargetSlot));       // work moves to the idle NIC
        Assert.Contains(splits, s => ReferenceEquals(s.Handle, s1));  // largest segment is split first
    }

    [Fact]
    public void NoIdleAdapters_NoPlateau_NoSplits()
    {
        var s1 = new object();
        var pending = new List<PendingSegmentInfo> { Seg(s1, 8_000_000, 0) };
        var active = new[] { 1, 1 };

        var splits = AdaptiveScalePolicy.PlanSplits(
            pending, 2, active, 1, 32, 2_000_000, throughputPlateau: false);

        Assert.Empty(splits);
    }

    [Fact]
    public void Plateau_SplitsLargestSegments_TowardLeastBusySlots()
    {
        var s1 = new object(); var s2 = new object(); var s3 = new object();
        var pending = new List<PendingSegmentInfo>
        {
            Seg(s1, 9_000_000, 0),
            Seg(s2, 7_000_000, 1),
            Seg(s3, 4_000_000, 0),
        };
        var active = new[] { 3, 1 };   // slot 0 busier than slot 1

        var splits = AdaptiveScalePolicy.PlanSplits(
            pending, 2, active, 3, 32, 1_000_000, throughputPlateau: true);

        Assert.Equal(2, splits.Count);
        Assert.Contains(splits, s => ReferenceEquals(s.Handle, s1));   // largest first
        Assert.Contains(splits, s => ReferenceEquals(s.Handle, s2));   // second largest next
        Assert.Equal(1, splits[0].TargetSlot);                          // least-busy slot preferred
        Assert.Equal(0, splits[1].TargetSlot);
    }

    [Fact]
    public void Respects_MaxSegments_Cap()
    {
        var s1 = new object();
        var pending = new List<PendingSegmentInfo> { Seg(s1, 8_000_000, 0) };
        var active = new[] { 1, 0 };

        var splits = AdaptiveScalePolicy.PlanSplits(
            pending, 2, active, currentSegmentCount: 32, maxSegments: 32,
            minSegmentBytes: 1_000_000, throughputPlateau: true);

        Assert.Empty(splits);
    }

    [Fact]
    public void Respects_MinSegment_Size()
    {
        var s1 = new object();
        // Remaining 3 MB with a 2 MB floor → needs 4 MB to split → refuses.
        var pending = new List<PendingSegmentInfo> { Seg(s1, 3_000_000, 0) };
        var active = new[] { 1, 0 };

        var splits = AdaptiveScalePolicy.PlanSplits(
            pending, 2, active, 1, 32, 2_000_000, throughputPlateau: true);

        Assert.Empty(splits);
    }

    [Fact]
    public void Never_Splits_More_Than_Budget()
    {
        var s1 = new object(); var s2 = new object();
        var pending = new List<PendingSegmentInfo>
        {
            Seg(s1, 9_000_000, 0),
            Seg(s2, 8_000_000, 0),
        };
        var active = new[] { 2, 0 };

        // One idle slot + cap allows only one more segment → exactly one split.
        var splits = AdaptiveScalePolicy.PlanSplits(
            pending, 2, active, currentSegmentCount: 31, maxSegments: 32,
            minSegmentBytes: 1_000_000, throughputPlateau: true);

        Assert.Single(splits);
    }

    [Fact]
    public void SegmentPlanner_Assigns_Interleaved_Slots()
    {
        Assert.Equal(0, SegmentPlanner.AssignAdapter(0, 2));
        Assert.Equal(1, SegmentPlanner.AssignAdapter(1, 2));
        Assert.Equal(0, SegmentPlanner.AssignAdapter(2, 2));
        Assert.Equal(0, SegmentPlanner.AssignAdapter(4, 1));
        Assert.Equal(0, SegmentPlanner.AssignAdapter(0, 0));   // degenerate — clamps to 0
    }
}
