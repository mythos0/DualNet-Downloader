using DualNet.Core.Models;

namespace DualNet.Core.Engine;

/// <summary>
/// Splits a file into byte ranges so that each network adapter receives an interleaved
/// subset of segments, letting Ethernet + Wi-Fi transfer simultaneously.
/// </summary>
public static class SegmentPlanner
{
    /// <summary>
    /// Plans segments for a download.
    /// </summary>
    /// <param name="totalSize">File size in bytes (-1 when the server did not report a length).</param>
    /// <param name="rangeSupported">Whether the server honors HTTP Range requests.</param>
    /// <param name="options">Tuning options.</param>
    public static List<SegmentRange> Plan(long totalSize, bool rangeSupported, DownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!rangeSupported || totalSize <= 0)
            return new List<SegmentRange> { new(0, -1) };   // single-stream fallback

        int desired = totalSize >= options.LargeFileThresholdBytes
            ? options.SegmentsLarge
            : options.SegmentsSmall;

        desired = Math.Clamp(desired, 1, Math.Max(1, options.MaxSegmentsCap));

        // Respect the minimum per-segment size.
        long minLimited = Math.Max(1, totalSize / options.MinSegmentSizeBytes);
        desired = (int)Math.Min(desired, minLimited);
        desired = Math.Max(1, desired);

        // Even split; distribute the remainder bytes across the first segments.
        long baseSize = totalSize / desired;
        long remainder = totalSize % desired;

        var segments = new List<SegmentRange>(desired);
        long cursor = 0;
        for (int i = 0; i < desired; i++)
        {
            long len = baseSize + (i < remainder ? 1 : 0);
            long end = cursor + len - 1;
            segments.Add(new SegmentRange(cursor, end));
            cursor = end + 1;
        }

        return segments;
    }

    /// <summary>Assigns each segment index to an adapter slot, interleaved so every
    /// bound adapter carries traffic concurrently (segment i -> adapter i % n).</summary>
    public static int AssignAdapter(int segmentIndex, int adapterCount) =>
        adapterCount <= 0 ? 0 : segmentIndex % adapterCount;
}
