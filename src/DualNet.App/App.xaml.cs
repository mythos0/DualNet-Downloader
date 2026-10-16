using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DualNet.App.Services;
using DualNet.Core;

namespace DualNet.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: a second launch forwards its URL arguments to the first window.
        _singleInstanceMutex = new Mutex(true, "DualNet.Downloader.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            ForwardToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            SentrySetup.CaptureException(args.Exception, "wpf.dispatcher.unhandled");
            args.Handled = true;    // a download manager must survive UI hiccups
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                SentrySetup.CaptureException(ex, "appdomain.unhandled");
            SentrySetup.FlushSync();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            SentrySetup.CaptureException(args.Exception, "task.unobserved");
            args.SetObserved();
        };

        AppBootstrapper.Initialize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DualNet"));

        ApplyTheme(AppBootstrapper.Settings.ThemeMode);
        SentrySetup.HookDispatcher(ex => SentrySetup.CaptureException(ex, "ui.unhandled"));

        var window = new MainWindow();
        window.Show();
        MainWindow2 = window;

        // Open URLs passed on the command line (e.g. "open with DualNet").
        foreach (var arg in e.Args)
        {
            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                window.OpenUrlInNewTab(arg);
            }
        }

        SentrySetup.TrackAppStart();
        base.OnStartup(e);
    }

    public MainWindow? MainWindow2 { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppBootstrapper.Shutdown(); } catch { }
        try { CefRuntime.Shutdown(); } catch { }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            var url = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));
            NativeMethods.SendSecondInstanceUrl(url ?? "");
        }
        catch { }
    }

    /// <summary>ThemeMode: 0 = follow system, 1 = dark, 2 = light.</summary>
    public void ApplyTheme(int mode)
    {
        bool dark = mode switch
        {
            1 => true,
            2 => false,
            _ => IsSystemDark(),
        };
        var uri = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(dict);
    }

    public void RefreshSystemTheme()
    {
        if (AppBootstrapper.Settings.ThemeMode == 0)
            ApplyTheme(0);
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}
