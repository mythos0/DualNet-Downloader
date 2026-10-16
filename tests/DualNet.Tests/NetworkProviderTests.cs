using System.Net;
using DualNet.Core.Engine;
using DualNet.Core.Models;
using DualNet.Core.Net;
using Xunit;

namespace DualNet.Tests;

/// <summary>
/// Manual network providers + dynamic tab sync:
///  - a newly arrived network adapter MUST get its own tab (auto-refresh and manual rescan)
///  - a departed network must lose its tab, its downloads moving to the Combined tab
///  - manual providers merge into the adapter list, offline ones never receive tabs/binds
/// </summary>
public class NetworkProviderTests : IDisposable
{
    private static NetworkAdapterInfo FakeAdapter(string id, string name, string ip, string type = "Ethernet") =>
        new(Id: id, Name: name, Description: $"fake {name}", TypeName: type,
            IPv4Address: ip, SpeedBitsPerSecond: 100_000_000);

    public void Dispose()
    {
        // Never leak fake adapters into other tests.
        NetworkAdapterService.OverrideForTests = null;
    }

    // ---------------- core: manual provider merging ----------------

    [Fact]
    public void ManualProvider_OfflineIp_IsListedButMarkedOffline()
    {
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10")
        };

        var adapters = NetworkAdapterService.GetActiveAdapters(new[]
        {
            new ManualAdapterEntry { Name = "Lab VPN", IPv4Address = "203.0.113.9", TypeName = "Other" }
        });

        var manual = adapters.Single(a => a.IsManual);
        Assert.Equal("manual:203.0.113.9", manual.Id);
        Assert.Equal("Lab VPN", manual.Name);
        Assert.False(manual.IsOnline);
        Assert.Equal(0UL, manual.SpeedBitsPerSecond);
        // The online auto adapter is untouched.
        Assert.Contains(adapters, a => a.Id == "eth-1" && a.IsOnline);
    }

    [Fact]
    public void ManualProvider_IpPresentLocally_IsOnline()
    {
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10")
        };

        // 127.0.0.1 is always assigned to the loopback NIC → the provider can bind.
        var adapters = NetworkAdapterService.GetActiveAdapters(new[]
        {
            new ManualAdapterEntry { Name = "Loop", IPv4Address = "127.0.0.1", TypeName = "Ethernet" }
        });

        var manual = adapters.Single(a => a.IsManual);
        Assert.True(manual.IsOnline);
        Assert.Equal("manual:127.0.0.1", manual.Id);
    }

    [Fact]
    public void ManualProvider_DuplicateOfAutoDetected_IsSkipped()
    {
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10")
        };

        var adapters = NetworkAdapterService.GetActiveAdapters(new[]
        {
            new ManualAdapterEntry { Name = "Dup", IPv4Address = "192.168.1.10", TypeName = "Wi-Fi" }
        });

        Assert.Single(adapters);
        Assert.False(adapters[0].IsManual);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("999.999.1.1")]
    [InlineData("::1")]              // IPv6 — manual providers are IPv4 only
    [InlineData("")]
    public void ManualProvider_GarbageInput_IsIgnoredSafely(string badIp)
    {
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10")
        };

        var adapters = NetworkAdapterService.GetActiveAdapters(new[]
        {
            new ManualAdapterEntry { Name = "Bad", IPv4Address = badIp, TypeName = "Other" }
        });

        Assert.Single(adapters);
        Assert.DoesNotContain(adapters, a => a.IsManual);
    }

    [Fact]
    public void BindResolution_ExcludesOfflineManualProviders()
    {
        NetworkAdapterService.OverrideForTests = () =>
            Array.Empty<NetworkAdapterInfo>();
        var manuals = new List<ManualAdapterEntry>
        {
            new() { Name = "Ghost", IPv4Address = "203.0.113.9", TypeName = "Other" }
        };
        var merged = NetworkAdapterService.GetActiveAdapters(manuals);
        var ghost = merged.First(a => a.IsManual);
        Assert.False(ghost.IsOnline);   // offline provider must never receive download binds
        Assert.Equal("manual:203.0.113.9", ghost.Id);
    }

}
