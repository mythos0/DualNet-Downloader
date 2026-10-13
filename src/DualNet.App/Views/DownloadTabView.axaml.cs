using Avalonia.Controls;
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

    private void OnSpeedSampled(double bytesPerSecond)
    {
        _graph ??= this.FindControl<Controls.SpeedGraph>("Graph");
        _graph?.Push(bytesPerSecond);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
