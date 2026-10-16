using System.Text.Json;

namespace DualNet.Core.Storage;

/// <summary>One persisted tab of a browser session (crash recovery / session restore).</summary>
public sealed class SessionTab
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Pinned { get; set; }
    public bool Muted { get; set; }
    public bool Private { get; set; }
    /// <summary>Proxy endpoint "host:port" bound to this tab, or null/empty for direct.</summary>
    public string? Proxy { get; set; }
    /// <summary>Download NIC scope for this tab: "all" (combined) or an adapter id.</summary>
    public string NetScope { get; set; } = "all";
    /// <summary>When true the tab showed an internal page (dualnet://app/...) — restore to newtab if the page is transient.</summary>
    public string? InternalPage { get; set; }
}

/// <summary>A whole browser session snapshot.</summary>
public sealed class SessionState
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists the open-tab session to JSON so the browser reopens where the user left off,
/// including a "recently closed" stack for Ctrl+Shift+T.
/// </summary>
public sealed class SessionStore
{
    private readonly string _path;
    private readonly string _closedPath;
    private readonly object _gate = new();

    public SessionStore(string appDataDir)
    {
        Directory.CreateDirectory(appDataDir);
        _path = Path.Combine(appDataDir, "session.json");
        _closedPath = Path.Combine(appDataDir, "closed-tabs.json");
    }

    public void Save(SessionState state)
    {
        try
        {
            state.SavedUtc = DateTime.UtcNow;
            lock (_gate)
                File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch { }
    }

    public SessionState? Load()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path)) return null;
                return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(_path));
            }
        }
        catch { return null; }
    }

    public void Clear()
    {
        try { lock (_gate) File.Delete(_path); } catch { }
    }

    /// <summary>Pushes a closed tab (URL + settings) so it can be reopened. Keeps the newest 25.</summary>
    public void PushClosedTab(SessionTab tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Url)) return;
        try
        {
            List<SessionTab> stack;
            lock (_gate)
            {
                stack = LoadClosed();
                stack.Insert(0, tab);
                stack = stack.Take(25).ToList();
                File.WriteAllText(_closedPath, JsonSerializer.Serialize(stack, JsonOpts));
            }
        }
        catch { }
    }

    /// <summary>Pops the most recently closed tab, or null when the stack is empty.</summary>
    public SessionTab? PopClosedTab()
    {
        try
        {
            lock (_gate)
            {
                var stack = LoadClosed();
                if (stack.Count == 0) return null;
                var tab = stack[0];
                stack.RemoveAt(0);
                File.WriteAllText(_closedPath, JsonSerializer.Serialize(stack, JsonOpts));
                return tab;
            }
        }
        catch { return null; }
    }

    public IReadOnlyList<SessionTab> ClosedTabs
    {
        get { try { lock (_gate) return LoadClosed(); } catch { return Array.Empty<SessionTab>(); } }
    }

    private List<SessionTab> LoadClosed()
    {
        if (!File.Exists(_closedPath)) return new List<SessionTab>();
        return JsonSerializer.Deserialize<List<SessionTab>>(File.ReadAllText(_closedPath)) ?? new List<SessionTab>();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
}
