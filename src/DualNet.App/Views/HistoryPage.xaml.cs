using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DualNet.App.Services;
using DualNet.App.ViewModels;
using DualNet.Core.Storage;

namespace DualNet.App.Views;

public partial class HistoryPage : UserControl
{
    private IReadOnlyList<VisitedEntry> _all = Array.Empty<VisitedEntry>();

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        DataContextChanged += (_, _) => Reload();
    }

    private void Reload()
    {
        _all = AppBootstrapper.BrowserStore.Visited;
        ApplyFilter();
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = FilterBox.Text?.Trim() ?? "";
        HistoryList.ItemsSource = _all
            .Where(v => q.Length == 0 ||
                        v.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        v.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(300)
            .Select(v => new HistoryRow(v.Title, v.Url, When(v.VisitedUtc)))
            .ToList();
    }

    private static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var delta = DateTime.Now - local;
        return delta.TotalMinutes < 1 ? "just now"
             : delta.TotalHours < 1 ? $"{delta.TotalMinutes:0} min ago"
             : delta.TotalDays < 1 ? $"{delta.TotalHours:0} h ago"
             : local.ToString("MMM d HH:mm");
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryRow r)
            VM_Navigate(r.Url);
    }

    private void VM_Navigate(string url)
    {
        if (DataContext is MainViewModel vm) vm.Navigate(vm.ActiveTab, url);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        AppBootstrapper.BrowserStore.ClearVisited();
        Reload();
    }
}

public sealed record HistoryRow(string Title, string Url, string WhenText);
