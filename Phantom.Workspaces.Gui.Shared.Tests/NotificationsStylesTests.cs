using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class NotificationsStylesTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void ExclamationIndicatorStyle_WithoutIsIndeterminate_HasCorrectSize()
    {
        var sharedStyles = LoadSharedStyles();
        var notificationStyles = LoadNotificationsStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("exclamation-indicator");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(notificationStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(22.0, progressBar.Width);
        Assert.Equal(22.0, progressBar.Height);
        Assert.Equal(0.0, progressBar.MinWidth);
        Assert.Equal(0.0, progressBar.MinHeight);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void ExclamationIndicatorStyle_WithoutIsIndeterminate_HasOpacityZero()
    {
        var sharedStyles = LoadSharedStyles();
        var notificationStyles = LoadNotificationsStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("exclamation-indicator");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(notificationStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var glyph = progressBar.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(glyph);
        Assert.Equal(0.0, glyph.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void ExclamationIndicatorStyle_WithIsIndeterminate_HasOpacityOne()
    {
        var sharedStyles = LoadSharedStyles();
        var notificationStyles = LoadNotificationsStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("exclamation-indicator");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(notificationStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var glyph = progressBar.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(glyph);
        Assert.Equal(1.0, glyph.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void ExclamationIndicatorStyle_WithIsIndeterminate_DisplaysExclamationMark()
    {
        var sharedStyles = LoadSharedStyles();
        var notificationStyles = LoadNotificationsStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("exclamation-indicator");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(notificationStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var glyph = progressBar.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(glyph);
        Assert.Equal("!", glyph.Text);
    }

    private static Avalonia.Styling.Styles LoadNotificationsStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/NotificationsStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }

    private static Avalonia.Styling.Styles LoadSharedStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/SharedStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }
}
