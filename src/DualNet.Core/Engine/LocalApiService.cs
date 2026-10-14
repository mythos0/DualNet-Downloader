using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace DualNet.Core.Engine;

/// <summary>
/// Tiny localhost HTTP endpoint (raw TcpListener — no URL-ACL requirements) that lets other
/// apps, scripts, and browser integrations push download URLs into the running instance:
///
///     GET /add?url=https://...&amp;scope=all      -> 200 ok
///     GET /ping                                  -> 200 pong
///
/// Doubles as the single-instance IPC channel: a second launch forwards its command-line
/// URLs here and exits.
/// </summary>
public sealed class LocalApiService : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LocalApiService(int port) => Port = port;

    public int Port { get; }

    /// <summary>The actual bound port (differs from Port when constructed with 0 = pick free).</summary>
    public int BoundPort { get; private set; }

    public bool IsRunning => _loop is not null && !_loop.IsCompleted;
    public event Action<string>? UrlReceived;

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (IOException) { return; }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? "";
                // Drain headers until blank line.
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct).ConfigureAwait(false)))
                {
                }

                var path = requestLine.Split(' ', 3) is { Length: >= 2 } parts ? parts[1] : "/";
                var (route, ok) = Route(path);
                var body = ok ? "ok" : "bad request";
                var status = ok ? 200 : 400;
                var payload = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\n" +
                    "Content-Type: text/plain\r\n" +
                    $"Content-Length: {Encoding.ASCII.GetByteCount(body)}\r\n" +
                    "Connection: close\r\n\r\n" + body);
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
                GC.KeepAlive(route);
            }
        }
        catch { /* best-effort local API */ }
    }

    private (string? url, bool ok) Route(string rawPath)
    {
        var qIndex = rawPath.IndexOf('?');
        var route = qIndex >= 0 ? rawPath[..qIndex] : rawPath;
        var query = qIndex >= 0 ? rawPath[(qIndex + 1)..] : "";

        if (route == "/ping")
            return (null, true);

        if (route == "/add")
        {
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "url")
                {
                    var url = HttpUtility.UrlDecode(kv[1]);
                    if (UrlNormalizer.Normalize(url) is { } normalized &&
                        normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        UrlReceived?.Invoke(normalized);
                        return (normalized, true);
                    }
                    return (null, false);
                }
            }
        }
        return (null, false);
    }

    /// <summary>Used by a second app launch to forward URLs to the running instance.</summary>
    public static async Task<bool> TrySendToRunningInstance(int port, string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            var text = await client.GetStringAsync(
                $"http://127.0.0.1:{port}/add?url={Uri.EscapeDataString(url)}").ConfigureAwait(false);
            return text.Trim() == "ok";
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _cts?.Dispose(); } catch { }
    }
}
