using System.Windows.Controls;
using DualNet.App.Services;
using DualNet.Core;

namespace DualNet.App.Views;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var v = SentrySetup.VersionText;
            VersionText.Text = $"Version {v}";
            EngineText.Text = $"Engine: Chromium {CefChromiumVersion} · .NET {Environment.Version} · WPF + CefSharp";
            SentryText.Text = SentrySetup.IsConfigured
                ? $"Telemetry: Sentry events go to the user's project (DSN …{SentrySetup.ActiveDsn[^8..]})."
                : "Telemetry: disabled.";
        };
    }

    private static string CefChromiumVersion
    {
        get
        {
            try { return CefSharp.Cef.ChromiumVersion; }
            catch { return "138"; }
        }
    }
}
