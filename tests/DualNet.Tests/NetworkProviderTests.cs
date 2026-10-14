using System.Net;
using Avalonia.Headless.XUnit;
using DualNet.App;
using DualNet.App.ViewModels;
using DualNet.App.Views;
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
        // Never leak fake adapters or manual providers into other tests (or settings.json).
        NetworkAdapterService.OverrideForTests = null;
        if (AppServices.Settings is not null)
        {
            AppServices.Settings.ManualAdapters.Clear();
            AppServices.SaveSettings();   // wipe anything a test persisted
        }
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
        AppServices.Settings.ManualAdapters.Add(
            new ManualAdapterEntry { Name = "Ghost", IPv4Address = "203.0.113.9", TypeName = "Other" });

        var all = AppServices.AllAdapterIPs();
        Assert.Empty(all);   // offline provider must never receive download binds

        var scoped = AppServices.ResolveBindsForTab("manual:203.0.113.9",
            new[] { "manual:203.0.113.9" });
        Assert.Empty(scoped);
    }

    // ---------------- UI: dynamic tab sync ----------------

    private static MainWindow CreateWindow()
    {
        AppServices.Initialize();          // idempotent
        return new MainWindow { DataContext = new MainViewModel() };
    }

    [AvaloniaFact]
    public void NewNetworkArriving_GetsItsOwnTab_Automatically()
    {
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10")
        };
        var window = CreateWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);
        int tabsBefore = vm.Tabs.Count;

        // A new Wi-Fi network appears (cable plugged / joined SSID).
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10"),
            FakeAdapter("wifi-9", "Wi-Fi 2.4G", "192.168.2.77", "Wi-Fi")
        };
        vm.RefreshNetworksCommand.Execute(null);

        var wifiTab = vm.Tabs.FirstOrDefault(t => t.Kind == TabKind.SingleAdapter &&
                                                  t.ScopeLabel == "wifi-9");
        Assert.NotNull(wifiTab);                       // ← the reported bug: tab did not arrive
        Assert.Equal(vm.Tabs.Count, tabsBefore + 1);
        Assert.Equal(vm.HistoryTab, vm.Tabs[^1]);      // history stays last
        Assert.True(vm.Tabs.IndexOf(wifiTab!) < vm.Tabs.IndexOf(vm.HistoryTab));
    }

    [AvaloniaFact]
    public void NetworkDisappearing_LosesTab_DownloadsMoveToCombined()
    {
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10")
        };
        var window = CreateWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);

        var ethTab = vm.Tabs.First(t => t.ScopeLabel == "eth-1");
        ethTab.AddRestoredJob(new DownloadJob(
            "https://example.com/migrate-me.bin",
            Array.Empty<IPAddress>(),
            new DownloadScope("eth-1", new List<string> { "eth-1" }),
            Path.GetTempPath(),
            new DownloadOptions()));
        Assert.NotEmpty(ethTab.Items);

        // The adapter goes away (unplug / disconnect).
        NetworkAdapterService.OverrideForTests = () => Array.Empty<NetworkAdapterInfo>();
        vm.RefreshNetworksCommand.Execute(null);

        Assert.DoesNotContain(vm.Tabs, t => t.ScopeLabel == "eth-1");
        var combined = vm.Tabs.First(t => t.Kind == TabKind.Combined);
        Assert.Contains(combined.Items, i => i.Url == "https://example.com/migrate-me.bin");
        Assert.Equal(vm.HistoryTab, vm.Tabs[^1]);
    }

    [AvaloniaFact]
    public void SelectedTab_IsPreserved_AcrossRescans()
    {
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10"),
            FakeAdapter("wifi-9", "Wi-Fi 2.4G", "192.168.2.77", "Wi-Fi")
        };
        var window = CreateWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);

        var ethTab = vm.Tabs.First(t => t.ScopeLabel == "eth-1");
        vm.SelectedTabIndex = vm.Tabs.IndexOf(ethTab);

        vm.RefreshNetworksCommand.Execute(null);   // same set — nothing changes
        Assert.Equal(ethTab, vm.SelectedTab);
    }

    [AvaloniaFact]
    public void ManualProvider_Lifecycle_WiresChipsTabsAndBinds()
    {
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10")
        };
        var window = CreateWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);
        int tabsBefore = vm.Tabs.Count;

        // OFFLINE manual provider: chip appears, but no tab and no binds.
        AppServices.Settings.ManualAdapters.Add(
            new ManualAdapterEntry { Name = "Ghost", IPv4Address = "203.0.113.9", TypeName = "Other" });
        vm.RefreshNetworksCommand.Execute(null);

        Assert.Equal(tabsBefore, vm.Tabs.Count);   // no tab for an offline provider
        var ghostChip = vm.Adapters.First(c => c.IPv4Address == "203.0.113.9");
        Assert.True(ghostChip.IsManual);
        Assert.False(ghostChip.IsOnline);

        // ONLINE manual provider (loopback IP is really on this PC): gets its own tab.
        AppServices.Settings.ManualAdapters.Add(
            new ManualAdapterEntry { Name = "Loop", IPv4Address = "127.0.0.1", TypeName = "Wi-Fi" });
        vm.RefreshNetworksCommand.Execute(null);

        var loopTab = vm.Tabs.FirstOrDefault(t => t.ScopeLabel == "manual:127.0.0.1");
        Assert.NotNull(loopTab);
        Assert.Contains("Loop", loopTab!.Title);
        var loopChip = vm.Adapters.First(c => c.IPv4Address == "127.0.0.1");
        Assert.True(loopChip.IsOnline);

        // Removing via the chip's ✕ wipes the provider, its tab and the chip.
        loopChip.RemoveProviderCommand.Execute(null);
        Assert.DoesNotContain(vm.Tabs, t => t.ScopeLabel == "manual:127.0.0.1");
        Assert.DoesNotContain(vm.Adapters, c => c.IPv4Address == "127.0.0.1");
        Assert.DoesNotContain(AppServices.Settings.ManualAdapters,
            m => m.IPv4Address == "127.0.0.1");
    }

    [AvaloniaFact]
    public void HeaderChips_ListOfflineProviders_GreyedOut()
    {
        NetworkAdapterService.OverrideForTests = () => new[]
        {
            FakeAdapter("eth-1", "Ethernet 1", "192.168.1.10")
        };
        var window = CreateWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);

        AppServices.Settings.ManualAdapters.Add(
            new ManualAdapterEntry { Name = "Ghost", IPv4Address = "203.0.113.9", TypeName = "Other" });
        vm.RefreshNetworksCommand.Execute(null);

        var ghostChip = vm.Adapters.First(c => c.IPv4Address == "203.0.113.9");
        Assert.Equal(0.55, ghostChip.ChipOpacity);
        Assert.Contains("Offline", ghostChip.Tooltip);
        // Summary line mentions the manual provider.
        Assert.Contains("manual provider", vm.AdaptersSummary);
    }

    [AvaloniaFact]
    public void ManualProviderWindow_AddFlow_ValidatesPersistsAndRefusesDuplicates()
    {
        // Constructing the window executes the XAML — a broken dialog fails here.
        var window = new ManualProviderWindow();
        var vm = Assert.IsType<ManualProvidersViewModel>(window.DataContext);

        vm.NewName = "Ghost";
        vm.NewIp = "999.1.2.3";
        vm.AddCommand.Execute(null);
        Assert.Empty(AppServices.Settings.ManualAdapters);          // invalid IP refused
        Assert.NotNull(vm.ValidationMessage);

        vm.NewIp = "203.0.113.9";
        vm.AddCommand.Execute(null);
        var added = Assert.Single(AppServices.Settings.ManualAdapters);   // persisted
        Assert.Equal("Ghost", added.Name);
        Assert.Equal("Ethernet", added.TypeName);   // type combo defaults to index 0

        vm.NewIp = "203.0.113.9";   // duplicate
        vm.AddCommand.Execute(null);
        Assert.Single(AppServices.Settings.ManualAdapters);
        Assert.Contains("already exists", vm.ValidationMessage);

        vm.RemoveCommand.Execute(added);
        Assert.Empty(AppServices.Settings.ManualAdapters);
    }
}
