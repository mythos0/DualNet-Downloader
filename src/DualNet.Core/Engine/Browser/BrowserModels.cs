namespace DualNet.Core.Engine.Browser;

/// <summary>One link found on a web page (absolute URL).</summary>
public sealed record BrowserLink(string Url, string Text, bool IsFile)
{
    public string Host
    {
        get
        {
            try { return new Uri(Url).Host; } catch { return ""; }
        }
    }

    /// <summary>File name when the link points at a downloadable file, else "".</summary>
    public string FileName => IsFile ? FileNameGuesser.FromUrl(Url) : "";

    /// <summary>Best single-line label for UI: file name, link text, or the path itself.</summary>
    public string DisplayText
    {
        get
        {
            if (IsFile && FileName.Length > 0)
                return FileName;
            if (!string.IsNullOrWhiteSpace(Text))
                return Text;
            try
            {
                var uri = new Uri(Url);
                var path = uri.AbsolutePath.TrimEnd('/');
                var last = path.Split('/').LastOrDefault();
                return string.IsNullOrEmpty(last) ? uri.Host : Uri.UnescapeDataString(last);
            }
            catch
            {
                return Url;
            }
        }
    }
}

/// <summary>A rendered block of a web page (reader view).</summary>
public abstract record PageBlock;

/// <summary>Heading (h1-h3). Level 1 is the biggest.</summary>
public sealed record HeadingBlock(int Level, string Text) : PageBlock;

/// <summary>Paragraph of text plus the links it contains (rendered as click chips).</summary>
public sealed record ParagraphBlock(string Text, IReadOnlyList<BrowserLink> Links) : PageBlock;

/// <summary>List item bullet.</summary>
public sealed record BulletBlock(string Text) : PageBlock;

/// <summary>Quoted text.</summary>
public sealed record QuoteBlock(string Text) : PageBlock;

/// <summary>Preformatted / code text.</summary>
public sealed record PreBlock(string Text) : PageBlock;

/// <summary>An image on the page (absolute URL).</summary>
public sealed record ImageBlock(string Url, string Alt) : PageBlock;

/// <summary>Horizontal rule.</summary>
public sealed record DividerBlock : PageBlock;

/// <summary>A fully parsed page, ready for the reader-view renderer.</summary>
public sealed record BrowserPage(
    string Url,
    string Title,
    IReadOnlyList<PageBlock> Blocks,
    IReadOnlyList<BrowserLink> Links);

/// <summary>Result of fetching a URL through one tab's adapter-bound pipeline.</summary>
public sealed class BrowserResult
{
    public string RequestedUrl { get; init; } = "";
    public string FinalUrl { get; init; } = "";
    public BrowserPage? Page { get; init; }
    public bool IsDownload { get; init; }
    public string? DownloadFileName { get; init; }
    public long? DownloadSize { get; init; }
    public string? Error { get; init; }
    public int StatusCode { get; init; }

    public bool IsSuccess => Page is not null || IsDownload;
}

/// <summary>
/// Search engines usable from the address bar. DuckDuckGo is the default
/// (index 0) — v4.0.0 made it the built-in default search engine.
/// </summary>
public static class SearchEngines
{
    public static readonly string[] Names = { "DuckDuckGo", "DuckDuckGo Lite", "Bing", "Google", "Brave" };

    public const int DefaultEngine = 0;

    public static string BuildUrl(int engine, string query)
    {
        var q = Uri.EscapeDataString(query.Trim());
        return engine switch
        {
            1 => $"https://lite.duckduckgo.com/lite/?q={q}",
            2 => $"https://www.bing.com/search?q={q}",
            3 => $"https://www.google.com/search?q={q}&num=20",
            4 => $"https://search.brave.com/search?q={q}",
            _ => $"https://duckduckgo.com/?q={q}"
        };
    }
}

/// <summary>Decides whether a URL points at a downloadable file rather than a page.</summary>
public static class LinkClassifier
{
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst",
        ".tar", ".tgz", ".iso", ".img", ".dmg", ".pkg", ".deb", ".rpm", ".apk", ".appimage",
        ".bin", ".jar", ".whl", ".nupkg", ".cab",
        ".pdf", ".epub", ".mobi", ".djvu",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".csv", ".rtf",
        ".mp3", ".flac", ".ogg", ".wav", ".wma", ".m4a", ".opus", ".aac",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpeg", ".ts",
        ".psd", ".ai", ".xcf", ".svgz",
        ".ttf", ".otf", ".woff", ".woff2",
        ".torrent", ".patch", ".diff", ".sql", ".db", ".sqlite"
    };

    /// <summary>True when the URL's path ends with a known file extension.</summary>
    public static bool IsFileLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        try
        {
            var path = new Uri(url).AbsolutePath;
            var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is null)
                return false;
            var dot = last.LastIndexOf('.');
            if (dot <= 0 || dot == last.Length - 1)
                return false;
            return FileExtensions.Contains(last[dot..]);
        }
        catch
        {
            return false;
        }
    }
}
