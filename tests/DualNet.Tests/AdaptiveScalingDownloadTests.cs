using System.Net;
using System.Security.Cryptography;
using DualNet.Core.Engine;
using DualNet.Core.Models;
using Xunit;

namespace DualNet.Tests;

/// <summary>
/// End-to-end tests for dynamic segment scaling: the scaler must add connections while
/// a per-connection server cap limits throughput, and must hand work to an adapter that
/// ran out of segments while the other one is still crawling — with the final file
/// byte-identical to the original in every case.
/// </summary>
public class AdaptiveScalingDownloadTests
{
    private static byte[] RandomData(int size)
    {
        var data = new byte[size];
        new Random(42).NextBytes(data);
        return data;
    }

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dualnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static DownloadOptions ScalingOptions(long minSegment = 256 * 1024, int scaleIntervalMs = 700) => new()
    {
        SegmentsSmall = 8,
        SegmentsLarge = 16,
        MinSegmentSizeBytes = minSegment,
        RetryCount = 4,
        StallTimeoutMs = 30_000,
        BufferSizeBytes = 64 * 1024,
        AdaptiveScaling = true,
        ScaleIntervalMs = scaleIntervalMs,
        MaxSegmentsCap = 32,
    };

    private static void AssertCompleted(DownloadJob job)
    {
        Assert.True(job.Status == DownloadStatus.Completed,
            $"job status={job.Status} message='{job.StatusMessage}' received={job.ReceivedBytes}/{job.TotalBytes}");
    }

    [Fact]
    public async Task PerConnectionServerCap_ScalerAddsConnections_AndFileIsExact()
    {
        // 24 MB with a heavy per-connection throttle: 8 connections finish slowly, the
        // scaler must detect the flat-line and split segments to add more connections.
        var data = RandomData(24 * 1024 * 1024);
        using var server = new RangeTestServer(data) { ChunkDelayMs = 120 };   // ≈0.55 MB/s per connection
        server.Start();

        var dir = NewTempDir();
        try
        {
            var binds = new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") };
            var job = new DownloadJob(server.BaseUrl, binds, new DownloadScope("All", new List<string>()), dir, ScalingOptions());
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(120));

            AssertCompleted(job);
            Assert.Equal(data.Length, new FileInfo(job.FilePath).Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Sha256File(job.FilePath));

            // The scaler must have added connections beyond the static 8.
            Assert.True(job.SegmentCount > 8,
                $"scaler never added connections: SegmentCount={job.SegmentCount}");
            Assert.True(job.SegmentCount <= 32, "segment cap must hold");

            // Both "NICs" must have carried traffic.
            Assert.True(job.PerAdapterBytesTotal[0] > 0, "adapter 0 got no bytes");
            Assert.True(job.PerAdapterBytesTotal[1] > 0, "adapter 1 got no bytes");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task IdleFastAdapter_StealsWork_FromSlowAdapter()
    {
        // 127.0.0.1 = slow radio (heavy per-connection delay), 127.0.0.2 = fast cable.
        // The fast adapter finishes its interleaved half almost instantly; work stealing
        // must move most of the remaining bytes onto it. (Generous margins: the slow
        // phase must long outlast any machine-load jitter in CI.)
        var data = RandomData(12 * 1024 * 1024);
        using var server = new RangeTestServer(data)
        {
            SlowRemotePrefix = "127.0.0.1",
            SlowChunkDelayMs = 250,        // ≈0.26 MB/s per slow connection
        };
        server.Start();

        var dir = NewTempDir();
        try
        {
            // Slot 0 = slow NIC, slot 1 = fast NIC (bind order matters).
            var binds = new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") };
            var job = new DownloadJob(server.BaseUrl, binds, new DownloadScope("All", new List<string>()), dir, ScalingOptions());
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(120));

            AssertCompleted(job);
            Assert.Equal(data.Length, new FileInfo(job.FilePath).Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Sha256File(job.FilePath));

            long slow = job.PerAdapterBytesTotal[0];
            long fast = job.PerAdapterBytesTotal[1];
            Assert.True(slow > 0, "the slow adapter must still carry some traffic");
            Assert.True(fast > slow,
                $"work stealing failed: slow={slow} fast={fast} — the fast adapter should carry MORE than half");

            // The scaler must have split segments to feed the idle fast adapter.
            Assert.True(job.SegmentCount > 8, $"no splits: SegmentCount={job.SegmentCount}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task ScalingDisabled_KeepsStaticPlan()
    {
        var data = RandomData(2 * 1024 * 1024);
        using var server = new RangeTestServer(data);
        server.Start();

        var dir = NewTempDir();
        try
        {
            var options = ScalingOptions();
            options.AdaptiveScaling = false;
            var binds = new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") };
            var job = new DownloadJob(server.BaseUrl, binds, new DownloadScope("All", new List<string>()), dir, options);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

            AssertCompleted(job);
            Assert.Equal(8, job.SegmentCount);   // static plan — never grows
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Sha256File(job.FilePath));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task StoodDownScaler_Reprobes_WhenServerFreesCapacity()
    {
        // Regression for the v1 real-world failure: the scaler stood down permanently after
        // ONE failed 2-second evaluation, so downloads never grew past the initial segment
        // plan — a server that frees capacity mid-download was never noticed again.
        //
        // Here the server throttles every connection for the first 8 seconds, then goes fast.
        // The scaler must: plateau → probe → (still slow) stand down → cool down → re-probe —
        // and once the fast path is available, ramp the connection count past the initial 8.
        var data = RandomData(80 * 1024 * 1024);
        using var server = new RangeTestServer(data) { ChunkDelayMs = 150, ThrottleForMs = 8_000 };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var options = ScalingOptions(minSegment: 256 * 1024, scaleIntervalMs: 100);
            var binds = new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") };
            var job = new DownloadJob(server.BaseUrl, binds, new DownloadScope("All", new List<string>()), dir, options);

            var diagnostics = new System.Collections.Concurrent.ConcurrentBag<string>();
            job.DiagnosticEvent += (_, kind, _) => diagnostics.Add(kind);

            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(150));

            AssertCompleted(job);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Sha256File(job.FilePath));

            Assert.True(job.SegmentCount > 8,
                $"scaler never recovered after the server freed capacity: SegmentCount={job.SegmentCount}");
            Assert.Contains("adaptive_scale", diagnostics);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task ScaledDownload_SurvivesPauseResume_WithSplitSegments()
    {
        // Pause mid-download after splits happened; resume must restore the split
        // segment list from disk and complete byte-exact.
        var data = RandomData(32 * 1024 * 1024);
        using var server = new RangeTestServer(data) { ChunkDelayMs = 150 };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var binds = new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") };
            var job = new DownloadJob(server.BaseUrl, binds, new DownloadScope("All", new List<string>()), dir, ScalingOptions());
            job.Start();

            // Wait until at least one split happened (or 10 s), then pause mid-flight.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (job.SegmentCount <= 8 && job.Status == DownloadStatus.Downloading &&
                   DateTime.UtcNow < deadline)
                await Task.Delay(150);

            if (job.Status == DownloadStatus.Downloading)
            {
                job.Pause();
                await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(DownloadStatus.Paused, job.Status);
                int segmentsAtPause = job.SegmentCount;
                Assert.True(segmentsAtPause > 8, "splits must have happened before the pause");

                job.Start();
            }
            // (If it finished within the window, the completion assertions below still hold.)

            await job.Completion.WaitAsync(TimeSpan.FromSeconds(120));
            AssertCompleted(job);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Sha256File(job.FilePath));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
