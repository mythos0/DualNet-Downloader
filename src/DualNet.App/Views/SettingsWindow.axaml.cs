using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DualNet.App.ViewModels;

namespace DualNet.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose download folder",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is SettingsViewModel vm)
            vm.DownloadDirectory = folders[0].Path.LocalPath;
    }

    private async void OnSendSentryTest(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.IsEnabled = false;
        try
        {
            if (DataContext is SettingsViewModel vm)
                await vm.SendSentryTestEvent();
        }
        finally
        {
            if (sender is Button btn2) btn2.IsEnabled = true;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.Save();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
