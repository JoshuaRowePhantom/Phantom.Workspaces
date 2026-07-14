using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Skia;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;
using Phantom.Workspaces.Templates;

[assembly: AvaloniaTestApplication(typeof(Phantom.Workspaces.Tests.AvaloniaTestAppBuilder))]

namespace Phantom.Workspaces.Tests;

public static class AvaloniaTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Phantom.Workspaces.Gui.Shared.Controls.ControllableBrowserFactory.Create = static () => new HeadlessControllableBrowser();
        Phantom.Workspaces.Controls.ConfiguredWebViewFactory.Create = static () => new HeadlessConfiguredWebView();
        return AppBuilder.Configure<TestApplication>()
            .UseHeadless(
                new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = false,
                })
            .UseSkia();
    }

    private sealed class TestApplication : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Styles.Add(new DockFluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://Phantom.Workspaces.Tests/"))
            {
                Source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/SharedStyles.axaml")
            });
            Styles.Add(new StyleInclude(new Uri("avares://Phantom.Workspaces.Tests/"))
            {
                Source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/NotificationsStyles.axaml")
            });

            foreach (var template in new WorkspaceDataTemplates())
            {
                DataTemplates.Add(template);
            }
        }
    }
}
