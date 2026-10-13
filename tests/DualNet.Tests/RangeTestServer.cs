using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;

namespace DualNet.Tests;

/// <summary>
/// A minimal HTTP server with real byte-range support for exercising the download engine.
/// Can throttle per-chunk, omit range support, and change its ETag to test resume revalidation.
/// </summary>
public sealed class RangeTestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public byte[] Data { get; set; }
    public string ETag { get; set; }
    public bool ServeWithoutRanges { get; set; }
    public int ChunkDelayMs { get; set; }          // artificial latency per 64KB chunk
    public int FailFirstRequests;                  // simulate flaky server: abort N requests
    public bool IgnoreRangeOnSegmentRequests;      // 206 for the 0-0 probe, 200 for real ranges
    public bool OmitContentRange;                  // 206 but without the Content-Range header
    public bool IgnoreRangeOnResume;               // 200 for range requests with start > 0
    public bool StallRequests;                     // accept connections but never respond
    public int FailFirstSegmentRequests;           // 503 the first N range requests with start > 0
    private int _servedRequests;
    public IReadOnlyCollection<string> SeenRemoteAddresses => _seenRemoteAddresses.ToList();
    private readonly ConcurrentBag<string> _seenRemoteAddresses = new();

    /// <summary>Per-request trace: [n] remote=… range=… -> status=… bytes=…</summary>
    public ConcurrentQueue<string> RequestLog { get; } = new();

    public string BaseUrl { get; private set; } = "";
    public string FileName { get; }

    public RangeTestServer(byte[] data, string fileName = "payload.bin")
    {
        Data = data;
        FileName = fileName;
        ETag = "\"test-etag-v1\"";
    }

    public void Start()
    {
        // pick a free port
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        BaseUrl = $"{prefix}{FileName}";
        _loop = Task.Run(() => ListenLoop(_cts.Token));
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }

            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var ct = _cts.Token;
        try
        {
            Interlocked.Increment(ref _servedRequests);
            _seenRemoteAddresses.Add(ctx.Request.RemoteEndPoint?.Address.ToString() ?? "?");

            if (FailFirstRequests > 0)
            {
                Interlocked.Decrement(ref FailFirstRequests);
                var abortRange = ctx.Request.Headers["Range"];
                RequestLog.Enqueue($"#{_servedRequests} remote={ctx.Request.RemoteEndPoint} range={abortRange} -> 503");
                try
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.ContentLength64 = 0;
                    ctx.Response.Close();
                }
                catch { }
                return;
            }

            var response = ctx.Response;
            response.Headers["ETag"] = ETag;
            response.Headers["Accept-Ranges"] = ServeWithoutRanges ? "none" : "bytes";
            response.SendChunked = false;
            response.ContentType = "application/octet-stream";

            long total = Data.Length;

            // Only honor ranges when explicitly enabled.
            string? rangeHeader = ServeWithoutRanges ? null : ctx.Request.Headers["Range"];

            long rangeStart = -1;
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var spec = HttpUtility.UrlDecode(rangeHeader["bytes=".Length..]);
                var dash = spec.IndexOf('-');
                rangeStart = long.Parse(spec[..dash]);
            }

            if (StallRequests)
            {
                RequestLog.Enqueue($"#{_servedRequests} remote={ctx.Request.RemoteEndPoint} range={rangeHeader} -> STALL");
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    try { ctx.Response.Abort(); } catch { }
                }
                return;
            }

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var spec = HttpUtility.UrlDecode(rangeHeader["bytes=".Length..]);
                var dash = spec.IndexOf('-');
                long start = long.Parse(spec[..dash]);
                long end = dash == spec.Length - 1 ? total - 1 : Math.Min(long.Parse(spec[(dash + 1)..]), total - 1);

                // Fault injection: segments (start > 0) misbehave, probe (0-0) still succeeds.
                if (start > 0 && (IgnoreRangeOnSegmentRequests || IgnoreRangeOnResume))
                    rangeHeader = null;   // fall through to the 200 full-body branch
                else if (start > 0 && FailFirstSegmentRequests > 0)
                {
                    Interlocked.Decrement(ref FailFirstSegmentRequests);
                    RequestLog.Enqueue($"#{_servedRequests} remote={ctx.Request.RemoteEndPoint} range={rangeHeader} -> 503 (segment fault)");
                    try
                    {
                        ctx.Response.StatusCode = 503;
                        ctx.Response.ContentLength64 = 0;
                        ctx.Response.Close();
                    }
                    catch { }
                    return;
                }

                if (rangeHeader is not null && start <= end)
                {
                    bool etagMatches = ctx.Request.Headers["If-Range"] is null ||
                                       ctx.Request.Headers["If-Range"] == ETag;

                    if (etagMatches)
                    {
                        long count = end - start + 1;
                        response.StatusCode = 206;
                        if (!OmitContentRange)
                            response.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
                        response.ContentLength64 = count;
                        RequestLog.Enqueue($"#{_servedRequests} remote={ctx.Request.RemoteEndPoint} range={rangeHeader} -> 206 bytes={count}");
                        using var body = response.OutputStream;
                        CopySlice(start, count, body, ct);
                        return;
                    }
                }
                // If-Range mismatch or bad range -> fall through to 200 full body (standard behavior)
            }

            response.StatusCode = 200;
            response.ContentLength64 = total;
            response.ContentType = "application/octet-stream";
            if (ServeWithoutRanges)
                response.Headers.Remove("Accept-Ranges");

            RequestLog.Enqueue($"#{_servedRequests} remote={ctx.Request.RemoteEndPoint} range={rangeHeader} -> 200 bytes={total}");
            using (var body = response.OutputStream)
                CopySlice(0, total, body, ct);
        }
        catch (Exception)
        {
            try { ctx.Response.Abort(); } catch { }
        }
        return;
    }

    private void CopySlice(long start, long count, Stream output, CancellationToken ct)
    {
        const int ChunkSize = 64 * 1024;
        var buffer = new byte[ChunkSize];
        long offset = start, remaining = count;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int len = (int)Math.Min(ChunkSize, remaining);
            Buffer.BlockCopy(Data, (int)offset, buffer, 0, len);
            output.Write(buffer, 0, len);
            output.Flush();
            offset += len;
            remaining -= len;
            if (ChunkDelayMs > 0)
                Thread.Sleep(ChunkDelayMs);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        try { _loop?.Wait(500); } catch { }
        _cts.Dispose();
    }
}
