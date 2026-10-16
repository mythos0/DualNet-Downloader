using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DualNet.Core.Engine;
using DualNet.Core.Engine.Browser;
using DualNet.Core.Models;
using DualNet.Core.Net;
using DualNet.Core.Storage;
using Xunit;

namespace DualNet.Tests;

/// <summary>
/// v4.0.0 browser-era Core tests: session restore, bookmark import/export, the online
/// free-proxy service (against local mock sources) and the per-download proxy/header
/// plumbing of the download engine — all verifiable on Linux without CEF.
/// </summary>
public class BrowserV4Tests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dualnet-v4", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] RandomData(int size)
    {
        var data = new byte[size];
        new Random(7).NextBytes(data);
        return data;
    }

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- session store ------------------------------------------------------------------------

    [Fact]
    public void SessionStore_RoundTripsTabs()
    {
        var dir = NewTempDir();
        var store = new SessionStore(dir);

        var state = new SessionState
        {
            ActiveIndex = 1,
            Tabs =
            {
                new SessionTab { Url = "https://duckduckgo.com/?q=dualnet", Title = "DuckDuckGo", Pinned = true },
                new SessionTab { Url = "https://example.com/file.zip", Title = "File", Proxy = "127.0.0.1:8888", NetScope = "wifi-9" },
                new SessionTab { Url = "dualnet://app/newtab", Title = "New Tab", Private = true, InternalPage = "newtab" },
            }
        };
        store.Save(state);

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Tabs.Count);
        Assert.Equal(1, loaded.ActiveIndex);
        Assert.True(loaded.Tabs[0].Pinned);
        Assert.Equal("127.0.0.1:8888", loaded.Tabs[1].Proxy);
        Assert.Equal("wifi-9", loaded.Tabs[1].NetScope);
        Assert.True(loaded.Tabs[2].Private);
    }

    [Fact]
    public void SessionStore_MissingFile_ReturnsNull()
    {
        var store = new SessionStore(NewTempDir());
        Assert.Null(store.Load());
    }

    [Fact]
    public void SessionStore_ClosedTabStack_PushesAndPopsInOrder()
    {
        var store = new SessionStore(NewTempDir());
        store.PushClosedTab(new SessionTab { Url = "https://a.example/" });
        store.PushClosedTab(new SessionTab { Url = "https://b.example/" });
        store.PushClosedTab(new SessionTab { Url = "https://c.example/" });

        Assert.Equal("https://c.example/", store.PopClosedTab()!.Url);
        Assert.Equal("https://b.example/", store.PopClosedTab()!.Url);
        Assert.Equal("https://a.example/", store.PopClosedTab()!.Url);
        Assert.Null(store.PopClosedTab());
    }

    // ---- bookmarks import/export --------------------------------------------------------------

    [Fact]
    public void BrowserStore_BookmarkExportImport_RoundTripsAndDedupes()
    {
        var dir = NewTempDir();
        var store = new BrowserStore(dir);
        store.ToggleBookmark("https://duckduckgo.com/", "DuckDuckGo");
        store.ToggleBookmark("https://example.com/page", "Example");

        var exportPath = Path.Combine(dir, "bookmarks.html");
        int exported = store.ExportBookmarksHtml(exportPath);
        Assert.Equal(2, exported);
        var html = File.ReadAllText(exportPath);
        Assert.Contains("NETSCAPE-Bookmark-file-1", html);
        Assert.Contains("https://duckduckgo.com/", html);

        // Import into a FRESH store (simulates another browser / another machine).
        var fresh = new BrowserStore(NewTempDir());
        int imported = fresh.ImportBookmarksHtml(exportPath);
        Assert.Equal(2, imported);
        Assert.True(fresh.IsBookmarked("https://example.com/page"));

        // Re-importing must not duplicate entries.
        int second = fresh.ImportBookmarksHtml(exportPath);
        Assert.Equal(0, second);
        Assert.Equal(2, fresh.Bookmarks.Count);
    }

    // ---- search engine default ----------------------------------------------------------------

    [Fact]
    public void ResolveInput_NonUrl_BecomesDuckDuckGoSearch()
    {
        // Non-URL input is a search query in a real browser — DDG is the default engine.
        Assert.StartsWith("https://duckduckgo.com", BrowserService.ResolveInput("two words query", 0));
        Assert.StartsWith("https://example.com/file.zip", BrowserService.ResolveInput("example.com/file.zip", 0));
    }

    // ---- proxy service ------------------------------------------------------------------------

    [Fact]
    public void ProxyService_ParsesPlainListsAndFiltersGarbage()
    {
        var body = """
            1.2.3.4:8080
            5.6.7.8:3128
            # comment line
            not-a-proxy
            9.9.9.9:
            10.10.10.10:99999
            11.11.11.11:80/path/extra
            """;
        var entries = ProxyService.ParsePlainList(body, "https://example.org/list.txt");
        Assert.Equal(3, entries.Count);
        Assert.Equal("1.2.3.4", entries[0].Host);
        Assert.Equal(8080, entries[0].Port);
        Assert.Equal("example.org", entries[0].Source);
        Assert.Equal(80, entries[2].Port);  // trailing path stripped
    }

    [Fact]
    public void ProxyService_ParsesGeonodeJson()
    {
        const string json = """
            {"data":[
              {"ip":"20.30.40.50","port":8080,"country":"DE"},
              {"ip":"20.30.40.51","port":"3128","country":"US"},
              {"ip":"20.30.40.52","port":70000},
              {"ip":"","port":80}
            ],"total":4}
            """;
        var entries = ProxyService.ParseGeonodeJson(json, "https://proxylist.geonode.com/api/proxy-list|json");
        Assert.Equal(2, entries.Count);
        Assert.Equal(8080, entries[0].Port);
        Assert.Equal("DE", entries[0].Country);
        Assert.Equal(3128, entries[1].Port);   // string port tolerated
    }

    [Fact]
    public void ProxyService_SplitSource_MarksJsonSources()
    {
        var (url, isJson) = ProxyService.SplitSource("https://x.y/api?limit=5|json");
        Assert.True(isJson);
        Assert.EndsWith("limit=5", url);
        var (plain, json2) = ProxyService.SplitSource("https://x.y/list.txt");
        Assert.False(json2);
        Assert.Equal("https://x.y/list.txt", plain);
    }

    [Fact]
    [Trait("Category", "TimingSensitive")]
    public async Task ProxyService_FindsWorkingProxy_ThroughLocalForwardProxy()
    {
        var data = RandomData(64 * 1024);
        using var probeServer = new RangeTestServer(data);
        probeServer.Start();

        using var proxy = new LocalForwardProxy();
        proxy.Start(ParsePort(probeServer.BaseUrl));

        using var source = new MockSourceServer($"{IPAddress.Loopback}:{proxy.Port}");
        source.Start();

        var service = new ProxyService(
            Path.Combine(NewTempDir(), "proxies.json"),
            sources: new[] { source.Url },
            probeUrl: $"{probeServer.BaseUrl.TrimEnd('/')}/probe")
        { ProbeTimeoutMs = 4000, FetchTimeoutMs = 5000, MaxConcurrency = 4 };

        var working = await service.GetWorkingAsync(force: true);
        Assert.Single(working);
        Assert.Equal(IPAddress.Loopback.ToString(), working[0].Host);
        Assert.Equal(proxy.Port, working[0].Port);
        Assert.True(working[0].LatencyMs >= 0);
        Assert.Equal(1, proxy.ForwardedCount);      // the probe really went through the proxy
        Assert.True(service.CacheFresh);

        // A second non-forced call must serve the fresh cache without re-probing.
        var cached = await service.GetWorkingAsync(force: false);
        Assert.Equal(1, proxy.ForwardedCount);
        Assert.Single(cached);
    }

    [Fact]
    [Trait("Category", "TimingSensitive")]
    public async Task ProxyService_DeadProxies_AreExcluded()
    {
        var data = RandomData(1024);
        using var probeServer = new RangeTestServer(data);
        probeServer.Start();

        // One dead endpoint (nobody listens) + one working proxy.
        int deadPort = TcpFreePort();
        using var proxy = new LocalForwardProxy();
        proxy.Start(ParsePort(probeServer.BaseUrl));

        using var source = new MockSourceServer($"127.0.0.1:{deadPort}", $"{IPAddress.Loopback}:{proxy.Port}");
        source.Start();

        var service = new ProxyService(
            Path.Combine(NewTempDir(), "proxies.json"),
            sources: new[] { source.Url },
            probeUrl: $"{probeServer.BaseUrl.TrimEnd('/')}/probe")
        { ProbeTimeoutMs = 3000, FetchTimeoutMs = 5000, MaxConcurrency = 4 };

        var working = await service.GetWorkingAsync(force: true);
        Assert.Single(working);
        Assert.Equal(proxy.Port, working[0].Port);
    }

    // ---- per-download proxy + headers ----------------------------------------------------------

    [Fact]
    public async Task DownloadJob_WithPerDownloadProxy_TunnelsThroughProxy()
    {
        var data = RandomData(4 * 1024 * 1024);
        using var server = new RangeTestServer(data);
        server.Start();
        using var proxy = new LocalForwardProxy();
        proxy.Start(ParsePort(server.BaseUrl));

        var dir = NewTempDir();
        var options = new DownloadOptions
        {
            SegmentsSmall = 4,
            SegmentsLarge = 8,
            MinSegmentSizeBytes = 256 * 1024,
            RetryCount = 3,
            ProxyHost = IPAddress.Loopback.ToString(),
            ProxyPort = proxy.Port,
            AdaptiveScaling = false,
        };
        var binds = new[] { IPAddress.Loopback };
        var job = new DownloadJob(server.BaseUrl, binds, new DownloadScope("All", new List<string>()), dir, options);
        job.Start();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));

        Assert.True(job.Status == DownloadStatus.Completed,
            $"status={job.Status} msg='{job.StatusMessage}'");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Sha256File(job.FilePath));
        Assert.True(proxy.ForwardedCount >= 1, "the download never touched the per-download proxy");
    }

    [Fact]
    public async Task DownloadJob_ExtraHeaders_AreSentOnRequests()
    {
        using var echo = new HeaderEchoServer();
        echo.Start();

        var data = RandomData(512 * 1024);
        using var server = new RangeTestServer(data);
        server.Start();

        var dir = NewTempDir();
        var options = new DownloadOptions
        {
            SegmentsSmall = 2,
            SegmentsLarge = 2,
            MinSegmentSizeBytes = 128 * 1024,
            RetryCount = 2,
            AdaptiveScaling = false,
            ExtraHeaders = new Dictionary<string, string>
            {
                ["Cookie"] = "sid=abcdef; theme=dark",
                ["Referer"] = "https://duckduckgo.com/",
                ["X-DualNet-Test"] = "yes",
            }
        };
        var job = new DownloadJob(echo.ProxyUrl, new[] { IPAddress.Loopback },
            new DownloadScope("All", new List<string>()), dir, options);
        job.Start();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(job.Status == DownloadStatus.Completed, $"status={job.Status} msg='{job.StatusMessage}'");
        Assert.Contains("sid=abcdef", echo.LastCookie);
        Assert.Contains("duckduckgo.com", echo.LastReferer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("yes", echo.LastCustom);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static int ParsePort(string url) => new Uri(url).Port;

    private static int TcpFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>
/// Minimal HTTP/1.1 forward proxy for loopback tests: forwards absolute-form GETs
/// (as sent by HttpClient with a WebProxy) to the origin server in origin-form.
/// </summary>
public sealed class LocalForwardProxy : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _forwarded;

    public int Port { get; private set; }
    public int ForwardedCount => Volatile.Read(ref _forwarded);
    public string LastTarget => _lastTarget;
    private string _lastTarget = "";

    public void Start(int targetPortHint = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream, ct);
            if (head.Length == 0) return;

            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2 || !Uri.TryCreate(requestLine[1], UriKind.Absolute, out var target))
                return;

            Interlocked.Increment(ref _forwarded);
            _lastTarget = target.Host + ":" + target.Port;

            using var origin = new TcpClient();
            await origin.ConnectAsync(target.Host, target.Port, ct);
            var originStream = origin.GetStream();

            // Rewrite to origin-form and force close so the pump ends deterministically.
            var sb = new StringBuilder();
            sb.Append(requestLine[0]).Append(' ').Append(target.PathAndQuery).Append(" HTTP/1.1\r\n");
            bool hadHost = false;
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                if (line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) { hadHost = true; }
                sb.Append(line).Append("\r\n");
            }
            if (!hadHost) sb.Append("Host: ").Append(target.Authority).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");

            var requestBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await originStream.WriteAsync(requestBytes, ct);
            await originStream.FlushAsync(ct);

            // Pump origin -> client until EOF.
            var buffer = new byte[64 * 1024];
            try
            {
                int read;
                while ((read = await originStream.ReadAsync(buffer, ct)) > 0)
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                await stream.FlushAsync(ct);
            }
            catch { /* socket torn down mid-pump is fine */ }
        }
        catch { /* malformed or abandoned client */ }
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n")) break;
            if (sb.Length > 64 * 1024) break;
        }
        var head = sb.ToString();
        int idx = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return idx < 0 ? head : head[..idx];
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
    }
}

/// <summary>Serves a fixed "ip:port per line" body — a mock online proxy list source.</summary>
public sealed class MockSourceServer : IDisposable
{
    private TcpListener? _listener;
    private readonly string[] _entries;

    public string Url { get; private set; } = "";

    public MockSourceServer(params string[] entries) => _entries = entries;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://127.0.0.1:{port}/list.txt";
        var body = Encoding.ASCII.GetBytes(string.Join("\n", _entries) + "\n");
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nConnection: close\r\n" +
            $"Content-Length: {body.Length}\r\n\r\n");
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var s = client.GetStream();
                            await s.WriteAsync(header);
                            await s.WriteAsync(body);
                            await s.FlushAsync();
                            client.Close();
                        }
                        catch { }
                    });
                }
            }
            catch { }
        });
    }

    public void Dispose()
    {
        try { _listener?.Stop(); } catch { }
    }
}

/// <summary>
/// Echoes selected request headers back as JSON — used to prove the engine really
/// sends browser-context headers (Cookie / Referer / custom).
/// </summary>
public sealed class HeaderEchoServer : IDisposable
{
    private TcpListener? _listener;

    public int Port { get; private set; }
    public string ProxyUrl => $"http://127.0.0.1:{Port}/echo.bin";
    public string LastCookie => _lastCookie;
    public string LastReferer => _lastReferer;
    public string LastCustom => _lastCustom;
    private string _lastCookie = "";
    private string _lastReferer = "";
    private string _lastCustom = "";

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var s = client.GetStream();
                            var head = await ReadHeadAsync(s);
                            foreach (var line in head.Split("\r\n"))
                            {
                                var colon = line.IndexOf(':');
                                if (colon <= 0) continue;
                                var name = line[..colon].Trim();
                                var value = line[(colon + 1)..].Trim();
                                if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) _lastCookie = value;
                                else if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase)) _lastReferer = value;
                                else if (name.Equals("X-DualNet-Test", StringComparison.OrdinalIgnoreCase)) _lastCustom = value;
                            }
                            var body = Encoding.ASCII.GetBytes(new string('D', 256 * 1024));
                            var header = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                                "Accept-Ranges: none\r\nConnection: close\r\n" +
                                $"Content-Length: {body.Length}\r\n\r\n");
                            await s.WriteAsync(header);
                            await s.WriteAsync(body);
                            await s.FlushAsync();
                            client.Close();
                        }
                        catch { }
                    });
                }
            }
            catch { }
        });
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n")) break;
        }
        var head = sb.ToString();
        int idx = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return idx < 0 ? head : head[..idx];
    }

    public void Dispose()
    {
        try { _listener?.Stop(); } catch { }
    }
}
