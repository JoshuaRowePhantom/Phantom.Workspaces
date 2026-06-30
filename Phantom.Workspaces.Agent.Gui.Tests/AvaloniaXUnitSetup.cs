using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Phantom.Workspaces.Agent.Gui.Tests.AvaloniaTestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]

namespace Phantom.Workspaces.Agent.Gui.Tests;

public static class AvaloniaTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        global::Phantom.Workspaces.Gui.Shared.Controls.ControllableBrowserFactory.Create = static () => new HeadlessControllableBrowser();
        return AppBuilder.Configure<TestApplication>()
            .UseHeadless(
                new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true,
                });
    }

    private sealed class TestApplication : Application
    {
        public override void Initialize()
        {
            // A loaded theme is required so that TryFindResource calls in
            // AgentChatOutputControl.BuildThemeVariables() can complete synchronously
            // on the Avalonia dispatcher thread without deadlocking.
            Styles.Add(new FluentTheme());
        }
    }
}
