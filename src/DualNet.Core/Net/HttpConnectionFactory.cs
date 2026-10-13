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
    public const string UserAgent = "DualNetDownloader/1.0 (+dual-network segment downloader)";

    public static HttpClient Create(IPAddress bindAddress)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
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
