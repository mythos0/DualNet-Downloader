using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DualNet.App.Services;
using DualNet.App.ViewModels;
using DualNet.Core.Storage;

namespace DualNet.App.Views;

public partial class NewTabPage : UserControl
{
    private MainViewModel VM => (MainViewModel)DataContext!;

    public NewTabPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshRecent();
    }

    private void RefreshRecent()
    {
        RecentList.ItemsSource = AppBootstrapper.BrowserStore.Visited.Take(6)
            .Select(v => new RecentItem(v.Title, HostOf(v.Url), v.Url))
            .ToList();
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return ""; }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        VM.Navigate(VM.ActiveTab, text);
    }

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url)
            VM.Navigate(VM.ActiveTab, url);
    }

    private void AddDownload_Click(object sender, RoutedEventArgs e)
    {
        var url = AddUrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        if (Window.GetWindow(this) is MainWindow mw) mw.AddDownloadFromUi(url);
        AddUrlBox.Clear();
    }

    private void Recent_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentItem r)
            VM.Navigate(VM.ActiveTab, r.Url);
    }
}

public sealed record RecentItem(string Title, string HostText, string Url);
