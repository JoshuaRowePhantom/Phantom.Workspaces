using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(Phantom.Workspaces.Agent.Gui.Tests.AvaloniaTestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Phantom.Workspaces.Agent.Gui.Tests;

public static class AvaloniaTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        global::Phantom.Workspaces.Gui.Styles.Controls.ControllableBrowserFactory.Create = static () => new HeadlessControllableBrowser();
        return AppBuilder.Configure<TestApplication>()
            .UseHeadless(
                new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true,
                });
    }

    private sealed class TestApplication : Application
    {
    }
}
