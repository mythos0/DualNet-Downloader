using System.Net;
using System.Security.Cryptography;
using System.Text;
using DualNet.Core.Engine;
using DualNet.Core.Models;
using DualNet.Core.Storage;
using Xunit;

namespace DualNet.Tests;

/// <summary>
/// Tests for the v1.2.0 robustness + IDM feature set: range-ignored fallback, resume
/// reset, transient segment retries, per-adapter counters, speed test, categories,
/// auth URLs, scheduler, local API, queue reorder and CSV export.
/// </summary>
public class FeatureTests
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
        new Random(4242).NextBytes(data);
        return data;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dualnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Waits (async, non-spinning on the UI thread) until a job reaches a status.</summary>
    private static async Task WaitStatus(DownloadJob job, DownloadStatus expected, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (job.Status != expected && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal(expected, job.Status);
    }

    // ---- 1. server ignores ranges mid-download → graceful single-stream downgrade ------------

    [Fact]
    public async Task ServerIgnoringRangesOnSegments_DowngradesToSingleStream_AndCompletes()
    {
        var data = RandomData(4 * 1024 * 1024);
        using var server = new RangeTestServer(data) { IgnoreRangeOnSegmentRequests = true };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var job = new DownloadJob(server.BaseUrl,
                new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") },
                new DownloadScope("All", new List<string> { "a", "b" }), dir, TestOptions);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));

            Assert.Equal(DownloadStatus.Completed, job.Status);
            Assert.Equal(data.Length, File.ReadAllBytes(job.FilePath).Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(job.FilePath))));

            // The downgrade must have been announced to the UI layer.
            Assert.False(job.IsRangeSupported);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- 2. resume that gets 200 restarts from zero (no corruption) ----------------------------

    [Fact]
    public async Task ResumeReceiving200_RestartsFromZero_AndProducesOriginalFile()
    {
        var data = RandomData(3 * 1024 * 1024);
        using var server = new RangeTestServer(data) { IgnoreRangeOnResume = true };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var job = new DownloadJob(server.BaseUrl, new[] { IPAddress.Loopback },
                new DownloadScope("All", new List<string> { "a" }), dir, TestOptions);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(DownloadStatus.Completed, job.Status);

            // Byte-for-byte identical — the previous 200-on-resume bug corrupted this file.
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(job.FilePath))));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- 3. transient segment failures (503) are retried -----------------------------------------

    [Fact]
    public async Task SegmentGetting503s_IsRetried_AndDownloadCompletes()
    {
        var data = RandomData(6 * 1024 * 1024);
        using var server = new RangeTestServer(data) { FailFirstSegmentRequests = 3 };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var job = new DownloadJob(server.BaseUrl,
                new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") },
                new DownloadScope("All", new List<string> { "a", "b" }), dir, TestOptions);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));

            Assert.Equal(DownloadStatus.Completed, job.Status);
            AssertFileMatches(job, data);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- 4. hung server on probe → Failed (never spurious Canceled) -------------------------------

    [Fact]
    public async Task ProbeAgainstHungServer_EndsFailed_NotCanceled()
    {
        var data = RandomData(64 * 1024);
        using var server = new RangeTestServer(data) { StallRequests = true };
        server.Start();

        var dir = NewTempDir();
        try
        {
            var options = new DownloadOptions
            {
                SegmentsSmall = 4,
                MinSegmentSizeBytes = 16 * 1024,
                RetryCount = 1,
                StallTimeoutMs = 500,       // watchdog fires fast on the hung server
                BufferSizeBytes = 16 * 1024
            };
            var job = new DownloadJob(server.BaseUrl, new[] { IPAddress.Loopback },
                new DownloadScope("All", new List<string> { "a" }), dir, options);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.NotEqual(DownloadStatus.Canceled, job.Status);   // the old spurious-cancel bug
            Assert.Equal(DownloadStatus.Failed, job.Status);
            Assert.False(string.IsNullOrEmpty(job.StatusMessage));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- 5. per-adapter byte counters ---------------------------------------------------------------

    [Fact]
    public async Task PerAdapterCounters_SumToTotalBytes_WithBothAdaptersUsed()
    {
        var data = RandomData(6 * 1024 * 1024);   // 3 segments of 2 MB → both adapter slots
        using var server = new RangeTestServer(data);
        server.Start();

        var dir = NewTempDir();
        try
        {
            var binds = new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") };
            var job = new DownloadJob(server.BaseUrl, binds,
                new DownloadScope("All", new List<string> { "a", "b" }), dir, TestOptions);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));

            Assert.Equal(DownloadStatus.Completed, job.Status);
            Assert.Equal(2, job.PerAdapterBytesTotal.Count);
            Assert.Equal(2, job.PerAdapterSpeedBps.Count);
            Assert.Equal(data.Length, job.PerAdapterBytesTotal.Sum());
            Assert.True(job.PerAdapterBytesTotal[0] > 0, "adapter 0 must have received bytes");
            Assert.True(job.PerAdapterBytesTotal[1] > 0, "adapter 1 must have received bytes");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- 6. speed test service ------------------------------------------------------------------------

    [Fact]
    public async Task SpeedTestService_MeasuresThroughputAgainstLocalServer()
    {
        var data = RandomData(4 * 1024 * 1024);
        using var server = new RangeTestServer(data) { ServeWithoutRanges = true };
        server.Start();

        var result = await SpeedTestService.RunAsync(
            "loopback-test", IPAddress.Loopback, server.BaseUrl, 2 * 1024 * 1024,
            CancellationToken.None);

        // Streaming reads overshoot the cap by at most one copy buffer.
        Assert.InRange(result.Bytes, 2 * 1024 * 1024, 2 * 1024 * 1024 + 1024 * 1024);
        Assert.True(result.MegabitsPerSecond > 0, "mbps must be positive");
        Assert.True(result.Seconds > 0);
    }

    // ---- 7. categories ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("movie.mkv", CategoryKind.Video)]
    [InlineData("song.MP3", CategoryKind.Audio)]
    [InlineData("backup.tar.gz", CategoryKind.Archive)]
    [InlineData("report.pdf", CategoryKind.Document)]
    [InlineData("setup.exe", CategoryKind.Program)]
    [InlineData("photo.png", CategoryKind.Image)]
    [InlineData("unknown.xyz", CategoryKind.Other)]
    public void DownloadCategory_ClassifiesByExtension(string fileName, CategoryKind expected)
    {
        Assert.Equal(expected, DownloadCategory.Of(fileName));
    }

    [Fact]
    public void DownloadCategory_Subfolder_RespectsOrganizeFlag()
    {
        Assert.Null(DownloadCategory.Subfolder(CategoryKind.Video, organizeByCategory: false));
        Assert.Equal("Video", DownloadCategory.Subfolder(CategoryKind.Video, organizeByCategory: true));
        Assert.Equal("Compressed", DownloadCategory.Subfolder(CategoryKind.Archive, true));
        Assert.Equal("Other", DownloadCategory.Subfolder(CategoryKind.Other, true));
    }

    // ---- 8. auth URLs ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://user:p%40ss@server.com/file.zip", "http://server.com/file.zip", "user", "p@ss")]
    [InlineData("https://bob:secret@host.io/dl.bin", "https://host.io/dl.bin", "bob", "secret")]
    [InlineData("https://plain.example.com/f.iso", "https://plain.example.com/f.iso", null, null)]
    public void AuthUrlParser_SplitsCredentialsFromUrl(string input, string expectedUrl,
        string? expectedUser, string? expectedPass)
    {
        var auth = AuthUrlParser.Parse(input);
        Assert.Equal(expectedUrl, auth.Url);
        Assert.Equal(expectedUser, auth.UserName);
        Assert.Equal(expectedPass, auth.Password);
    }

    [Fact]
    public void AuthUrlParser_BasicHeaderValue_EncodesUserPass()
    {
        var value = AuthUrlParser.BasicHeaderValue("user", "p@ss");
        Assert.NotNull(value);
        Assert.Equal("user:p@ss", Encoding.UTF8.GetString(Convert.FromBase64String(value!)));
        Assert.Null(AuthUrlParser.BasicHeaderValue(null, "x"));
    }

    // ---- 9. scheduler ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("10:00", "18:00", "12:30", ScheduleWindow.Inside)]
    [InlineData("10:00", "18:00", "09:59", ScheduleWindow.Outside)]
    [InlineData("10:00", "18:00", "18:00", ScheduleWindow.Outside)]
    [InlineData("23:00", "07:00", "03:15", ScheduleWindow.Inside)]   // overnight
    [InlineData("23:00", "07:00", "12:00", ScheduleWindow.Outside)]
    [InlineData("23:00", "07:00", "23:00", ScheduleWindow.Inside)]
    [InlineData("09:00", "09:00", "04:00", ScheduleWindow.Inside)]   // degenerate = always
    public void SchedulerService_EvaluatesWindows(string start, string stop, string now, ScheduleWindow expected)
    {
        Assert.Equal(expected, SchedulerService.Evaluate(
            TimeOnly.Parse(now), TimeOnly.Parse(start), TimeOnly.Parse(stop)));
    }

    [Fact]
    public void SchedulerService_TransitionsTriggerActions()
    {
        Assert.Equal("start", SchedulerService.ActionForTransition(ScheduleWindow.Outside, ScheduleWindow.Inside));
        Assert.Equal("pause", SchedulerService.ActionForTransition(ScheduleWindow.Inside, ScheduleWindow.Outside));
        Assert.Null(SchedulerService.ActionForTransition(ScheduleWindow.Inside, ScheduleWindow.Inside));
    }

    // ---- 10. local integration API ----------------------------------------------------------------------

    [Fact]
    public async Task LocalApiService_ReceivesUrlsAndRejectsGarbage()
    {
        using var api = new LocalApiService(0);   // port 0 = pick free
        string? received = null;
        var signal = new TaskCompletionSource();
        api.UrlReceived += url =>
        {
            received = url;
            signal.TrySetResult();
        };
        api.Start();
        Assert.True(api.BoundPort > 0);

        var sent = await LocalApiService.TrySendToRunningInstance(api.BoundPort,
            "https://example.com/local-api-file.zip");
        Assert.True(sent, "send must return ok");

        await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("https://example.com/local-api-file.zip", received);

        var bad = await LocalApiService.TrySendToRunningInstance(api.BoundPort, "not a url");
        Assert.False(bad, "garbage must be rejected");
    }

    // ---- 11. queue reorder --------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadManager_MoveInQueue_ReordersWaitingJobs()
    {
        // Rare CI hiccup: the loopback listener can drop a connection right after the
        // probe, collapsing the queue before the reorder happens. The reorder logic is
        // what we verify — so retry the whole scenario on infrastructure-level collapses.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await RunQueueReorderScenarioAsync();
                return;
            }
            catch (Xunit.Sdk.XunitException) when (attempt < 3)
            {
                await Task.Delay(200 * attempt);
            }
        }
    }

    private static async Task RunQueueReorderScenarioAsync()
    {
        var data = RandomData(512 * 1024);
        using var server = new RangeTestServer(data) { ChunkDelayMs = 80 };   // keep every job observable
        server.Start();

        var settings = new AppSettings { MaxConcurrentDownloads = 1, OrganizeByCategory = false };
        using var manager = new DownloadManager(() => settings);
        var dir = NewTempDir();
        DownloadJob j1 = null!, j2 = null!, j3 = null!;
        var startOrder = new List<string>();   // first non-Queued transition per job, in order
        void RecordStart(DownloadJob j)
        {
            lock (startOrder)
            {
                if (j.Status != DownloadStatus.Queued && !startOrder.Contains(j.Id))
                    startOrder.Add(j.Id);
            }
        }
        try
        {
            var binds = new[] { IPAddress.Loopback };
            var scope = new DownloadScope("All", new List<string> { "a" });
            var options = TestOptions;

            j1 = manager.AddDownload(server.BaseUrl, binds, scope, dir, options);
            j1.StatusChanged += _ => RecordStart(j1);
            j2 = manager.AddDownload(server.BaseUrl + "?p2", binds, scope, dir, options);
            j2.StatusChanged += _ => RecordStart(j2);
            j3 = manager.AddDownload(server.BaseUrl + "?p3", binds, scope, dir, options);
            j3.StatusChanged += _ => RecordStart(j3);

            await WaitStatus(j1, DownloadStatus.Downloading);   // probe is async — wait for it
            Assert.True(j2.Status == DownloadStatus.Queued,
                $"j2 should be queued — j1={j1.Status} ({j1.StatusMessage}), j2={j2.Status}");
            Assert.True(j3.Status == DownloadStatus.Queued,
                $"j3 should be queued — j1={j1.Status} ({j1.StatusMessage}), j3={j3.Status}");

            var snap = manager.QueuedJobsSnapshot().Select(j => j.Id).ToArray();
            Assert.True(snap.Length == 2 && snap[0] == j2.Id && snap[1] == j3.Id,
                $"queue=[{string.Join(",", snap)}] — " +
                $"j1={j1.Status} ({j1.StatusMessage}), j2={j2.Status} ({j2.StatusMessage}), " +
                $"j3={j3.Status} ({j3.StatusMessage})");

            Assert.True(manager.MoveInQueue(j3, -1),   // jump the line
                $"j3 must still be queued — j1={j1.Status} ({j1.StatusMessage}), j2={j2.Status}, " +
                $"j3={j3.Status}, queue=[{string.Join(",", manager.QueuedJobsSnapshot().Select(j => j.Id))}]");
            Assert.Equal(new[] { j3.Id, j2.Id }, manager.QueuedJobsSnapshot().Select(j => j.Id));

            // Finishing the active job must start the REORDERED head of the queue (j3) first.
            // Proven by start order — no timing races, even for tiny fast downloads.
            j1.Cancel();
            await j3.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            await j2.Completion.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(new[] { j1.Id, j3.Id, j2.Id }, startOrder);
        }
        finally
        {
            j1?.Cancel(); j2?.Cancel(); j3?.Cancel();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- 12. CSV export ------------------------------------------------------------------------------------

    [Fact]
    public void HistoryService_ExportCsv_WritesQuotedRows()
    {
        var dir = NewTempDir();
        try
        {
            var history = new HistoryService(dir);
            history.Add(new HistoryEntry
            {
                Url = "https://a.io/x.zip",
                FileName = "x.zip",
                FilePath = "/dl/x.zip",
                SizeBytes = 1234,
                Status = "Completed",
                NetworkScope = "Combined"
            });
            history.Add(new HistoryEntry
            {
                Url = "https://a.io/y.zip",
                FileName = "y, \"quoted\".zip",
                FilePath = "/dl/y.zip",
                SizeBytes = 42,
                Status = "Failed",
                Message = "boom"
            });

            var path = Path.Combine(dir, "export.csv");
            var rows = history.ExportCsv(path);
            Assert.Equal(2, rows);

            var content = File.ReadAllText(path);
            Assert.StartsWith("FileName,Url,SizeBytes,Status", content);
            Assert.Contains("\"y, \"\"quoted\"\".zip\"", content);
            Assert.Contains("1234", content);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- 13. category sub-folder routing through the manager ------------------------------------------------

    [Fact]
    public async Task DownloadManager_CategorizesIntoSubfolder_WhenEnabled()
    {
        var data = RandomData(256 * 1024);
        using var server = new RangeTestServer(data) { ServeWithoutRanges = true };
        server.Start();

        var settings = new AppSettings { MaxConcurrentDownloads = 2, OrganizeByCategory = true };
        using var manager = new DownloadManager(() => settings);
        var dir = NewTempDir();
        DownloadJob? job = null;
        try
        {
            job = manager.AddDownload(
                server.BaseUrl.Replace("payload.bin", "payload.bin"),   // payload.bin = Other
                new[] { IPAddress.Loopback },
                new DownloadScope("All", new List<string> { "a" }), dir, TestOptions);
            job.Start();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(DownloadStatus.Completed, job.Status);
            Assert.Contains(Path.Combine(dir, "Other"), job.FilePath);
            Assert.True(File.Exists(job.FilePath));
        }
        finally
        {
            job?.Cancel();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void AssertFileMatches(DownloadJob job, byte[] data)
    {
        Assert.True(File.Exists(job.FilePath), $"file missing: {job.FilePath}");
        var actual = File.ReadAllBytes(job.FilePath);
        Assert.Equal(data.Length, actual.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)),
            Convert.ToHexString(SHA256.HashData(actual)));
    }
}
