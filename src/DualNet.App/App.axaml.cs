using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            desktop.ShutdownRequested += (_, _) =>
            {
                AppServices.Manager.PauseAll();
                AppServices.SaveSettings();
                SentrySetup.FlushSync();   // flush events before shutdown (Application_End equivalent)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
