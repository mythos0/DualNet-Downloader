namespace DualNet.Core.Engine;

/// <summary>
/// Process-wide download speed cap shared by every segment of every job.
/// 0 = unlimited. <see cref="DownloadJob"/> consults this before writing each buffer,
/// so the combined throughput of all network adapters stays under the user's limit.
/// </summary>
public static class GlobalSpeedLimit
{
    private static readonly object Sync = new();
    private static long _limitBytesPerSecond;   // 0 = unlimited
    private static long _windowStartMs;
    private static long _grantedThisWindow;

    /// <summary>Current cap in bytes/second (0 = unlimited).</summary>
    public static long LimitBytesPerSecond => Interlocked.Read(ref _limitBytesPerSecond);

    /// <summary>Bytes granted inside the current 1-second accounting window (diagnostics/tests).</summary>
    public static long GrantedThisWindow => Interlocked.Read(ref _grantedThisWindow);

    /// <summary>Sets the process-wide cap in bytes/second (0 = unlimited).</summary>
    public static void SetLimit(long bytesPerSecond) =>
        Interlocked.Exchange(ref _limitBytesPerSecond, Math.Max(0, bytesPerSecond));

    /// <summary>
    /// Grants up to <paramref name="bytes"/> immediately under the current cap.
    /// Returns how many bytes the caller may write right now (0 when this second's
    /// budget is exhausted; unlimited when the cap is 0).
    /// </summary>
    public static int TryTake(int bytes)
    {
        if (bytes <= 0)
            return 0;

        var limit = Interlocked.Read(ref _limitBytesPerSecond);
        if (limit <= 0)
            return bytes;   // unlimited

        lock (Sync)
        {
            var now = Environment.TickCount64;
            if (now - _windowStartMs >= 1000)
            {
                _windowStartMs = now;
                _grantedThisWindow = 0;
            }

            var budget = limit - _grantedThisWindow;
            if (budget <= 0)
                return 0;

            var granted = (int)Math.Min((long)bytes, budget);
            _grantedThisWindow += granted;
            return granted;
        }
    }

    /// <summary>Waits (in ~60 ms slices) until <paramref name="bytes"/> fit under the cap.</summary>
    public static async ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var take = TryTake(bytes);
            if (take >= bytes)
                return;
            bytes -= take;
            await Task.Delay(60, ct).ConfigureAwait(false);
        }
    }

    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _windowStartMs = Environment.TickCount64;
            _grantedThisWindow = 0;
        }
        SetLimit(0);
    }
}
