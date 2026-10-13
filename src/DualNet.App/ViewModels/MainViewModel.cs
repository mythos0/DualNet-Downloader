using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualNet.Core.Engine;
using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<DownloadTabViewModel> Tabs { get; } = new();
    public ObservableCollection<AdapterChipViewModel> Adapters { get; } = new();
    public DownloadTabViewModel HistoryTab { get; }

    [ObservableProperty] private int themeMode;
    [ObservableProperty] private string adaptersSummary = "";
    [ObservableProperty] private string totalSpeedText = "0 B/s";
    [ObservableProperty] private int selectedTabIndex;

    private static readonly string[] AdapterColors =
        { "#4F8CFF", "#B388FF", "#2DD4BF", "#F472B6", "#FBBF24", "#A3E635", "#FB923C" };

    private readonly DispatcherTimer _totalTimer;

    public MainViewModel()
    {
        AppServices.RefreshAdapters();

        Tabs.Add(new DownloadTabViewModel(TabKind.Combined));
        foreach (var adapter in AppServices.Adapters)
            Tabs.Add(new DownloadTabViewModel(TabKind.SingleAdapter, adapter));
        HistoryTab = new DownloadTabViewModel(TabKind.History);
        Tabs.Add(HistoryTab);

        themeMode = AppServices.Settings.ThemeMode;
        _totalTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateTotals());
        _totalTimer.IsEnabled = true;

        AppServices.Manager.JobEnded += OnJobEnded;
        LoadAdapterChips();

        foreach (var job in AppServices.Manager.RecoverFromDirectory(
                     AppServices.Settings.DownloadDirectory,
                     () => AppServices.AllAdapterIPs(),
                     (label, ids) =>
                     {
                         var r = AppServices.ResolveBindsForTab(label, ids);
                         return r.Count > 0 ? r : null;
                     }))
        {
            AddRestoredJob(job);
        }
    }

    private void AddRestoredJob(DownloadJob job)
    {
        var tab = Tabs.FirstOrDefault(t =>
            t.Kind == TabKind.Combined && job.Scope.Label == "All") ??
            Tabs.FirstOrDefault(t => t.Kind == TabKind.SingleAdapter && t.ScopeLabel == job.Scope.Label) ??
            Tabs[0];
        tab.Items.Insert(0, new DownloadItemViewModel(job));
        tab.HasNoItems = tab.Items.Count == 0;
    }

    partial void OnThemeModeChanged(int value)
    {
        AppServices.ApplyTheme(value);
        AppServices.Settings.ThemeMode = value;
        AppServices.SaveSettings();
    }

    private void OnJobEnded(DownloadJob job)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (job.Status)
            {
                case DownloadStatus.Completed:
                    HistoryTab.RecordHistory(job, "Completed", null);
                    SentrySetup.TrackDownloadCompleted(
                        job.Scope.Label == "All" ? "combined" : job.Scope.Label,
                        job.TotalBytes > 0 ? job.TotalBytes : job.ReceivedBytes,
                        job.DurationSeconds,
                        job.AverageSpeedBps);
                    break;
                case DownloadStatus.Failed:
                    HistoryTab.RecordHistory(job, "Failed", job.StatusMessage);
                    SentrySetup.TrackDownloadFailed(
                        job.Scope.Label == "All" ? "combined" : job.Scope.Label,
                        job.StatusMessage);
                    break;
                case DownloadStatus.Canceled:
                    SentrySetup.TrackDownloadCanceled(
                        job.Scope.Label == "All" ? "combined" : job.Scope.Label);
                    break;
            }
        });
    }

    private void LoadAdapterChips()
    {
        Adapters.Clear();
        for (int i = 0; i < AppServices.Adapters.Count; i++)
            Adapters.Add(new AdapterChipViewModel(AppServices.Adapters[i],
                AdapterColors[i % AdapterColors.Length]));
        AdaptersSummary = AppServices.Adapters.Count switch
        {
            0 => "No active network adapter detected",
            1 => $"1 adapter: {AppServices.Adapters[0].TypeName} ({AppServices.Adapters[0].IPv4Address})",
            _ => $"{AppServices.Adapters.Count} adapters: " +
                 string.Join(" + ", AppServices.Adapters.Select(a => $"{a.TypeName} ({a.IPv4Address})"))
        };
    }

    private void UpdateTotals()
    {
        double sum = Tabs.Where(t => t.Kind != TabKind.History).Sum(t => t.TabSpeedBps);
        TotalSpeedText = Formatter.Speed(sum);
    }

    [RelayCommand]
    private void RefreshNetworks()
    {
        AppServices.RefreshAdapters();
        LoadAdapterChips();
    }

    [RelayCommand]
    private void OpenDownloadsFolder()
    {
        try
        {
            var dir = AppServices.Settings.DownloadDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", $"\"{dir}\"")
                {
                    UseShellExecute = true
                });
        }
        catch { }
    }

    public void Shutdown()
    {
        _totalTimer.IsEnabled = false;
        foreach (var tab in Tabs)
            tab.Shutdown();
        AppServices.Manager.PauseAll();
        AppServices.SaveSettings();
    }
}
