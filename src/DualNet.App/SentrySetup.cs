using System.Reflection;
using Avalonia.Threading;
using Sentry;

namespace DualNet.App;

/// <summary>
/// Sentry telemetry for DualNet Downloader (desktop/Avalonia flavor).
/// Replaces the ASP.NET Global.asax wiring from the onboarding snippet with the
/// correct hooks for a desktop app: AppDomain + TaskScheduler + Dispatcher handlers,
/// startup logs, and download lifecycle logs/metrics.
///
/// The DSN can be overridden at runtime (Settings → Telemetry) — useful when the DSN
/// is rotated, because a stale DSN is rejected server-side (HTTP 403 "ProjectId") and
/// events silently never arrive. "Send test event" verifies the pipeline end to end.
/// Never throws — telemetry must never break downloads.
/// </summary>
public static class SentrySetup
{
    public const string DefaultDsn =
        "https://354263622957c3bcf410a3cb253b5bb9@o4512147759235072.ingest.us.sentry.io/4512249722241024";

    private static IDisposable? _sentrySdk;
    private static bool _initialized;
    private static string _dsn = DefaultDsn;

    public static string VersionText =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.2.0";

    public static string ActiveDsn => _dsn;

    /// <summary>True when telemetry is configured with a non-empty DSN.</summary>
    public static bool IsConfigured => _dsn.Length > 0;

    private static KeyValuePair<string, object> Tag(string k, object v) => new(k, v);

    /// <summary>Disambiguates the IEnumerable vs ReadOnlySpan Emit* overloads.</summary>
    private static IEnumerable<KeyValuePair<string, object>> Tags(params KeyValuePair<string, object>[] tags) => tags;

    /// <summary>Initializes Sentry. Call once at startup, before any UI work.</summary>
    public static void Init()
    {
        Init(AppServices.TryLoadDsn());
    }

    /// <summary>Initializes Sentry with an explicit DSN (empty string disables telemetry).</summary>
    public static void Init(string dsn)
    {
        if (_initialized)
        {
            if (!string.Equals(dsn, _dsn, StringComparison.Ordinal))
                Restart(dsn);
            return;
        }
        _initialized = true;
        _dsn = string.IsNullOrWhiteSpace(dsn) ? "" : dsn.Trim();

        if (_dsn.Length == 0)
            return;   // telemetry disabled by the user

        try
        {
            _sentrySdk = SentrySdk.Init(o =>
            {
                o.Dsn = _dsn;
#if DEBUG
                // While configuring for the first time, see what the SDK is doing:
                o.Debug = true;
#else
                o.Debug = false;
#endif
                // Capture 100% of transactions for tracing; adjust in production if noisy.
                o.TracesSampleRate = 1.0;
                // Enable structured logs to be sent to Sentry.
                o.EnableLogs = true;
                o.AttachStacktrace = true;
                o.MaxBreadcrumbs = 200;
                // Desktop-app session analytics.
                o.AutoSessionTracking = true;
                o.Release = $"dualnet-downloader@{VersionText}";
                o.Environment =
#if DEBUG
                    "development";
#else
                    "production";
#endif
            });

            SentrySdk.SetTag("app", "dualnet-downloader");
            SentrySdk.SetTag("platform_kind", "windows-desktop");
        }
        catch
        {
            // Sentry is optional infrastructure; a broken DSN must never block the app.
        }

        // Equivalent of ASP.NET Application_Error / global error catcher, desktop edition.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                CaptureException(ex, "appdomain.unhandled", e.IsTerminating);
            else
                CaptureMessage($"Unhandled non-exception object: {e.ExceptionObject}", "appdomain.unhandled");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CaptureException(e.Exception, "taskscheduler.unobserved");
            e.SetObserved();
        };

        // Process exit: flush events before shutdown (Application_End equivalent).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushSync();
    }

    /// <summary>Re-initializes against a new DSN (Settings changed) and flushes the old client.</summary>
    public static void Restart(string dsn)
    {
        try { SentrySdk.Flush(TimeSpan.FromSeconds(1)); } catch { }
        try { _sentrySdk?.Dispose(); } catch { }
        _sentrySdk = null;
        _initialized = false;
        Init(dsn);
        if (_dsn.Length > 0)
            TrackAppStart();
    }

    /// <summary>Avalonia UI-thread crash hook — call after the dispatcher exists.</summary>
    public static void HookDispatcher()
    {
        try
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                CaptureException(e.Exception, "avalonia.dispatcher.unhandled");
                // A download manager should survive UI hiccups; swallow + report.
                e.Handled = true;
            };
        }
        catch { }
    }

    /// <summary>Startup event + test log, visible in the Sentry onboarding page.</summary>
    public static void TrackAppStart()
    {
        if (_dsn.Length == 0) return;
        try
        {
            SentrySdk.Logger.LogInfo($"DualNet Downloader v{VersionText} started");

            var transaction = SentrySdk.StartTransaction("app-startup", "app.lifecycle");
            transaction.SetTag("version", VersionText);
            transaction.Finish(SpanStatus.Ok);

            SentrySdk.Metrics.EmitCounter("app_start", 1,
                Tags(Tag("app_version", VersionText)), null);
        }
        catch { }
    }

    /// <summary>
    /// Sends one deliberately visible error-level event and flushes. Returns true when the
    /// SDK accepted it for transport (DSN configured); the dashboard side is verified by
    /// the user actually seeing this event.
    /// </summary>
    public static async Task<bool> SendTestEventAsync()
    {
        if (_dsn.Length == 0) return false;
        try
        {
            var id = SentrySdk.CaptureMessage(
                $"DualNet Sentry self-test @ {DateTime.Now:HH:mm:ss} — pipeline verification",
                s =>
                {
                    s.SetTag("kind", "selftest");
                    s.Level = SentryLevel.Warning;
                });
            SentrySdk.Logger.LogWarning("Sentry self-test event {0}", id);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void TrackDownloadStarted(string scope, int adapterCount)
    {
        if (_dsn.Length == 0) return;
        try
        {
            SentrySdk.Logger.LogInfo($"Download started ({scope}, {adapterCount} adapter(s))");
            SentrySdk.Metrics.EmitCounter("download_started", 1,
                Tags(Tag("scope", scope), Tag("adapters", adapterCount)), null);
        }
        catch { }
    }

    public static void TrackDownloadCompleted(string scope, long bytes, double durationSeconds, double speedBps)
    {
        if (_dsn.Length == 0) return;
        try
        {
            SentrySdk.Logger.LogInfo(
                $"Download completed: {bytes} bytes in {durationSeconds:0.0}s ({speedBps / (1 << 20):0.0} MB/s)");

            SentrySdk.Metrics.EmitCounter("download_completed", 1,
                Tags(Tag("scope", scope)), null);

            SentrySdk.Metrics.EmitDistribution("download_duration", durationSeconds * 1000.0,
                MeasurementUnit.Duration.Millisecond,
                Tags(Tag("scope", scope)), null);

            SentrySdk.Metrics.EmitGauge("download_speed", speedBps,
                MeasurementUnit.None,
                Tags(Tag("scope", scope)), null);
        }
        catch { }
    }

    public static void TrackDownloadFailed(string scope, string? message)
    {
        if (_dsn.Length == 0) return;
        try
        {
            SentrySdk.Logger.LogError("Download failed ({0}): {1}", scope, message ?? "unknown error");
            SentrySdk.CaptureMessage(
                $"Download failed ({scope}): {message ?? "unknown error"}",
                s => s.SetTag("kind", "download_failure"),
                SentryLevel.Error);
        }
        catch { }
    }

    public static void TrackDownloadCanceled(string scope)
    {
        if (_dsn.Length == 0) return;
        try
        {
            SentrySdk.Metrics.EmitCounter("download_canceled", 1,
                Tags(Tag("scope", scope)), null);
        }
        catch { }
    }

    /// <summary>Range downgrade / resume reset — engine silently saving a download.</summary>
    public static void TrackDiagnostic(string kind, string message)
    {
        if (_dsn.Length == 0) return;
        try
        {
            SentrySdk.Logger.LogWarning("Engine [{0}]: {1}", kind, message);
            SentrySdk.Metrics.EmitCounter($"engine_{kind}", 1, Tags(), null);
        }
        catch { }
    }

    public static void TrackSpeedTest(string adapter, double mbps, bool ok)
    {
        if (_dsn.Length == 0) return;
        try
        {
            if (ok)
            {
                SentrySdk.Logger.LogInfo($"Speed test {adapter}: {mbps:0.#} Mbps");
                SentrySdk.Metrics.EmitGauge("speedtest_mbps", mbps,
                    MeasurementUnit.None,
                    Tags(Tag("adapter", adapter)), null);
            }
            else
            {
                SentrySdk.Logger.LogError("Speed test {0} failed", adapter);
            }
        }
        catch { }
    }

    public static void CaptureException(Exception ex, string kind, bool isTerminating = false)
    {
        if (_dsn.Length == 0) return;
        try
        {
            var evt = new SentryEvent(ex) { Level = isTerminating ? SentryLevel.Fatal : SentryLevel.Error };
            evt.SetTag("kind", kind);
            SentrySdk.CaptureEvent(evt);
        }
        catch { }
    }

    public static void CaptureMessage(string message, string kind)
    {
        if (_dsn.Length == 0) return;
        try
        {
            SentrySdk.CaptureMessage(message,
                s => s.SetTag("kind", kind),
                SentryLevel.Warning);
        }
        catch { }
    }

    public static void FlushSync()
    {
        try { SentrySdk.Flush(TimeSpan.FromSeconds(3)); } catch { }
        try { _sentrySdk?.Dispose(); } catch { }
        _sentrySdk = null;
    }
}
