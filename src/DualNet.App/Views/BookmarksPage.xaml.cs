using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DualNet.App.Services;
using DualNet.App.ViewModels;
using DualNet.Core.Storage;

namespace DualNet.App.Views;

public partial class BookmarksPage : UserControl
{
    public BookmarksPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        DataContextChanged += (_, _) => Reload();
    }

    private void Reload()
    {
        BookmarkList.ItemsSource = AppBootstrapper.BrowserStore.Bookmarks
            .OrderByDescending(b => b.AddedUtc)
            .ToList();
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookmarkEntry b && DataContext is MainViewModel vm)
            vm.Navigate(vm.ActiveTab, b.Url);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookmarkEntry b)
        {
            AppBootstrapper.BrowserStore.RemoveBookmark(b.Url);
            Reload();
            if (DataContext is MainViewModel vm) vm.ReloadBookmarksBar();
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Bookmarks HTML|*.html;*.htm|All files|*.*" };
        if (dlg.ShowDialog(Window.GetWindow(this)!) != true) return;
        var n = AppBootstrapper.BrowserStore.ImportBookmarksHtml(dlg.FileName);
        Reload();
        if (DataContext is MainViewModel vm)
        {
            vm.ReloadBookmarksBar();
            vm.GetType();
        }
        MessageBox.Show($"{n} bookmarks imported.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Bookmarks HTML|*.html",
            FileName = $"dualnet-bookmarks-{DateTime.Now:yyyyMMdd}.html",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)!) != true) return;
        var n = AppBootstrapper.BrowserStore.ExportBookmarksHtml(dlg.FileName);
        MessageBox.Show($"{n} bookmarks exported to {dlg.FileName}", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
