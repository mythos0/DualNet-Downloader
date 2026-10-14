using System.Text;
using System.Collections.Concurrent;
using DualNet.App;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace DualNet.Tests;

/// <summary>
/// Real-pipeline Sentry tests: the SDK is initialized with a recording transport (no network),
/// then the app's actual telemetry helpers are exercised and the emitted envelopes are
/// verified — proving events, exceptions, structured logs and metrics flow end to end.
/// </summary>
public class SentryIntegrationTests
{
    private sealed class RecordingTransport : ITransport
    {
        public ConcurrentQueue<Envelope> Envelopes { get; } = new();
        public Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            Envelopes.Enqueue(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class SilentLogger : IDiagnosticLogger
    {
        public bool IsEnabled(SentryLevel level) => false;
        public void Log(SentryLevel level, string message, Exception? exception = null, params object[] args) { }
    }

    private static async Task<string> ItemJsonAsync(EnvelopeItem item)
    {
        using var ms = new MemoryStream();
        await item.SerializeAsync(ms, new SilentLogger());
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<List<string>> AllItemJsonAsync(ConcurrentQueue<Envelope> envelopes)
    {
        var jsons = new List<string>();
        foreach (var envelope in envelopes)
            foreach (var item in envelope.Items)
                jsons.Add(await ItemJsonAsync(item));
        return jsons;
    }

    private const string TestDsn =
        "https://1234567890abcdef1234567890abcdef@o123456.ingest.sentry.io/7654321";

    [Fact]
    public async Task CapturePipeline_EventExceptionLogMetric_AllReachTransport()
    {
        var transport = new RecordingTransport();
        try
        {
            using (SentrySdk.Init(o =>
                   {
                       o.Dsn = TestDsn;
                       o.Transport = transport;
                       o.AutoSessionTracking = false;
                       o.TracesSampleRate = 1.0;
                       o.EnableLogs = true;          // structured logs — same as production config
                       o.Debug = false;
                   }))
            {
                SentrySdk.CaptureMessage("dualnet-selftest-message");
                SentrySdk.CaptureException(new InvalidOperationException("dualnet-selftest-exception"));
                SentrySdk.Logger.LogInfo("dualnet-selftest-log {0}", 42);
                SentrySdk.Metrics.EmitCounter("dualnet_selftest_metric", 1);
                await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            }

            Assert.True(transport.Envelopes.Count >= 3,
                $"expected >=3 envelopes (event/exception/log/metric), got {transport.Envelopes.Count}");

            var items = await AllItemJsonAsync(transport.Envelopes);

            // The exception must arrive with its type and our message.
            Assert.Contains(items, j => j.Contains("InvalidOperationException") &&
                                        j.Contains("dualnet-selftest-exception"));

            // The capture-message event must arrive with its text.
            Assert.Contains(items, j => j.Contains("dualnet-selftest-message"));

            // Structured logs travel as envelope items of type "log".
            Assert.Contains(transport.Envelopes, e =>
                e.Items.Any(i => string.Equals(i.TryGetType(), "log", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            SentrySdk.Flush(TimeSpan.FromSeconds(1));
            SentrySetup.ResetForTests();
        }
    }

    [Fact]
    public void SentrySetup_Lifecycle_InitDisabledDisabledRestart()
    {
        try
        {
            SentrySetup.ResetForTests();

            // Disabled (empty DSN): configured == false, every helper is a safe no-op.
            SentrySetup.Init("");
            Assert.False(SentrySetup.IsConfigured);
            SentrySetup.TrackDownloadStarted("scope", 1);
            SentrySetup.TrackDownloadCompleted("scope", 1024, 1.0, 1024);
            SentrySetup.TrackDownloadFailed("scope", "boom");
            SentrySetup.TrackDownloadCanceled("scope");
            SentrySetup.TrackDiagnostic("unit_test", "no dsn");
            SentrySetup.TrackSpeedTest("eth", 100, true);
            SentrySetup.CaptureException(new Exception("no dsn"), "test");
            SentrySetup.CaptureMessage("no dsn", "test");

            // Restart with a DSN: telemetry is on.
            SentrySetup.Restart(TestDsn);
            Assert.True(SentrySetup.IsConfigured);
            Assert.Equal(TestDsn, SentrySetup.ActiveDsn);
        }
        finally
        {
            SentrySetup.ResetForTests();
        }
    }

    [Fact]
    public async Task SentrySetup_Trackers_EmitEnvelopesThroughTransport()
    {
        var transport = new RecordingTransport();
        try
        {
            // Point SentrySetup at a recording transport by initializing the global SDK first.
            using (SentrySdk.Init(o =>
                   {
                       o.Dsn = TestDsn;
                       o.Transport = transport;
                       o.AutoSessionTracking = false;
                       o.EnableLogs = true;
                       o.Debug = false;
                   }))
            {
                SentrySetup.UseDsnForTests(TestDsn);

                SentrySetup.TrackAppStart();
                SentrySetup.TrackDownloadStarted("combined", 2);
                SentrySetup.TrackDownloadCompleted("combined", 10_000_000, 5.0, 2_000_000);
                SentrySetup.TrackSpeedTest("Ethernet", 87.5, true);
                SentrySetup.TrackDiagnostic("range_fallback", "unit-test");
                await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            }

            Assert.True(transport.Envelopes.Count >= 2,
                $"expected telemetry envelopes, got {transport.Envelopes.Count}");
        }
        finally
        {
            SentrySdk.Flush(TimeSpan.FromSeconds(1));
            SentrySetup.ResetForTests();
        }
    }

    [Fact]
    public async Task SendTestEvent_WithDsn_ReturnsTrue_AndSendsEnvelope()
    {
        var transport = new RecordingTransport();
        try
        {
            using (SentrySdk.Init(o =>
                   {
                       o.Dsn = TestDsn;
                       o.Transport = transport;
                       o.AutoSessionTracking = false;
                       o.Debug = false;
                   }))
            {
                SentrySetup.UseDsnForTests(TestDsn);
                var ok = await SentrySetup.SendTestEventAsync();
                Assert.True(ok);
                Assert.True(transport.Envelopes.Count >= 1, "self-test event envelope missing");
            }
        }
        finally
        {
            SentrySdk.Flush(TimeSpan.FromSeconds(1));
            SentrySetup.ResetForTests();
        }
    }

    [Fact]
    public async Task SendTestEvent_WithoutDsn_ReturnsFalse()
    {
        try
        {
            SentrySetup.ResetForTests();
            SentrySetup.Init("");
            var ok = await SentrySetup.SendTestEventAsync();
            Assert.False(ok);
        }
        finally
        {
            SentrySetup.ResetForTests();
        }
    }
}
