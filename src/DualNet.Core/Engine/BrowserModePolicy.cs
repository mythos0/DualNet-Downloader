namespace DualNet.Core.Engine;

/// <summary>
/// Decides which browsing engine a tab uses. Pure + testable: the real browser
/// (Chromium via WebView2) requires the Windows platform, the Evergreen WebView2
/// runtime, and the user setting; anything else keeps the built-in reader view.
/// </summary>
public static class BrowserModePolicy
{
    public enum BrowserMode { Real, Reader }

    /// <param name="settingEnabled">Settings.UseRealBrowser.</param>
    /// <param name="osWindows">Running on Windows (WebView2 is Windows-only).</param>
    /// <param name="runtimeAvailable">WebView2 runtime detected / initialization succeeded.</param>
    public static BrowserMode Decide(bool settingEnabled, bool osWindows, bool runtimeAvailable) =>
        settingEnabled && osWindows && runtimeAvailable ? BrowserMode.Real : BrowserMode.Reader;
}
