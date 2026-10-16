using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace DualNet.Core.Net;

/// <summary>One free proxy candidate discovered online and (optionally) verified.</summary>
public sealed class ProxyEntry
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    /// <summary>Measured round-trip of the verification probe, in milliseconds.</summary>
    public int LatencyMs { get; set; }
    public string Source { get; set; } = "";
    public string? Country { get; set; }
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;

    public string EndPoint => $"{Host}:{Port}";
    public string LatencyText => LatencyMs <= 0 ? "—" : $"{LatencyMs} ms";
}

/// <summary>
/// Aggregates free HTTP proxies from public sources, verifies them concurrently with a
/// real HTTP probe through each candidate, and caches the working list on disk.
/// Sources and the probe URL are injectable so tests can point the service at local mocks.
/// </summary>
public sealed class ProxyService
{
    /// <summary>Default public sources (plain "ip:port" lines; append "|json" for geonode-style JSON).</summary>
    public static readonly string[] DefaultSources =
    {
        "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=http&timeout=6000&country=all&ssl=all&anonymity=all",
        "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt",
        "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt",
        "https://www.proxy-list.download/api/v1/get?type=http",
        "https://raw.githubusercontent.com/clarketm/proxy-list/master/proxy-list-raw.txt",
        "https://raw.githubusercontent.com/mmpx12/proxy-list/master/http.txt",
        "https://proxylist.geonode.com/api/proxy-list?limit=500&page=1&sort_by=lastChecked&sort_type=desc&protocols=http%2Chttps|json",
        "https://raw.githubusercontent.com/roosterkid/openproxylist/main/HTTPS_RAW.txt",
    };

    private const string DefaultProbeUrl = "http://www.gstatic.com/generate_204";

    private readonly string _cachePath;
    private readonly string[] _sources;
    private readonly string _probeUrl;
    private readonly object _gate = new();
    private List<ProxyEntry> _cache = new();
    private DateTime _cacheTimeUtc = DateTime.MinValue;
    private int _busy;

    /// <summary>How long the verified list stays fresh before a refresh re-probes.</summary>
    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromMinutes(30);

    public int MaxConcurrency { get; set; } = 64;
    public int ProbeTimeoutMs { get; set; } = 6000;
    public int FetchTimeoutMs { get; set; } = 10000;

    public ProxyService(string cachePath, string[]? sources = null, string? probeUrl = null)
    {
        _cachePath = cachePath;
        _sources = sources is { Length: > 0 } ? sources : DefaultSources;
        _probeUrl = probeUrl ?? DefaultProbeUrl;
        LoadCache();
    }

    /// <summary>Last known working proxies (may be empty on first run).</summary>
    public IReadOnlyList<ProxyEntry> Cache
    {
        get { lock (_gate) return _cache.ToList(); }
    }

    public bool CacheFresh => DateTime.UtcNow - _cacheTimeUtc < CacheLifetime;

    public event Action<int, int, int>? Progress;      // (done, total, alive)
    public event Action<string>? Note;

    /// <summary>Working proxies, fastest first. A fresh cache is served when <paramref name="force"/> is false.</summary>
    public async Task<IReadOnlyList<ProxyEntry>> GetWorkingAsync(bool force, CancellationToken ct = default)
    {
        if (!force && CacheFresh && _cache.Count > 0)
            return Cache;

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return Cache;   // a refresh is already running

        try
        {
            EmitNote("Fetching proxy lists…");
            var candidates = await FetchCandidatesAsync(ct).ConfigureAwait(false);
            EmitNote($"Testing {candidates.Count} proxies…");

            var working = new ConcurrentBag<ProxyEntry>();
            int done = 0;
            using var throttler = new SemaphoreSlim(Math.Max(1, MaxConcurrency));
            var tasks = candidates.Select(async c =>
            {
                try
                {
                    await throttler.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var latency = await ProbeAsync(c, ct).ConfigureAwait(false);
                        if (latency is not null)
                        {
                            c.LatencyMs = latency.Value;
                            c.CheckedUtc = DateTime.UtcNow;
                            working.Add(c);
                        }
                    }
                    finally { throttler.Release(); }
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    var d = Interlocked.Increment(ref done);
                    if (d % 25 == 0)
                        EmitProgress(d, candidates.Count, working.Count);
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);

            var list = working
                .OrderBy(p => p.LatencyMs)
                .Take(500)
                .ToList();

            lock (_gate)
            {
                _cache = list;
                _cacheTimeUtc = DateTime.UtcNow;
            }
            SaveCache();
            EmitProgress(candidates.Count, candidates.Count, list.Count);
            EmitNote($"Done — {list.Count} working of {candidates.Count} tested.");
            return list;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Tests a single host:port proxy and returns its latency, or null when dead.</summary>
    public async Task<int?> TestAsync(string host, int port, CancellationToken ct = default)
    {
        return await ProbeAsync(new ProxyEntry { Host = host.Trim(), Port = port }, ct).ConfigureAwait(false);
    }

    /// <summary>True when the candidate served a successful HTTP probe within the timeout.</summary>
    public async Task<int?> ProbeAsync(ProxyEntry candidate, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeoutMs);
            using var client = CreateProbeClient(candidate);
            using var request = new HttpRequestMessage(HttpMethod.Get, _probeUrl);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();
            int status = (int)response.StatusCode;
            return status is >= 200 and < 400 ? (int)sw.ElapsedMilliseconds : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;    // probe timeout — dead proxy
        }
        catch
        {
            return null;
        }
    }

    private HttpClient CreateProbeClient(ProxyEntry candidate)
    {
        return new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(candidate.Host, Math.Clamp(candidate.Port, 1, 65535)),
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(ProbeTimeoutMs),
            PooledConnectionLifetime = TimeSpan.FromSeconds(10),
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromMilliseconds(ProbeTimeoutMs),
        };
    }

    private async Task<List<ProxyEntry>> FetchCandidatesAsync(CancellationToken ct)
    {
        var set = new Dictionary<string, ProxyEntry>(StringComparer.Ordinal);
        using var fetcher = new HttpClient { Timeout = TimeSpan.FromMilliseconds(FetchTimeoutMs) };
        fetcher.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 DualNet/4.0");

        foreach (var source in _sources)
        {
            if (ct.IsCancellationRequested) break;
            var (url, isJson) = SplitSource(source);
            try
            {
                using var response = await fetcher.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var parsed = isJson ? ParseGeonodeJson(body, source) : ParsePlainList(body, source);
                foreach (var entry in parsed)
                    set.TryAdd(entry.Host + ":" + entry.Port, entry);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* source offline — keep going */ }
        }
        return set.Values.ToList();
    }

    internal static (string Url, bool IsJson) SplitSource(string source)
    {
        const string marker = "|json";
        return source.EndsWith(marker, StringComparison.Ordinal)
            ? (source[..^marker.Length], true)
            : (source, false);
    }

    internal static List<ProxyEntry> ParsePlainList(string body, string source)
    {
        var result = new List<ProxyEntry>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int slash = line.IndexOf('/');
            if (slash > 0) line = line[..slash];
            int colon = line.LastIndexOf(':');
            if (colon <= 0 || colon == line.Length - 1) continue;
            var host = line[..colon].Trim();
            if (!int.TryParse(line[(colon + 1)..].Trim(), out var port)) continue;
            if (port is < 1 or > 65535) continue;
            if (host.Length < 3) continue;
            result.Add(new ProxyEntry { Host = host, Port = port, Source = SourceLabel(source) });
        }
        return result;
    }

    internal static List<ProxyEntry> ParseGeonodeJson(string body, string source)
    {
        var result = new List<ProxyEntry>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("ip", out var ipEl)) continue;
                    if (!item.TryGetProperty("port", out var portEl)) continue;
                    var host = ipEl.GetString();
                    if (string.IsNullOrWhiteSpace(host)) continue;
                    int port = portEl.ValueKind == JsonValueKind.String
                        ? (int.TryParse(portEl.GetString(), out var p) ? p : 0)
                        : (portEl.TryGetInt32(out var p2) ? p2 : 0);
                    if (port is < 1 or > 65535) continue;
                    string? country = item.TryGetProperty("country", out var cEl) && cEl.ValueKind == JsonValueKind.String
                        ? cEl.GetString() : null;
                    result.Add(new ProxyEntry { Host = host.Trim(), Port = port, Source = SourceLabel(source), Country = country });
                }
            }
        }
        catch { }
        return result;
    }

    internal static string SourceLabel(string url)
    {
        try
        {
            var host = new Uri(url.Split('|')[0]).Host;
            return host.Replace("api.", "").Replace("www.", "").Replace("raw.githubusercontent.com", "github");
        }
        catch { return "online"; }
    }

    // ---- cache -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private void EmitProgress(int done, int total, int alive)
    {
        try { Progress?.Invoke(done, total, alive); } catch { }
        try { Note?.Invoke($"{alive} working · {done}/{total} tested"); } catch { }
    }

    private void EmitNote(string message)
    {
        try { Note?.Invoke(message); } catch { }
    }

    private void SaveCache()
    {
        try
        {
            lock (_gate)
            {
                var dir = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var payload = new CachePayload { SavedUtc = _cacheTimeUtc, Proxies = _cache };
                File.WriteAllText(_cachePath, JsonSerializer.Serialize(payload, JsonOpts));
            }
        }
        catch { }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var payload = JsonSerializer.Deserialize<CachePayload>(File.ReadAllText(_cachePath));
            if (payload is null) return;
            lock (_gate)
            {
                _cache = payload.Proxies ?? new List<ProxyEntry>();
                _cacheTimeUtc = payload.SavedUtc;
            }
        }
        catch { }
    }

    private sealed class CachePayload
    {
        public DateTime SavedUtc { get; set; }
        public List<ProxyEntry> Proxies { get; set; } = new();
    }
}
