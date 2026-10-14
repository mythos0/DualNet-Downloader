namespace DualNet.Core.Storage;

/// <summary>A bookmarked page.</summary>
public sealed class BookmarkEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One visited page (browser history).</summary>
public sealed class VisitedEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime VisitedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persistent browser data for one app instance: bookmarks and page-visit history.
/// Both are small JSON files in the app-data directory, capped to stay lightweight.
/// </summary>
public sealed class BrowserStore
{
    private const int MaxBookmarks = 300;
    private const int MaxVisited = 500;

    private readonly string _bookmarkPath;
    private readonly string _visitedPath;
    private readonly object _sync = new();
    private List<BookmarkEntry> _bookmarks;
    private List<VisitedEntry> _visited;

    public BrowserStore(string appDataDir)
    {
        Directory.CreateDirectory(appDataDir);
        _bookmarkPath = Path.Combine(appDataDir, "bookmarks.json");
        _visitedPath = Path.Combine(appDataDir, "browser-history.json");
        _bookmarks = JsonStore.Load<List<BookmarkEntry>>(_bookmarkPath) ?? new();
        _visited = JsonStore.Load<List<VisitedEntry>>(_visitedPath) ?? new();
    }

    // ---- bookmarks ---------------------------------------------------------------------------

    public IReadOnlyList<BookmarkEntry> Bookmarks
    {
        get { lock (_sync) return _bookmarks.ToList(); }
    }

    public bool IsBookmarked(string url)
    {
        lock (_sync) return _bookmarks.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Toggles the bookmark; returns true when the page is now bookmarked.</summary>
    public bool ToggleBookmark(string url, string title)
    {
        bool added;
        lock (_sync)
        {
            var existing = _bookmarks.FindIndex(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                _bookmarks.RemoveAt(existing);
                added = false;
            }
            else
            {
                _bookmarks.Insert(0, new BookmarkEntry { Url = url, Title = title, AddedUtc = DateTime.UtcNow });
                if (_bookmarks.Count > MaxBookmarks)
                    _bookmarks.RemoveAt(_bookmarks.Count - 1);
                added = true;
            }
            JsonStore.Save(_bookmarkPath, _bookmarks);
        }
        return added;
    }

    public bool RemoveBookmark(string url)
    {
        lock (_sync)
        {
            var removed = _bookmarks.RemoveAll(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                JsonStore.Save(_bookmarkPath, _bookmarks);
            return removed > 0;
        }
    }

    // ---- visited pages -----------------------------------------------------------------------

    public IReadOnlyList<VisitedEntry> Visited
    {
        get { lock (_sync) return _visited.ToList(); }
    }

    public void AddVisited(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return;
        lock (_sync)
        {
            _visited.RemoveAll(v => string.Equals(v.Url, url, StringComparison.OrdinalIgnoreCase));
            _visited.Insert(0, new VisitedEntry { Url = url, Title = title, VisitedUtc = DateTime.UtcNow });
            if (_visited.Count > MaxVisited)
                _visited.RemoveRange(MaxVisited, _visited.Count - MaxVisited);
            JsonStore.Save(_visitedPath, _visited);
        }
    }

    public void ClearVisited()
    {
        lock (_sync)
        {
            _visited.Clear();
            JsonStore.Save(_visitedPath, _visited);
        }
    }
}
