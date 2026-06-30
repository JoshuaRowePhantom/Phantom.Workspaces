using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class NotificationsStylesTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void BellRingingAnimation_ApplyingClassToTextBlock_DoesNotThrow()
    {
        // Regression test for #143: string-valued RenderTransform KeyFrame setters cause
        // "No animator registered for the property RenderTransform" because Avalonia's
        // XAML IL compiler does not apply type converters inside KeyFrame.Setter.
        // The fix is to use typed <RotateTransform> object syntax instead of Value="rotate(...)".
        var styles = LoadNotificationsStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("notification-bell-ringing");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        // Measure/Arrange triggers style application and animation keyframe interpretation.
        // This throws InvalidOperationException if RenderTransform keyframe values are strings.
        // The animation keyframes have been removed (#323); the base style retains the typed
        // RenderTransform setter so this guard remains valid.
        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
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

    [AvaloniaFact(Timeout = 15_000)]
    public void NotificationsIndicator_ApplyingClassToProgressBar_DoesNotThrow()
    {
        var styles = LoadNotificationsStyles();

        var progressBar = new ProgressBar();
        progressBar.Classes.Add("notifications-indicator");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void NotificationsIndicator_WhenIndeterminate_DoesNotThrow()
    {
        var styles = LoadNotificationsStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("notifications-indicator");

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
