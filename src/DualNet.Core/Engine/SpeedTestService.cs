using System.Buffers;
using System.Diagnostics;
using System.Net;
using DualNet.Core.Net;

namespace DualNet.Core.Engine;

/// <summary>Result of a per-adapter bandwidth measurement.</summary>
public sealed record SpeedTestResult(
    string AdapterName,
    string AdapterIp,
    long Bytes,
    double Seconds,
    double MegabitsPerSecond)
{
    public override string ToString()
    {
        var mb = Bytes / (1024.0 * 1024.0);
        return $"{AdapterName}: {MegabitsPerSecond:0.#} Mbps ({mb:0.0} MB in {Seconds:0.0}s)";
    }
}

/// <summary>
/// Measures real achievable bandwidth through one specific network adapter by
/// streaming a throw-away payload through a socket bound to that adapter's IP.
/// </summary>
public static class SpeedTestService
{
    /// <summary>Cloudflare's public downlink endpoint — reliable, rangeless, CDN-fast.</summary>
    public const string DefaultTestUrl = "https://speed.cloudflare.com/__down";

    public static string BuildUrl(string? url, long bytes) =>
        string.IsNullOrWhiteSpace(url)
            ? $"{DefaultTestUrl}?bytes={bytes}&r={Guid.NewGuid():N}"
            : url;

    public static async Task<SpeedTestResult> RunAsync(
        string adapterName, IPAddress bindAddress, string? customUrl, long bytes, CancellationToken ct)
    {
        if (bytes <= 0) bytes = 8 * 1024 * 1024;

        using var client = HttpConnectionFactory.Create(bindAddress);
        client.Timeout = Timeout.InfiniteTimeSpan;

        var url = BuildUrl(customUrl, bytes);
        var sw = Stopwatch.StartNew();

        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                           .ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total >= bytes)
                        break;
                }
                sw.Stop();
                if (total <= 0)
                    throw new IOException("Speed test received no data.");

                var mbps = total * 8.0 / 1_000_000.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                return new SpeedTestResult(adapterName, bindAddress.ToString(), total,
                    sw.Elapsed.TotalSeconds, mbps);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
