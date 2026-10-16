using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DualNet.App.ViewModels;

namespace DualNet.App.Views;

public partial class DownloadsPage : UserControl
{
    private MainViewModel VM => (MainViewModel)DataContext!;

    public DownloadsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UrlBox.Focus();
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add_Click(sender, e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        if (Window.GetWindow(this) is MainWindow mw) mw.AddDownloadFromUi(url);
        UrlBox.Clear();
    }

    private void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in VM.Downloads.Where(d => d.CanPause).ToList())
            d.Job.Pause();
    }
}
