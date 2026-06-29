using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;

namespace Phantom.Workspaces.Gui.Styles.Tests;

public sealed class RunningIndicatorStylesTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicator_ApplyingClassToProgressBar_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar();
        progressBar.Classes.Add("running-indicator");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicator_WhenIdle_OpacityIsZero()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("running-indicator");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(0.0, progressBar.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicator_WhenIndeterminate_OpacityIsOne()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("running-indicator");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(1.0, progressBar.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicator_WhenSucceeded_OpacityIsOne()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("running-indicator");
        progressBar.Classes.Add("succeeded");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(1.0, progressBar.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicator_WhenSucceeded_ValueIs100()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("running-indicator");
        progressBar.Classes.Add("succeeded");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(100.0, progressBar.Value);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicator_WhenFailed_OpacityIsOne()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("running-indicator");
        progressBar.Classes.Add("failed");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(1.0, progressBar.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicator_WhenFailed_ValueIs100()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("running-indicator");
        progressBar.Classes.Add("failed");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(100.0, progressBar.Value);
    }

    private static Avalonia.Styling.Styles LoadSharedStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Styles/Styles/SharedStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Styles/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }
}
