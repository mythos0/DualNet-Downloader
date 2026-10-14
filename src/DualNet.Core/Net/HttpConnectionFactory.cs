using System.Net;
using System.Net.Sockets;

namespace DualNet.Core.Net;

/// <summary>
/// Creates <see cref="HttpClient"/> instances whose TCP connections are force-bound to a
/// specific local adapter IP address. This is what makes a download actually flow through
/// the Ethernet cable or the Wi-Fi radio instead of the OS picking a single route.
/// </summary>
public static class HttpConnectionFactory
{
    public const string UserAgent = "DualNetDownloader/1.4 (+dual-network segment downloader)";

    private static volatile IWebProxy? _proxyOverride;

    /// <summary>
    /// Configures the proxy used by every download connection.
    /// mode: 0 = direct, 1 = system proxy, 2 = manual host:port.
    /// </summary>
    public static void ConfigureProxy(int mode, string host, int port)
    {
        switch (mode)
        {
            case 1:
                _proxyOverride = HttpClient.DefaultProxy;
                break;
            case 2:
                _proxyOverride = string.IsNullOrWhiteSpace(host)
                    ? null
                    : new WebProxy(host.Trim(), Math.Clamp(port, 1, 65535));
                break;
            default:
                _proxyOverride = null;
                break;
        }
    }

    public static HttpClient Create(IPAddress bindAddress)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            Proxy = _proxyOverride,
            UseProxy = _proxyOverride is not null,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            // Segment workers pull from the per-adapter pool in parallel; make the
            // headroom explicit (default is int.MaxValue but be defensive across runtimes).
            MaxConnectionsPerServer = 64,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                int port = context.DnsEndPoint.Port;

                IPAddress target;
                if (IPAddress.TryParse(host, out var literal))
                {
                    target = literal;
                }
                else
                {
                    var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                    // Prefer an address whose family matches the adapter we bind to.
                    target = addresses.FirstOrDefault(a => a.AddressFamily == bindAddress.AddressFamily)
                             ?? addresses.FirstOrDefault()
                             ?? throw new SocketException((int)SocketError.HostNotFound);
                }

                var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    // Bind locally so the OS routes traffic through this adapter.
                    if (bindAddress.AddressFamily == target.AddressFamily)
                        socket.Bind(new IPEndPoint(bindAddress, 0));

                    await socket.ConnectAsync(target, port, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        var client = new HttpClient(handler, disposeHandler: true);
        client.Timeout = Timeout.InfiniteTimeSpan;   // streaming downloads; watchdogs handle stalls
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public static IReadOnlyList<HttpClient> CreateAll(IEnumerable<IPAddress> bindAddresses)
    {
        var clients = new List<HttpClient>();
        foreach (var ip in bindAddresses)
            clients.Add(Create(ip));
        return clients;
    }
}
