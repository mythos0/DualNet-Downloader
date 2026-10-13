using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace DualNet.App.Views;

public partial class DownloadTabView : UserControl
{
    private Controls.SpeedGraph? _graph;
    private ViewModels.DownloadTabViewModel? _vm;

    public DownloadTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.SpeedSampled -= OnSpeedSampled;
            _vm = DataContext as ViewModels.DownloadTabViewModel;
            if (_vm is not null)
                _vm.SpeedSampled += OnSpeedSampled;
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ctrl+L — focus the address bar, exactly like a web browser.
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            FindAddressBox()?.Focus();
            e.Handled = true;
        }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && _vm is not null)
        {
            _vm.NavigateCommand.Execute(_vm.UrlInput);
            e.Handled = true;
        }
    }

    private TextBox? FindAddressBox() => this.FindControl<TextBox>("AddressBox");

    private void OnSpeedSampled(double bytesPerSecond)
    {
        _graph ??= this.FindControl<Controls.SpeedGraph>("Graph");
        _graph?.Push(bytesPerSecond);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
