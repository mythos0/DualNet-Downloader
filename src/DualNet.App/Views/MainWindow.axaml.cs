using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls.Notifications;
using DualNet.App.ViewModels;
using DualNet.Core.Engine;

namespace DualNet.App.Views;

public partial class MainWindow : Window
{
    private WindowNotificationManager? _toasts;

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

        _toasts = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 4,
            Margin = new Thickness(0, 0, 14, 14)
        };

        DataContextChanged += OnDataContextChanged;
        WireDragAndDrop();

        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Shutdown();
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ToastRequested -= OnToast;
            vm.ToastRequested += OnToast;
        }
    }

    private void OnToast(string title, string message, bool isError) =>
        _toasts?.Show(new Notification(title, message,
            isError ? NotificationType.Error : NotificationType.Success,
            TimeSpan.FromSeconds(4)));

    // ---- drag & drop links (IDM-style) ------------------------------------------------------

    private void WireDragAndDrop()
    {
        try
        {
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }
        catch { }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Text) ||
                        e.Data.Contains(DataFormats.FileNames)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;

            // Dropped .txt/.lst/.csv files: every line is a link.
            if (e.Data.Contains(DataFormats.FileNames) &&
                e.Data.GetFiles() is { } fileItems)
            {
                foreach (var item in fileItems)
                {
                    var file = item.Path.LocalPath;
                    if (File.Exists(file) &&
                        (file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".lst", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                    {
                        vm.AddUrlsFromText(File.ReadAllText(file));
                    }
                }
            }

            if (e.Data.Contains(DataFormats.Text) &&
                e.Data.GetText() is { Length: > 0 } text)
            {
                vm.AddUrlsFromText(text);
            }
        }
        catch { }
        e.Handled = true;
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel() };
        await dialog.ShowDialog(this);
        if (DataContext is MainViewModel vm)
        {
            vm.RefreshNetworksCommand.Execute(null);
            vm.AttachLocalApi();   // local API may have been reconfigured
        }
    }

    // ---- chrome toolbar handlers --------------------------------------------------------------

    /// <summary>✕ on a tab header — closes that tab.</summary>
    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && (sender as Button)?.DataContext is DownloadTabViewModel tab)
            vm.CloseTabCommand.Execute(tab);
    }

    private void OnAddressTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is AutoCompleteBox box && DataContext is MainViewModel vm)
            vm.SelectedTab?.Browser.UpdateSuggestions(box.Text);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && DataContext is MainViewModel vm &&
            vm.SelectedTab is { } tab && !e.Handled)
        {
            tab.Browser.GoCommand.Execute(tab.Browser.AddressText);
            e.Handled = true;
        }
    }

    /// <summary>Chrome keyboard shortcuts: Ctrl+T new tab, Ctrl+W close tab, Ctrl+L focus omnibox.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.T when ctrl:
                vm.NewTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.W when ctrl:
                vm.CloseTabCommand.Execute(vm.SelectedTab);
                e.Handled = true;
                break;
            case Key.L when ctrl:
                Omnibox?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
