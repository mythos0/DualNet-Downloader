using System.Buffers;
using System.Diagnostics;
using System.Net;
using DualNet.Core.Net;

namespace DualNet.Core.Engine;

/// <summary>Result of a per-adapter bandwidth measurement. Never throws: failures set <see cref="Error"/>.</summary>
public sealed record SpeedTestResult(
    string AdapterName,
    string AdapterIp,
    long Bytes,
    double Seconds,
    double MegabitsPerSecond,
    string? Error = null)
{
    public bool Success => Error is null && Bytes > 0;

    public override string ToString()
    {
        if (Error is not null)
            return $"{AdapterName}: failed — {Error}";
        var mb = Bytes / (1024.0 * 1024.0);
        return $"{AdapterName}: {MegabitsPerSecond:0.#} Mbps ({mb:0.0} MB in {Seconds:0.0}s)";
    }
}

/// <summary>
/// Measures real achievable bandwidth through one specific network adapter by streaming a
/// throw-away payload through a socket bound to that adapter's IP.
///
/// Robust by design: bounded by both a payload cap and a wall-clock window, reports
/// progress for the UI, maps network failures to friendly text, and NEVER throws —
/// a broken/unreachable test endpoint must surface as "failed — reason", not a crash.
/// </summary>
public static class SpeedTestService
{
    /// <summary>Cloudflare's public downlink endpoint — reliable, rangeless, CDN-fast.</summary>
    public const string DefaultTestUrl = "https://speed.cloudflare.com/__down";

    /// <summary>How long one adapter measurement may run (keeps the UI snappy on slow links).</summary>
    public const int MaxSeconds = 8;

    /// <summary>Payload cap — generous so fast links fill the whole window.</summary>
    public const long MaxBytes = 64 * 1024 * 1024;

    public static string BuildUrl(string? url, long bytes) =>
        string.IsNullOrWhiteSpace(url)
            ? $"{DefaultTestUrl}?bytes={bytes}&r={Guid.NewGuid():N}"
            : url;

    public static async Task<SpeedTestResult> RunAsync(
        string adapterName, IPAddress bindAddress, string? customUrl, long bytes,
        CancellationToken ct, Action<long>? progress = null)
    {
        if (bytes <= 0 || bytes > MaxBytes)
            bytes = MaxBytes;

        try
        {
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
                        if (progress is not null && (total & 0x3FFFFF) == 0)   // every ~4 MB
                            progress(total);

                        if (total >= bytes)
                            break;
                        if (sw.Elapsed.TotalSeconds >= MaxSeconds)
                            break;   // window over — report what we got
                    }
                    sw.Stop();
                    if (total <= 0)
                        return new SpeedTestResult(adapterName, bindAddress.ToString(), 0, sw.Elapsed.TotalSeconds, 0,
                            "no data received — the endpoint may be blocked on this network");

                    var mbps = total * 8.0 / 1_000_000.0 / Math.Max(0.05, sw.Elapsed.TotalSeconds);
                    return new SpeedTestResult(adapterName, bindAddress.ToString(), total,
                        sw.Elapsed.TotalSeconds, mbps);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SpeedTestResult(adapterName, bindAddress.ToString(), 0, 0, 0, "canceled");
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException se)
        {
            return new SpeedTestResult(adapterName, bindAddress.ToString(), 0, 0, 0,
                se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound => "address not found (DNS)",
                    System.Net.Sockets.SocketError.NetworkUnreachable or
                    System.Net.Sockets.SocketError.HostUnreachable => "this network cannot reach the test endpoint",
                    System.Net.Sockets.SocketError.ConnectionRefused => "connection refused",
                    _ => $"network error: {se.SocketErrorCode}"
                });
        }
        catch (HttpRequestException ex)
        {
            return new SpeedTestResult(adapterName, bindAddress.ToString(), 0, 0, 0,
                ex.StatusCode is { } code ? $"HTTP {(int)code} from the test endpoint" : ex.Message);
        }
        catch (Exception ex)
        {
            return new SpeedTestResult(adapterName, bindAddress.ToString(), 0, 0, 0, ex.Message);
        }
    }
}
