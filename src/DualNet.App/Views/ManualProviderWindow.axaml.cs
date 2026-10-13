using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DualNet.App.ViewModels;

namespace DualNet.App.Views;

public partial class ManualProviderWindow : Window
{
    public ManualProviderWindow()
    {
        InitializeComponent();
        DataContext = new ManualProvidersViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
            Close();
        base.OnKeyDown(e);
    }
}
