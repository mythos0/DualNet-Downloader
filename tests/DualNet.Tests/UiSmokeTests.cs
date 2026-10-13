using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DualNet.App;
using DualNet.App.ViewModels;
using DualNet.App.Views;
using Xunit;

namespace DualNet.Tests;

/// <summary>
/// Headless UI smoke tests. The headless XUnit session may not expose a classic desktop
/// lifetime, so each test builds its own window/VM pair — the documented pattern for
/// Avalonia.Headless.XUnit control tests.
/// </summary>
public class UiSmokeTests
{
    private static MainWindow CreateWindow()
    {
        AppServices.Initialize();          // idempotent
        return new MainWindow { DataContext = new MainViewModel() };
    }

    [AvaloniaFact]
    public void MainWindow_BootsWithCombinedAndHistoryTabs()
    {
        var window = CreateWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);

        Assert.True(vm.Tabs.Count >= 2, $"expected >= 2 tabs, got {vm.Tabs.Count}");
        Assert.Equal(TabKind.Combined, vm.Tabs[0].Kind);
        Assert.Equal(TabKind.History, vm.Tabs[^1].Kind);

        // Every download tab exposes add/paste commands.
        Assert.NotNull(vm.Tabs[0].AddFromInputCommand);
        Assert.NotNull(vm.Tabs[0].PasteFromClipboardCommand);

        // Adapter tab exists for every detected adapter with a gateway.
        var adapterTabs = vm.Tabs.Where(t => t.Kind == TabKind.SingleAdapter).ToList();
        Assert.Equal(AppServices.Adapters.Count, adapterTabs.Count);
    }

    [AvaloniaFact]
    public void ThemeToggle_AppliesDarkLightAndSystemVariants()
    {
        var window = CreateWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);

        vm.ThemeMode = 1;
        Assert.Equal(ThemeVariant.Dark, Avalonia.Application.Current!.RequestedThemeVariant);
        vm.ThemeMode = 2;
        Assert.Equal(ThemeVariant.Light, Avalonia.Application.Current!.RequestedThemeVariant);
        vm.ThemeMode = 0;
        Assert.Equal(ThemeVariant.Default, Avalonia.Application.Current!.RequestedThemeVariant);

        // Setting persists for next launch.
        Assert.Equal(0, AppServices.Settings.ThemeMode);
    }

    [AvaloniaFact]
    public void AddDownload_RejectsInvalidUrl_WithFriendlyError()
    {
        var window = CreateWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);
        var combined = vm.Tabs[0];

        combined.AddDownload("not-a-url", out var error);
        Assert.NotNull(error);

        combined.AddDownload("ftp://files.example.com/x.zip", out var error2);
        Assert.NotNull(error2);

        // Well-formed https URL is accepted (job may fail to connect, that's fine).
        if (AppServices.Adapters.Count > 0)
        {
            var item = combined.AddDownload("https://example.com/definitely-not-a-real-file.bin", out var error3);
            Assert.Null(error3);
            Assert.NotNull(item);
            item!.Job.Cancel();
            item.Job.CleanupPartial();
        }
    }

    [AvaloniaFact]
    public void HistoryTab_RecordsCompletedDownloadEntry()
    {
        var window = CreateWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);

        vm.HistoryTab.RecordHistory(
            new DualNet.Core.Engine.DownloadJob("https://example.com/sentinel-file.bin",
                Array.Empty<System.Net.IPAddress>(),
                new DualNet.Core.Models.DownloadScope("All", new List<string>()),
                Path.GetTempPath(),
                new DualNet.Core.Models.DownloadOptions()),
            "Completed", null);

        Assert.Contains(vm.HistoryTab.HistoryEntries, h => h.FileName.Contains("sentinel-file"));
    }

    [AvaloniaFact]
    public void SpeedGraph_PushSamplesWithoutThrowing()
    {
        var window = CreateWindow();
        var graph = new DualNet.App.Controls.SpeedGraph();
        graph.Measure(new Avalonia.Size(200, 46));
        graph.Arrange(new Avalonia.Rect(0, 0, 200, 46));
        for (int i = 0; i < 120; i++)
            graph.Push(i * 1000.0);
        // Headless render pass:
        (window as Control).InvalidateVisual();
        Assert.True(true);
    }
}
