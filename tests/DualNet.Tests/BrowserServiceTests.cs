using System.Net;
using System.Text;
using DualNet.Core.Engine.Browser;
using DualNet.Core.Storage;
using Xunit;

namespace DualNet.Tests;

/// <summary>Tiny HTTP server that serves configurable HTML/file responses for browser tests.</summary>
public sealed class HtmlTestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>path (starts with "/") → responder.</summary>
    public Dictionary<string, Func<HttpListenerResponse, Task>> Routes { get; } = new();

    public string BaseUrl { get; private set; } = "";

    public void Start()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        var prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        BaseUrl = prefix.TrimEnd('/');
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public static Func<HttpListenerResponse, Task> Html(string body, int status = 200) => async res =>
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = "text/html; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.OutputStream.Close();
    };

    public static Func<HttpListenerResponse, Task> File(string fileName, string contentType, long size = 1024) => async res =>
    {
        res.StatusCode = 200;
        res.ContentType = contentType;
        res.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
        res.ContentLength64 = size;
        await res.OutputStream.WriteAsync(new byte[size], 0, (int)size);
        res.OutputStream.Close();
    };

    public static Func<HttpListenerResponse, Task> Status(int status) => res =>
    {
        res.StatusCode = status;
        res.ContentLength64 = 0;
        res.Close();
        return Task.CompletedTask;
    };

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }

            _ = Task.Run(async () =>
            {
                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    if (Routes.TryGetValue(path, out var responder))
                        await responder(ctx.Response);
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            }, ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        _cts.Dispose();
    }
}

public class BrowserServiceTests
{
    private const string PageHtml = """
        <html>
          <head><title>DualNet Test Page</title></head>
          <body>
            <script>var hidden = "script text must not appear";</script>
            <style>.hidden { color: red }</style>
            <h1>Welcome Heading</h1>
            <p>This is the first paragraph with some words.</p>
            <div>
              <a href="/files/setup.exe">Download the installer</a>
              <a href="https://mirror.example.net/other/page.html">A relative page link</a>
              <a href="mailto:someone@example.com">mail</a>
              <a href="#anchor">fragment</a>
            </div>
            <ul><li>First bullet item</li><li>Second bullet item</li></ul>
            <img src="/img/logo.png" alt="Logo">
            <hr>
          </body>
        </html>
        """;

    [Fact]
    public async Task Fetch_HtmlPage_ParsesTitleBlocksAndLinks()
    {
        using var server = new HtmlTestServer();
        server.Routes["/page"] = HtmlTestServer.Html(PageHtml);
        server.Start();

        var result = await BrowserService.FetchAsync($"{server.BaseUrl}/page",
            new[] { IPAddress.Loopback }, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.Page);
        var page = result.Page!;

        Assert.Equal("DualNet Test Page", page.Title);

        // Blocks: heading + paragraph + bullets + image + divider
        Assert.Contains(page.Blocks, b => b is HeadingBlock h && h.Level == 1 && h.Text.Contains("Welcome Heading"));
        Assert.Contains(page.Blocks, b => b is ParagraphBlock p && p.Text.Contains("first paragraph"));
        Assert.Contains(page.Blocks, b => b is BulletBlock li && li.Text.Contains("First bullet item"));
        Assert.Contains(page.Blocks, b => b is ImageBlock img && img.Url.EndsWith("/img/logo.png"));
        Assert.Contains(page.Blocks, b => b is DividerBlock);

        // Script/style text must not leak into any block.
        Assert.DoesNotContain(page.Blocks, b =>
            (b is ParagraphBlock pp && pp.Text.Contains("script text")) ||
            (b is HeadingBlock hh && hh.Text.Contains("script text")));

        // Links: absolute resolution + skip mailto/fragment + file classification.
        Assert.Contains(page.Links, l => l.Url == $"{server.BaseUrl}/files/setup.exe" && l.IsFile);
        Assert.Contains(page.Links, l => l.Url == "https://mirror.example.net/other/page.html" && !l.IsFile);
        Assert.DoesNotContain(page.Links, l => l.Url.StartsWith("mailto:"));
        Assert.All(page.Links, l => Assert.True(l.Url.StartsWith("http")));
    }

    [Fact]
    public async Task Fetch_NonHtmlContentDisposition_IsDownload()
    {
        using var server = new HtmlTestServer();
        server.Routes["/setup.exe"] = HtmlTestServer.File("setup-1.2.exe", "application/octet-stream", 4096);
        server.Start();

        var result = await BrowserService.FetchAsync($"{server.BaseUrl}/setup.exe",
            new[] { IPAddress.Loopback }, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.IsDownload);
        Assert.Equal("setup-1.2.exe", result.DownloadFileName);
        Assert.Equal(4096, result.DownloadSize);
    }

    [Fact]
    public async Task Fetch_404_ReturnsFriendlyError_NotThrow()
    {
        using var server = new HtmlTestServer();
        server.Routes["/missing"] = HtmlTestServer.Status(404);
        server.Start();

        var result = await BrowserService.FetchAsync($"{server.BaseUrl}/missing",
            new[] { IPAddress.Loopback }, CancellationToken.None);

        Assert.Null(result.Page);
        Assert.False(result.IsDownload);
        Assert.Contains("404", result.Error);
    }

    [Fact]
    public async Task Fetch_NoBinds_FriendlyError()
    {
        var result = await BrowserService.FetchAsync("https://example.com/x",
            Array.Empty<IPAddress>(), CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Contains("No network adapter", result.Error);
    }

    [Fact]
    public async Task Fetch_InvalidUrl_FriendlyError()
    {
        var result = await BrowserService.FetchAsync("not a url at all",
            new[] { IPAddress.Loopback }, CancellationToken.None);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Fetch_ConnectionRefused_FriendlyError()
    {
        // Grab a free port and close it — guaranteed refused connection, fast failure.
        int port;
        using (var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        }

        var result = BrowserService.FetchAsync($"http://127.0.0.1:{port}/x",
            new[] { IPAddress.Loopback }, CancellationToken.None).GetAwaiter().GetResult();

        Assert.NotNull(result.Error);
        Assert.Null(result.Page);
    }

    [Theory]
    [InlineData("https://cdn.example.com/app/setup.exe", true)]
    [InlineData("https://cdn.example.com/app/setup.exe?sig=abc&x=1", true)]
    [InlineData("https://example.com/movie.mkv", true)]
    [InlineData("https://example.com/docs/readme.MD", false)]           // not in the list → page
    [InlineData("https://example.com/docs/report.PDF", true)]           // case-insensitive
    [InlineData("https://example.com/page.html", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://example.com/noextension", false)]
    [InlineData("https://example.com/archive.tar.gz", true)]
    public void LinkClassifier_DetectsFileUrls(string url, bool expected)
    {
        Assert.Equal(expected, LinkClassifier.IsFileLink(url));
    }

    [Theory]
    [InlineData(0, "duckduckgo.com")]
    [InlineData(1, "lite.duckduckgo.com")]
    [InlineData(2, "www.bing.com")]
    [InlineData(3, "www.google.com")]
    [InlineData(4, "search.brave.com")]
    public void SearchEngines_BuildUrls(int engine, string expectedHost)
    {
        var url = SearchEngines.BuildUrl(engine, "best download manager");
        Assert.StartsWith("https://", url);
        Assert.Contains(expectedHost, url);
        Assert.Contains("best%20download%20manager", url);
    }

    [Fact]
    public void SearchEngines_Default_IsDuckDuckGo()
    {
        // v4.0.0: DuckDuckGo is the built-in default search engine.
        Assert.Equal(0, SearchEngines.DefaultEngine);
        Assert.StartsWith("https://duckduckgo.com", SearchEngines.BuildUrl(SearchEngines.DefaultEngine, "x"));
        Assert.Equal("DuckDuckGo", SearchEngines.Names[0]);
    }

    [Fact]
    public void ResolveInput_RoutesUrlsAndSearches()
    {
        Assert.Equal("https://example.com/file.zip", BrowserService.ResolveInput("example.com/file.zip", 0));
        Assert.Equal("https://example.com/", BrowserService.ResolveInput("https://example.com/", 0));
        Assert.StartsWith("https://duckduckgo.com", BrowserService.ResolveInput("two words query", 0));
        Assert.StartsWith("https://duckduckgo.com", BrowserService.ResolveInput("nodots", 0));
    }

    // ---- browser store -----------------------------------------------------------------------

    [Fact]
    public void BrowserStore_BookmarksToggleAndRemove()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dualnet-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BrowserStore(dir);
            Assert.False(store.IsBookmarked("https://example.com/a"));

            Assert.True(store.ToggleBookmark("https://example.com/a", "Page A"));
            Assert.True(store.IsBookmarked("https://example.com/a"));
            Assert.Single(store.Bookmarks);
            Assert.Equal("Page A", store.Bookmarks[0].Title);

            Assert.False(store.ToggleBookmark("https://example.com/a", "Page A"));   // toggled off
            Assert.Empty(store.Bookmarks);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void BrowserStore_VisitedHistory_IsRecencyOrdered_AndPersisted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dualnet-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BrowserStore(dir);
            store.AddVisited("https://example.com/first", "First");
            store.AddVisited("https://example.com/second", "Second");
            store.AddVisited("https://example.com/first", "First again");   // dedupe → moved to top

            var visited = store.Visited;
            Assert.Equal(2, visited.Count);
            Assert.Equal("https://example.com/first", visited[0].Url);
            Assert.Equal("https://example.com/second", visited[1].Url);

            // A new instance reads the same files (persistence round-trip).
            var store2 = new BrowserStore(dir);
            Assert.Equal(2, store2.Visited.Count);

            store2.ClearVisited();
            Assert.Empty(store2.Visited);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- headless browser view-model ----------------------------------------------------------

}
