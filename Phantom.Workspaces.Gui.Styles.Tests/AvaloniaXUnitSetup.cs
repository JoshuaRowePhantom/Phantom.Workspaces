using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Phantom.Workspaces.Gui.Styles.Tests.GuiStylesTestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Phantom.Workspaces.Gui.Styles.Tests;

public static class GuiStylesTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<GuiStylesTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    private sealed class GuiStylesTestApplication : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }
    }
}
