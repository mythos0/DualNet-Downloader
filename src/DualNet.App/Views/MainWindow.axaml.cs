using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DualNet.App.ViewModels;

namespace DualNet.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        AppServices.MainWindow = this;

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://DualNet.App/Assets/app.ico"));
            Icon = new WindowIcon(stream);
        }
        catch { }

        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Shutdown();
        };
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel() };
        await dialog.ShowDialog(this);
        if (DataContext is MainViewModel vm)
            vm.RefreshNetworksCommand.Execute(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
