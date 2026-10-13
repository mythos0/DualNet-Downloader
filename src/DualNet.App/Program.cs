using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DualNet.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Hidden CI helper: DualNetDownloader --screenshot /path/out.png
        // Boots the real UI on the current display, renders the main window to a PNG and exits.
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            RunScreenshot(args[1]);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RunScreenshot(string path)
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            Args = Array.Empty<string>(),
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        BuildAvaloniaApp().SetupWithLifetime(lifetime);

        lifetime.Startup += (_, _) =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Task.Delay(1800);   // let the first frames settle
                    var win = lifetime.MainWindow;
                    if (win is not null)
                    {
                        var pixelSize = PixelSize.FromSize(win.ClientSize, win.RenderScaling);
                        using var rtb = new RenderTargetBitmap(pixelSize);
                        rtb.Render(win);
                        rtb.Save(path);
                        Console.WriteLine($"screenshot saved: {path}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"screenshot failed: {ex.Message}");
                }
                lifetime.Shutdown();
            }, DispatcherPriority.Background);
        };

        lifetime.Start(Array.Empty<string>());
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
