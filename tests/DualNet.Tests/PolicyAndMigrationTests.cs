using DualNet.Core;
using DualNet.Core.Engine;
using Xunit;

namespace DualNet.Tests;

/// <summary>Small pure-decision tests for v1.4.0 features.</summary>
public class PolicyAndMigrationTests
{
    // ---- browser mode -------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true, BrowserModePolicy.BrowserMode.Real)]
    [InlineData(false, true, true, BrowserModePolicy.BrowserMode.Reader)]   // setting off
    [InlineData(true, false, true, BrowserModePolicy.BrowserMode.Reader)]   // non-Windows
    [InlineData(true, true, false, BrowserModePolicy.BrowserMode.Reader)]   // runtime missing
    [InlineData(false, false, false, BrowserModePolicy.BrowserMode.Reader)]
    public void BrowserModePolicy_CoversEveryCombination(
        bool setting, bool windows, bool runtime, BrowserModePolicy.BrowserMode expected)
    {
        Assert.Equal(expected, BrowserModePolicy.Decide(setting, windows, runtime));
    }

    // ---- Sentry DSN migration -------------------------------------------------------------------

    [Fact]
    public void NormalizeDsn_MigratesLegacyRotatedDsn_ToShippedDefault()
    {
        Assert.Equal(SentrySetup.DefaultDsn, SentrySetup.NormalizeDsn(SentrySetup.LegacyDsns[0]));
    }

    [Fact]
    public void NormalizeDsn_KeepsFreshCustomDsn_AndMigratesEmpty()
    {
        var custom = "https://custom-key@example.ingest.sentry.io/123456";
        Assert.Equal(custom, SentrySetup.NormalizeDsn(custom));
        Assert.Equal(custom, SentrySetup.NormalizeDsn("  " + custom + "  "));
        Assert.Equal(SentrySetup.DefaultDsn, SentrySetup.NormalizeDsn(""));
        Assert.Equal(SentrySetup.DefaultDsn, SentrySetup.NormalizeDsn(null!));
    }

    [Fact]
    public void ShippedDsn_PointsAtTheCurrentProject()
    {
        // The v1.3.0 build shipped a DSN of a since-rotated project, so users saw an empty
        // dashboard. Pin the current project id so this cannot silently regress again.
        Assert.Contains("/4512249743147008", SentrySetup.DefaultDsn, StringComparison.Ordinal);
        Assert.NotEqual(SentrySetup.LegacyDsns[0], SentrySetup.DefaultDsn);
    }

    // ---- settings defaults ------------------------------------------------------------------------

    [Fact]
    public void NewSettings_DefaultToSaneValues()
    {
        var s = new DualNet.Core.Models.AppSettings();
        Assert.True(s.AdaptiveScaling);
        Assert.True(s.UseRealBrowser);
        Assert.InRange(s.MaxConnectionsPerDownload, 4, 64);
    }

    [Fact]
    public void DownloadManager_MapsConnectionCapIntoOptions()
    {
        var settings = new DualNet.Core.Models.AppSettings { MaxConnectionsPerDownload = 12 };
        var manager = new DualNet.Core.Engine.DownloadManager(() => settings);
        var options = manager.OptionsFromSettings();
        Assert.Equal(12, options.MaxSegmentsCap);
        Assert.True(options.AdaptiveScaling);
    }

    [Fact]
    public void DownloadManager_ClampsCrazyConnectionCaps()
    {
        var settings = new DualNet.Core.Models.AppSettings { MaxConnectionsPerDownload = 5000 };
        var manager = new DualNet.Core.Engine.DownloadManager(() => settings);
        Assert.Equal(64, manager.OptionsFromSettings().MaxSegmentsCap);

        settings.MaxConnectionsPerDownload = 0;
        Assert.Equal(4, manager.OptionsFromSettings().MaxSegmentsCap);
    }
}
