using System.Reflection;
using Avalonia.Threading;
using Sentry;

namespace DualNet.App;

/// <summary>
/// Sentry telemetry for DualNet Downloader (desktop/Avalonia flavor).
/// Replaces the ASP.NET Global.asax wiring from the onboarding snippet with the
/// correct hooks for a desktop app: AppDomain + TaskScheduler + Dispatcher handlers,
/// startup logs, and download lifecycle logs/metrics.
/// Never throws — telemetry must never break downloads.
/// </summary>
public static class SentrySetup
{
    private const string Dsn =
        "https://354263622957c3bcf410a3cb253b5bb9@o4512147759235072.ingest.us.sentry.io/4512249722241024";

    private static IDisposable? _sentrySdk;
    private static bool _initialized;

    public static string VersionText =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static KeyValuePair<string, object> Tag(string k, object v) => new(k, v);

    /// <summary>Disambiguates the IEnumerable vs ReadOnlySpan Emit* overloads.</summary>
    private static IEnumerable<KeyValuePair<string, object>> Tags(params KeyValuePair<string, object>[] tags) => tags;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            _sentrySdk = SentrySdk.Init(o =>
            {
                o.Dsn = Dsn;
#if DEBUG
                // While configuring for the first time, see what the SDK is doing:
                o.Debug = true;
#else
                o.Debug = false;
#endif
                // Capture 100% of transactions for tracing; adjust in production if noisy.
                o.TracesSampleRate = 1.0;
                // Enable logs to be sent to Sentry.
                o.EnableLogs = true;
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

    public static void TrackDownloadStarted(string scope, int adapterCount)
    {
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
        try
        {
            SentrySdk.Metrics.EmitCounter("download_canceled", 1,
                Tags(Tag("scope", scope)), null);
        }
        catch { }
    }

    public static void CaptureException(Exception ex, string kind, bool isTerminating = false)
    {
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
