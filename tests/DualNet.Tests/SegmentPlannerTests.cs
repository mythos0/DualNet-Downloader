using DualNet.Core.Engine;
using DualNet.Core.Models;
using Xunit;

namespace DualNet.Tests;

public class SegmentPlannerTests
{
    private static DownloadOptions O(int small = 8, int large = 16, long minSeg = 2L * 1024 * 1024) =>
        new() { SegmentsSmall = small, SegmentsLarge = large, MinSegmentSizeBytes = minSeg };

    [Fact]
    public void NoRangeSupport_ProducesSingleOpenSegment()
    {
        var plan = SegmentPlanner.Plan(600 * 1024 * 1024, rangeSupported: false, O());
        var segment = Assert.Single(plan);
        Assert.Equal(0, segment.Start);
        Assert.Equal(-1, segment.End);
    }

    [Fact]
    public void UnknownSize_ProducesSingleOpenSegment()
    {
        var plan = SegmentPlanner.Plan(-1, rangeSupported: true, O());
        var segment = Assert.Single(plan);
        Assert.Equal(-1, segment.End);
    }

    [Fact]
    public void TinyFile_ProducesSingleSegment()
    {
        var plan = SegmentPlanner.Plan(1024, rangeSupported: true, O());
        var segment = Assert.Single(plan);
        Assert.Equal(0, segment.Start);
        Assert.Equal(1023, segment.End);
    }

    [Fact]
    public void FileUnder500MB_UsesEightSegments()
    {
        long size = 100 * 1024 * 1024;
        var plan = SegmentPlanner.Plan(size, true, O());
        Assert.Equal(8, plan.Count);
        AssertCoverage(plan, size);
    }

    [Fact]
    public void FileOver500MB_UsesSixteenSegments()
    {
        long size = 600 * 1024 * 1024;
        var plan = SegmentPlanner.Plan(size, true, O());
        Assert.Equal(16, plan.Count);
        AssertCoverage(plan, size);
    }

    [Fact]
    public void MinSegmentSize_ReducesSegmentCount()
    {
        // 8 MB with a 2 MB floor must not exceed 4 segments.
        var plan = SegmentPlanner.Plan(8 * 1024 * 1024, true, O(minSeg: 2 * 1024 * 1024));
        Assert.Equal(4, plan.Count);
        AssertCoverage(plan, 8 * 1024 * 1024);
    }

    [Fact]
    public void UnevenSplit_DistributesRemainder()
    {
        long size = 10_000_003;
        var plan = SegmentPlanner.Plan(size, true, O(small: 8, minSeg: 1));
        AssertCoverage(plan, size);
    }

    [Fact]
    public void AssignAdapter_InterleavesAcrossAdapters()
    {
        Assert.Equal(0, SegmentPlanner.AssignAdapter(0, 2));
        Assert.Equal(1, SegmentPlanner.AssignAdapter(1, 2));
        Assert.Equal(0, SegmentPlanner.AssignAdapter(2, 2));
        Assert.Equal(1, SegmentPlanner.AssignAdapter(7, 2));
    }

    private static void AssertCoverage(List<SegmentRange> plan, long totalSize)
    {
        long covered = 0;
        for (int i = 0; i < plan.Count; i++)
        {
            Assert.True(plan[i].Start <= plan[i].End, $"segment {i} inverted");
            if (i > 0)
                Assert.Equal(plan[i - 1].End + 1, plan[i].Start);  // contiguous, no gaps/overlaps
            covered += plan[i].End - plan[i].Start + 1;
        }
        Assert.Equal(totalSize, covered);
    }
}
