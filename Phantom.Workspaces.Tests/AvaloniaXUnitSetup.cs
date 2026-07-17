using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;
using Phantom.Workspaces.Templates;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Phantom.Workspaces.Tests.AvaloniaTestAppBuilder))]

// Serialize headless Avalonia tests: the stock Avalonia.Headless.XUnit harness dispatches every
// test on a single dispatch thread and Avalonia does not support concurrent execution against a
// shared application. This assembly already uses PerTest isolation (the supported default), so no
// AvaloniaTestIsolation attribute is needed. See issue #1101.
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]

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

            var themeDictionaries = new ResourceDictionary();
            themeDictionaries.ThemeDictionaries[ThemeVariant.Light] =
                new ResourceInclude(new Uri("avares://Phantom.Workspaces.Tests/"))
                {
                    Source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Themes/Light.axaml")
                };
            themeDictionaries.ThemeDictionaries[ThemeVariant.Dark] =
                new ResourceInclude(new Uri("avares://Phantom.Workspaces.Tests/"))
                {
                    Source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Themes/Dark.axaml")
                };
            Resources.MergedDictionaries.Add(themeDictionaries);

            foreach (var template in new WorkspaceDataTemplates())
            {
                DataTemplates.Add(template);
            }
        }
    }
}
