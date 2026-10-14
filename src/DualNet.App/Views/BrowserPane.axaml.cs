using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DualNet.App.Views;

public partial class BrowserPane : UserControl
{
    public BrowserPane()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
