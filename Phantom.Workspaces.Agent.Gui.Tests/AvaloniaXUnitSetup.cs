using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Phantom.Workspaces.Agent.Gui.Tests.AvaloniaTestAppBuilder))]

// Headless Avalonia tests must not run concurrently: with the stock Avalonia.Headless.XUnit
// harness the shared HeadlessUnitTestSession dispatches every test on a single dispatch thread,
// and Avalonia documents that concurrent execution against a shared application is unsupported.
// Serializing the assembly removes the last load-driven trigger for the cross-thread
// "a different thread owns it" fault (DefaultRenderLoop.Add -> Dispatcher.VerifyAccess).
// PerTest isolation (Avalonia's supported default) is used — no AvaloniaTestIsolation attribute
// is declared — so a construction failure can never cascade across the batch. See issue #1101.
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]

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
            // Issue #1035: the agent-chat detail region hosts a Dock.Avalonia DocumentDock, so the
            // Dock Fluent theme (with cached document content, matching App.axaml) must be present
            // for the control to instantiate and render its cached detail documents under test.
            Styles.Add(new DockFluentTheme { CacheDocumentTabContent = true });
        }
    }
}
