using System.Net;
using System.Security.Cryptography;
using DualNet.Core.Engine;
using DualNet.Core.Models;
using Xunit;

namespace DualNet.Tests;

public class DownloadEngineTests
{
    private static readonly DownloadOptions TestOptions = new()
    {
        SegmentsSmall = 8,
        SegmentsLarge = 16,
        MinSegmentSizeBytes = 64 * 1024,
        RetryCount = 4,
        StallTimeoutMs = 30_000,
        BufferSizeBytes = 64 * 1024,
        LargeFileThresholdBytes = 500 * 1024 * 1024
    };

    private static byte[] RandomData(int size)
    {
        var data = new byte[size];
        new Random(1337).NextBytes(data);
        return data;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string DumpLog(RangeTestServer server)
    {
        try { return string.Join(" | ", server.RequestLog.ToArray().TakeLast(15)); }
        catch { return ""; }
    }

    private static void AssertCompleted(DownloadJob job, RangeTestServer? server = null)
    {
        Assert.True(job.Status == DownloadStatus.Completed,
            $"job status={job.Status} message='{job.StatusMessage}' received={job.ReceivedBytes}/{job.TotalBytes} requests=[{(server is null ? "" : DumpLog(server))}]");
    }

    private static void AssertFileMatches(DownloadJob job, byte[] data, RangeTestServer? server = null)
    {
        Assert.True(File.Exists(job.FilePath), $"file missing: {job.FilePath}");
        var actual = File.ReadAllBytes(job.FilePath);
        Assert.True(actual.Length == data.Length,
            $"length mismatch: file={actual.Length} expected={data.Length} (status={job.Status} msg='{job.StatusMessage}' requests=[{(server is null ? "" : DumpLog(server))}])");
        for (int i = 0; i < data.Length; i++)
        {
            if (actual[i] != data[i])
                Assert.Fail($"first diff at offset {i}: file={actual[i]} expected={data[i]} (job status={job.Status} msg='{job.StatusMessage}')");
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dualnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static DownloadScope ScopeAll() => new("All", new List<string> { "test-adapter" });

    [Fact]
    public async Task SegmentedDownload_UsesBothBoundSourceIps_AndProducesOriginalFile()
    {
        var data = RandomData(8 * 1024 * 1024);
        using var server = new RangeTestServer(data);
        server.Start();

        var dir = NewTempDir();
        try
        {
            // Two "adapters": 127.0.0.1 and 127.0.0.2 (second loopback alias, available on Linux & Windows).
            var binds = new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") };
            var job = new DownloadJob(server.BaseUrl, binds, ScopeAll(), dir, TestOptions);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));

            AssertCompleted(job, server);
            Assert.Equal(8, job.SegmentCount);
            AssertFileMatches(job, data, server);
            Assert.Equal(Sha256(data), Sha256File(job.FilePath));

            // The server must have received connections from BOTH bound source addresses.
            var remotes = server.SeenRemoteAddresses.Distinct().ToList();
            Assert.Contains("127.0.0.1", remotes);
            Assert.Contains("127.0.0.2", remotes);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task ServerWithoutRangeSupport_FallsBackToSingleStream()
    {
        var data = RandomData(2 * 1024 * 1024);
        using var server = new RangeTestServer(data) { ServeWithoutRanges = true };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var job = new DownloadJob(server.BaseUrl, new[] { IPAddress.Loopback }, ScopeAll(), dir, TestOptions);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

            AssertCompleted(job, server);
            Assert.False(job.IsRangeSupported);
            AssertFileMatches(job, data, server);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Pause_Resume_RestartRecovery_KeepsProgressAndCompletes()
    {
        var data = RandomData(8 * 1024 * 1024);
        using var server = new RangeTestServer(data) { ChunkDelayMs = 25 };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var job = new DownloadJob(server.BaseUrl, new[] { IPAddress.Loopback }, ScopeAll(), dir, TestOptions);
            job.Start();

            // Wait until ~30% live progress (segment.Written is updated per chunk), then pause.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (job.TotalProgress() < data.Length * 0.30
                   && job.Status is DownloadStatus.Downloading or DownloadStatus.Preparing
                   && DateTime.UtcNow < deadline)
                await Task.Delay(30);
            Assert.True(job.TotalProgress() > 0,
                $"download did not progress: status={job.Status} msg='{job.StatusMessage}' requests=[{DumpLog(server)}]");

            job.Pause();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(DownloadStatus.Paused, job.Status);

            long savedProgress = job.ReceivedBytes;
            Assert.True(File.Exists(job.FilePath + ".dualnet"), "state file missing after pause");
            Assert.True(savedProgress > 0);

            job.Dispose();

            // Simulate an app restart: restore from the state file.
            var restored = DownloadJob.TryRestore(job.FilePath + ".dualnet", TestOptions,
                (_, _) => new[] { IPAddress.Loopback });
            Assert.NotNull(restored);
            Assert.Equal(DownloadStatus.Paused, restored!.Status);

            restored.Start();
            await restored.Completion.WaitAsync(TimeSpan.FromSeconds(90));
            AssertCompleted(restored, server);
            AssertFileMatches(restored, data, server);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Resume_AfterRemoteFileChanged_RestartsFromScratch()
    {
        var original = RandomData(4 * 1024 * 1024);
        using var server = new RangeTestServer(original) { ChunkDelayMs = 25 };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var job = new DownloadJob(server.BaseUrl, new[] { IPAddress.Loopback }, ScopeAll(), dir, TestOptions);
            job.Start();

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (job.TotalProgress() < original.Length * 0.30
                   && job.Status is DownloadStatus.Downloading or DownloadStatus.Preparing
                   && DateTime.UtcNow < deadline)
                await Task.Delay(30);
            Assert.True(job.TotalProgress() > 0,
                $"download did not progress: status={job.Status} msg='{job.StatusMessage}' requests=[{DumpLog(server)}]");

            job.Pause();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(DownloadStatus.Paused, job.Status);
            job.Dispose();

            // Server content + ETag change while paused.
            var replacement = RandomData(4 * 1024 * 1024);
            server.Data = replacement;
            server.ETag = "\"test-etag-v2\"";

            var restored = DownloadJob.TryRestore(job.FilePath + ".dualnet", TestOptions,
                (_, _) => new[] { IPAddress.Loopback });
            Assert.NotNull(restored);
            restored!.Start();
            await restored.Completion.WaitAsync(TimeSpan.FromSeconds(90));

            AssertCompleted(restored, server);
            AssertFileMatches(restored, replacement, server);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task FlakyServer_AbortedConnections_AreRetriedAutomatically()
    {
        var data = RandomData(2 * 1024 * 1024);
        using var server = new RangeTestServer(data) { FailFirstRequests = 3 };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var job = new DownloadJob(server.BaseUrl, new[] { IPAddress.Loopback }, ScopeAll(), dir, TestOptions);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));

            AssertCompleted(job, server);
            AssertFileMatches(job, data, server);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task InvalidUrl_FailsWithMessage()
    {
        var dir = NewTempDir();
        try
        {
            var job = new DownloadJob("http://127.0.0.1:1/nonexistent.bin",
                new[] { IPAddress.Loopback }, ScopeAll(), dir, TestOptions);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(DownloadStatus.Failed, job.Status);
            Assert.False(string.IsNullOrEmpty(job.StatusMessage));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task DownloadManager_QueueRespectsMaxConcurrent()
    {
        var data = RandomData(3 * 1024 * 1024);
        using var server = new RangeTestServer(data) { ChunkDelayMs = 4 };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var settings = new AppSettings { MaxConcurrentDownloads = 2 };
            using var manager = new DownloadManager(() => settings);

            var jobs = Enumerable.Range(0, 4).Select(_ =>
                manager.AddDownload(server.BaseUrl, new[] { IPAddress.Loopback }, ScopeAll(), dir, TestOptions)
            ).ToList();

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (jobs.Any(j => j.Status is not DownloadStatus.Completed) && DateTime.UtcNow < deadline)
                await Task.Delay(150);

            var failures = string.Join("\n", jobs.Select(j =>
                $"{Path.GetFileName(j.FilePath)}: {j.Status} '{j.StatusMessage}'"));
            Assert.True(jobs.All(j => j.Status == DownloadStatus.Completed), $"not all completed:\n{failures}\nrequests=[{DumpLog(server)}]");
            foreach (var job in jobs)
                AssertFileMatches(job, data, server);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
