using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class NotificationsStylesTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void NotificationIndicator_ApplyingClassToProgressBar_DoesNotThrow()
    {
        var styles = LoadNotificationsStyles();

        var progressBar = new ProgressBar();
        progressBar.Classes.Add("notification-indicator");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    private static Avalonia.Styling.Styles LoadNotificationsStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/NotificationsStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }
}
