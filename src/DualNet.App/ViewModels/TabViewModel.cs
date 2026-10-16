using System.ComponentModel;
using DualNet.App.Browser;

namespace DualNet.App.ViewModels;

public enum TabContentKind { Web, Internal }

/// <summary>One browser tab: either real Chromium content or an internal DualNet page.</summary>
public sealed class TabViewModel : INotifyPropertyChanged
{
    public int Id { get; init; }
    public TabContentKind Kind { get; set; } = TabContentKind.Internal;
    /// <summary>dualnet://app/&lt;page&gt; when Kind == Internal ("newtab", "downloads", …).</summary>
    public string InternalPage { get; set; } = "newtab";

    /// <summary>v4.1.1 — populated when the Chromium engine could not be created for this tab
    /// (shown on the engine-error internal page instead of failing silently).</summary>
    public string EngineError { get; set; } = "";

    private string _title = "New Tab";
    public string Title { get => _title; set { _title = value; Changed(nameof(Title)); Changed(nameof(DisplayTitle)); } }

    private string _url = "dualnet://app/newtab";
    public string Url { get => _url; set { _url = value; Changed(nameof(Url)); Changed(nameof(OmniboxText)); Changed(nameof(IsSecure)); } }

    /// <summary>What the omnibox shows (full URL, or internal-page pseudonym).</summary>
    public string OmniboxText => Kind == TabContentKind.Internal && Url.StartsWith("dualnet://", StringComparison.Ordinal)
        ? Url.Replace("dualnet://app/", "dualnet://")
        : Url;

    public bool IsSecure => Kind == TabContentKind.Web && Url.StartsWith("https://", StringComparison.Ordinal);

    public string DisplayTitle
    {
        get
        {
            if (Kind == TabContentKind.Internal)
            {
                return InternalPage switch
                {
                    "newtab" => "New Tab",
                    "downloads" => "Downloads",
                    "history" => "History",
                    "bookmarks" => "Bookmarks",
                    "settings" => "Settings",
                    "extensions" => "Extensions",
                    "agent" => "AI Agent",
                    "about" => "About",
                    "engine-error" => "Browser engine unavailable",
                    _ => InternalPage,
                };
            }
            var t = Title.Trim();
            return t.Length > 0 ? t : Url;
        }
    }

    private string _faviconUrl = "";
    public string FaviconUrl { get => _faviconUrl; set { _faviconUrl = value; Changed(nameof(FaviconUrl)); } }

    private bool _loading;
    public bool Loading { get => _loading; set { _loading = value; Changed(nameof(Loading)); } }

    private bool _canBack;
    public bool CanBack { get => _canBack; set { _canBack = value; Changed(nameof(CanBack)); } }

    private bool _canFwd;
    public bool CanFwd { get => _canFwd; set { _canFwd = value; Changed(nameof(CanFwd)); } }

    private bool _isPinned;
    public bool IsPinned { get => _isPinned; set { _isPinned = value; Changed(nameof(IsPinned)); } }

    private bool _isActive;
    public bool IsActive { get => _isActive; set { _isActive = value; Changed(nameof(IsActive)); } }

    private bool _isMuted;
    public bool IsMuted { get => _isMuted; set { _isMuted = value; Changed(nameof(IsMuted)); } }

    private bool _isPrivate;
    public bool IsPrivate { get => _isPrivate; set { _isPrivate = value; Changed(nameof(IsPrivate)); } }

    /// <summary>Proxy endpoint "host:port" for this tab; empty = direct connection.</summary>
    private string _proxy = "";
    public string Proxy { get => _proxy; set { _proxy = value; Changed(nameof(Proxy)); Changed(nameof(ProxyLabel)); Changed(nameof(ProxyActive)); } }

    public bool ProxyActive => !string.IsNullOrWhiteSpace(Proxy);
    public string ProxyLabel => ProxyActive ? Proxy : "Direct";

    /// <summary>Download NIC scope for this tab: "all" (combined) or a specific adapter id.</summary>
    private string _netScope = "all";
    public string NetScope { get => _netScope; set { _netScope = value; Changed(nameof(NetScope)); Changed(nameof(NetLabel)); } }

    public string NetLabel => NetScope == "all" ? "Combined" : NetScope;

    /// <summary>Browser host — created lazily on first WEB navigation.</summary>
    public CefTabHost? Host { get; set; }

    /// <summary>Cached internal page view (kept alive while the tab exists).</summary>
    public object? InternalView { get; set; }

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public bool IsWeb => Kind == TabContentKind.Web;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void RefreshDisplay() { Changed(nameof(DisplayTitle)); Changed(nameof(OmniboxText)); Changed(nameof(IsSecure)); }
}
