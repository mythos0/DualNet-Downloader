using System;
using Avalonia.Controls;
using Avalonia.Input;
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
        if (DataContext is not BrowserViewModel vm || !vm.UseRealBrowserEnabled)
            return;

        if (vm.Web is { } existing)
        {
            // The view was rebuilt (e.g. after a tab switch) — re-host the SAME engine
            // instance. RealWebView re-creates its native surface and the view-model
            // restores the current page once the engine reports Ready again.
            WebHost.Content = existing;
            return;
        }

        // First host for this tab: create the real Chromium engine (Windows + WebView2).
        var web = new RealWebView();
        vm.AttachWeb(web);
        WebHost.Content = web;
        _ = Dispatcher.UIThread.InvokeAsync(() => { });
    }

    /// <summary>Enter on the New Tab Page search box opens the address on all networks.</summary>
    private void OnNtpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && DataContext is BrowserViewModel vm && !e.Handled)
        {
            vm.GoCommand.Execute(vm.AddressText.Length > 0 ? vm.AddressText : (sender as TextBox)?.Text);
            e.Handled = true;
        }
    }
}
