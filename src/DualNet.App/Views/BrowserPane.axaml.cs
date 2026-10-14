using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DualNet.App.ViewModels;

namespace DualNet.App.Views;

public partial class BrowserPane : UserControl
{
    public BrowserPane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not BrowserViewModel vm || vm.Web is not null)
            return;
        if (!vm.UseRealBrowserEnabled)
            return;

        // Host the real Chromium engine (Windows + WebView2 runtime + user setting).
        var web = new RealWebView();
        vm.AttachWeb(web);
        WebHost.Content = web;
        _ = Dispatcher.UIThread.InvokeAsync(() => { });
    }
}
