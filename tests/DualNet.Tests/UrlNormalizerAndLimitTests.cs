using DualNet.Core.Engine;
using Xunit;

namespace DualNet.Tests;

public class UrlNormalizerTests
{
    [Theory]
    [InlineData("example.com/file.zip", "https://example.com/file.zip")]
    [InlineData("  example.com/file.zip  ", "https://example.com/file.zip")]
    [InlineData("//cdn.example.com/a.bin", "https://cdn.example.com/a.bin")]
    [InlineData("http://192.168.1.10:8080/f.iso", "http://192.168.1.10:8080/f.iso")]
    [InlineData("https://server.io/dir/file.tar.gz?x=1", "https://server.io/dir/file.tar.gz?x=1")]
    [InlineData("localhost:5000/big.bin", "https://localhost:5000/big.bin")]
    public void Normalize_AddsSchemeAndTrims(string input, string expected)
    {
        Assert.Equal(expected, UrlNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("just some words")]
    [InlineData("https://")]
    [InlineData("no-dot-host/f.zip")]
    public void Normalize_RejectsGarbage(string? input)
    {
        Assert.Null(UrlNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_KeepsNonHttpSchemes_ForCallerToReject()
    {
        Assert.Equal("ftp://files.example.com/x.zip", UrlNormalizer.Normalize("ftp://files.example.com/x.zip"));
    }
}

public class GlobalSpeedLimitTests
{
    public GlobalSpeedLimitTests() => GlobalSpeedLimit.ResetForTests();

    [Fact]
    public void Unlimited_ByDefault_PassesEverythingThrough()
    {
        Assert.Equal(0, GlobalSpeedLimit.LimitBytesPerSecond);
        Assert.Equal(64 * 1024, GlobalSpeedLimit.TryTake(64 * 1024));
    }

    [Fact]
    public void TryTake_RationsBytes_InsideOneWindow()
    {
        GlobalSpeedLimit.SetLimit(1000);
        Assert.Equal(600, GlobalSpeedLimit.TryTake(600));
        Assert.Equal(400, GlobalSpeedLimit.TryTake(600));   // only 400 left this second
        Assert.Equal(0, GlobalSpeedLimit.TryTake(100));     // budget exhausted
        Assert.Equal(1000, GlobalSpeedLimit.GrantedThisWindow);
    }

    [Fact]
    public async Task TryTake_Resets_AfterTheSecondElapses()
    {
        GlobalSpeedLimit.SetLimit(1000);
        Assert.Equal(1000, GlobalSpeedLimit.TryTake(1000));
        Assert.Equal(0, GlobalSpeedLimit.TryTake(100));

        await Task.Delay(1100);

        // Poll briefly — CI schedulers can push the first probe just before the
        // 1-second window actually flips (TryTake(0) has no side effects).
        var deadline = DateTime.UtcNow.AddSeconds(3);
        int took;
        while ((took = GlobalSpeedLimit.TryTake(100)) != 100 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(100, took);
    }

    [Fact]
    public async Task AcquireAsync_CompletesQuickly_WhenUnlimited()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await GlobalSpeedLimit.AcquireAsync(1024 * 1024, CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 200, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AcquireAsync_SpreadsTransfer_WhenCapped()
    {
        // 240 KB at 60 KB/s must take at least ~3 windows (>= ~2 s; give slack for CI).
        GlobalSpeedLimit.SetLimit(60 * 1024);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await GlobalSpeedLimit.AcquireAsync(240 * 1024, CancellationToken.None);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 1200,
            $"transfer finished too fast for the cap: {sw.ElapsedMilliseconds} ms");
        GlobalSpeedLimit.SetLimit(0);
    }
}


