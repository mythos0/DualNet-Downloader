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

        // v4.1.1 startup crash guard: a failure before the main window exists previously
        // vanished (exception swallowed below, no window, no message — "the app is not
        // opening"). Now: log to %APPDATA%\DualNet\logs + show a real error dialog.
        try
        {
            StartupCore(e);
        }
        catch (Exception ex)
        {
            FatalStartupError("startup", ex);
        }
    }

    private void StartupCore(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var log = CrashLog.Write("wpf.dispatcher.unhandled", args.Exception);
            SentrySetup.CaptureException(args.Exception, "wpf.dispatcher.unhandled");
            if (MainWindow2 is null)
            {
                // The main window never appeared — without this dialog the app would look
                // like it "did not open". Report loudly and exit.
                args.Handled = false;
                ShowFatalDialog(args.Exception, log);
                Environment.Exit(1);
                return;
            }
            args.Handled = true;    // a download manager must survive UI hiccups
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CrashLog.Write("appdomain.unhandled", ex);
                SentrySetup.CaptureException(ex, "appdomain.unhandled");
            }
            SentrySetup.FlushSync();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            SentrySetup.CaptureException(args.Exception, "task.unobserved");
            args.SetObserved();
        };

        AppBootstrapper.Initialize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DualNet"));
        CrashLog.Note($"Starting DualNet {Assembly.GetEntryAssembly()?.GetName().Version} " +
                      $"from {AppContext.BaseDirectory}");

        // Initialize the Chromium engine BEFORE the first tab exists. This was previously
        // never called at all (v4.0.0/v4.1.0) — the first browser tab crashed with
        // "Cef is not initialized" and the failure was swallowed before any window showed.
        try
        {
            CefRuntime.EnsureInitialized();
        }
        catch (Exception ex)
        {
            var log = CrashLog.Write("cef.initialize", ex);
            SentrySetup.CaptureException(ex, "cef.initialize");
            ShowFatalDialog(ex, log);
            Environment.Exit(1);
            return;
        }

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

    private static void ShowFatalDialog(Exception ex, string logPath)
    {
        try
        {
            var details = ex.Message;
            var hint = string.IsNullOrEmpty(logPath)
                ? ""
                : $"\n\nA detailed error report was saved to:\n{logPath}";
            MessageBox.Show(
                $"DualNet Downloader failed to start.\n\n{details}{hint}",
                "DualNet Downloader — startup error",
                MessageBoxButton.OK, MessageBoxImage.Error,
                MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
        }
        catch
        {
            // Even MessageBox can fail in a poisoned process — do not mask the original error.
        }
    }

    private static void FatalStartupError(string source, Exception ex)
    {
        try { SentrySetup.CaptureException(ex, source); } catch { }
        var log = CrashLog.Write(source, ex);
        ShowFatalDialog(ex, log);
        try { Environment.Exit(1); } catch { }
        try { Application.Current?.Shutdown(1); } catch { }
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
        try
        {
            var dict = new ResourceDictionary { Source = uri };
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(dict);
        }
        catch (Exception ex)
        {
            // v4.1.1 hardening: a missing/broken theme dictionary must never kill startup
            // (the missing Dark.xaml did exactly that in v4.0.0–v4.1.0). Fall back to Light.
            CrashLog.Write($"apply-theme:{mode}", ex);
            try
            {
                var light = new ResourceDictionary { Source = new Uri("Themes/Light.xaml", UriKind.Relative) };
                Resources.MergedDictionaries.Clear();
                Resources.MergedDictionaries.Add(light);
            }
            catch { }
        }
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
