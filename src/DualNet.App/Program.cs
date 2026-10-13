using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DualNet.App;

/// <summary>URLs found on the command line, added to the Combined tab after startup.</summary>
public static class StartupRequests
{
    public static List<string> PendingUrls { get; } = new();
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Hidden CI helper: DualNetDownloader --screenshot /path/out.png
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            RunScreenshot(args[1]);
            return;
        }

        // IDM-style command line: DualNetDownloader.exe <url...> or /d <url>
        var urls = ExtractUrls(args);
        if (urls.Count > 0)
        {
            // Single-instance behavior: forward to a running instance (localhost API) and exit.
            var forwarded = true;
            foreach (var url in urls)
            {
                var sent = Core.Engine.LocalApiService.TrySendToRunningInstance(
                    AppServices.TryLoadLocalApiPort(), url).GetAwaiter().GetResult();
                if (!sent) forwarded = false;
            }
            if (forwarded)
                return;
            StartupRequests.PendingUrls.AddRange(urls);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static List<string> ExtractUrls(string[] args)
    {
        var urls = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var candidate = string.Equals(args[i], "/d", StringComparison.OrdinalIgnoreCase) &&
                            i + 1 < args.Length
                ? args[++i]
                : args[i];
            if (candidate.StartsWith('-') || candidate.StartsWith('/'))
                continue;   // flags other than "/d url"
            if (Core.Engine.UrlNormalizer.Normalize(candidate) is { } normalized &&
                normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(normalized);
            }
        }
        return urls;
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
