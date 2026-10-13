namespace DualNet.Core.Models;

/// <summary>Lifecycle state of a download.</summary>
public enum DownloadStatus
{
    Queued,
    Preparing,
    Downloading,
    Pausing,
    Paused,
    Completed,
    Failed,
    Canceled
}

/// <summary>A byte range [Start, End] (inclusive). End = -1 means open-ended (unknown length).</summary>
public sealed record SegmentRange(long Start, long End);

/// <summary>Mutable per-segment progress, persisted for pause/resume.</summary>
public sealed class SegmentState
{
    public long Start { get; set; }
    public long End { get; set; }          // inclusive; -1 = open ended
    public long Written { get; set; }      // bytes already written to disk

    public long Length => End < 0 ? -1 : End - Start + 1;
    public long Remaining => End < 0 ? -1 : Math.Max(0, End - Start + 1 - Written);
    public bool IsComplete => End >= 0 && Written >= End - Start + 1;
}

/// <summary>Download tuning options.</summary>
public sealed class DownloadOptions
{
    /// <summary>Segment count for files below LargeFileThresholdBytes (user preference: 8).</summary>
    public int SegmentsSmall { get; set; } = 8;

    /// <summary>Segment count for files at/above LargeFileThresholdBytes.</summary>
    public int SegmentsLarge { get; set; } = 16;

    /// <summary>Files at or above this size use SegmentsLarge.</summary>
    public long LargeFileThresholdBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>Never create segments smaller than this (2 MB).</summary>
    public long MinSegmentSizeBytes { get; set; } = 2L * 1024 * 1024;

    /// <summary>Consecutive retry attempts per segment before failing.</summary>
    public int RetryCount { get; set; } = 5;

    /// <summary>If no bytes arrive within this window, the attempt is aborted and retried.</summary>
    public int StallTimeoutMs { get; set; } = 90_000;

    /// <summary>Copy buffer size per stream.</summary>
    public int BufferSizeBytes { get; set; } = 256 * 1024;

    /// <summary>Upper cap for auto-planned segments.</summary>
    public int MaxSegmentsCap { get; set; } = 32;

    public static DownloadOptions Default => new();
}

/// <summary>Which network adapters a download is bound to.</summary>
public sealed record DownloadScope(string Label, IReadOnlyList<string> AdapterIds)
{
    public static DownloadScope All(IEnumerable<string> adapterIds) =>
        new("All", adapterIds.ToArray());
}

/// <summary>A detected active network adapter with an IPv4 address we can bind to.</summary>
public sealed record NetworkAdapterInfo(
    string Id,
    string Name,
    string Description,
    string TypeName,          // "Ethernet", "Wi-Fi", "Other"
    string IPv4Address,
    ulong SpeedBitsPerSecond,
    bool IsManual = false,    // user-defined "network provider" (auto-scan missed it)
    bool IsOnline = true)     // false = manual provider whose IP is not present on this PC
{
    public string SpeedText =>
        SpeedBitsPerSecond >= 1_000_000_000
            ? $"{SpeedBitsPerSecond / 1_000_000_000.0:0.#} Gbps"
            : $"{SpeedBitsPerSecond / 1_000_000.0:0} Mbps";
}

/// <summary>Serialized download state used for pause/resume and app-restart recovery.</summary>
public sealed class PersistedDownloadState
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long TotalSize { get; set; } = -1;
    public bool RangeSupported { get; set; }
    public string? ETag { get; set; }
    public string? LastModifiedUtc { get; set; }
    public string ScopeLabel { get; set; } = "All";
    public List<string> AdapterIds { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<SegmentState> Segments { get; set; } = new();
}

/// <summary>
/// A user-defined network provider, for adapters the automatic scan does not offer
/// (filtered virtual adapters, VPN tunnels, multi-IP NICs, …). Identified by its IPv4
/// address — the engine binds sockets to that address.
/// </summary>
public sealed class ManualAdapterEntry
{
    public string Name { get; set; } = "";
    public string IPv4Address { get; set; } = "";
    public string TypeName { get; set; } = "Other";   // Ethernet / Wi-Fi / Other
}

/// <summary>One entry in the download history.</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Status { get; set; } = "Completed";
    public string NetworkScope { get; set; } = "All";
    public string? Message { get; set; }
    public DateTime FinishedUtc { get; set; } = DateTime.UtcNow;
}
