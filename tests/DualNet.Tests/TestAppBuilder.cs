using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using DualNet.App;

[assembly: AvaloniaTestApplication(typeof(DualNet.Tests.TestAppBuilder))]

namespace DualNet.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::DualNet.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
