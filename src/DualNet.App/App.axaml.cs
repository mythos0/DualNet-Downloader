using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using DualNet.App.Views;

namespace DualNet.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize Sentry FIRST (equivalent of ASP.NET Application_Start ordering),
        // so AppDomain unhandled exceptions and startup logs are captured from the start.
        SentrySetup.Init();

        AppServices.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SentrySetup.HookDispatcher();
            SentrySetup.TrackAppStart();

            desktop.MainWindow = new MainWindow();
            SetupTrayIcon(desktop.MainWindow);
            desktop.ShutdownRequested += (_, _) =>
            {
                AppServices.Manager.PauseAll();
                AppServices.SaveSettings();
                SentrySetup.FlushSync();   // flush events before shutdown (Application_End equivalent)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(Window mainWindow)
    {
        try
        {
            var tray = new TrayIcon
            {
                ToolTipText = "DualNet Downloader",
                IsVisible = true
            };
            using (var stream = AssetLoader.Open(new Uri("avares://DualNet.App/Assets/app.ico")))
                tray.Icon = new WindowIcon(stream);
            tray.Clicked += (_, _) =>
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            };
            var icons = new TrayIcons { tray };
            TrayIcon.SetIcons(this, icons);
        }
        catch
        {
            // tray is optional (some Linux shells lack the extension)
        }
    }
}
