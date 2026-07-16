using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Phantom.Workspaces.Agent.Gui.Tests.AvaloniaTestAppBuilder))]

// Use a single shared Application/Dispatcher for the whole assembly. Without this attribute
// Avalonia's HeadlessUnitTestSession defaults to AvaloniaTestIsolationLevel.PerTest, which
// tears down and rebuilds the Application/Dispatcher/compositor on every test. That repeated
// AvaloniaHeadlessPlatform.Initialize path is the source of the intermittent
// "The calling thread cannot access this object because a different thread owns it" crash
// (DefaultRenderLoop.Add -> Dispatcher.VerifyAccess). Building the app exactly once removes
// the crash surface deterministically. See issues #815 and #1012.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

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
