using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Gui.Shared.Tests;

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
    public void RunningIndicator_WhenIndeterminate_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("running-indicator");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicatorAgent_WhenIndeterminate_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("running-indicator");
        progressBar.Classes.Add("running-indicator-agent");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicatorAgent_WhenIndeterminate_OpacityIsOne()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("running-indicator");
        progressBar.Classes.Add("running-indicator-agent");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(1.0, progressBar.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicatorAgent_WhenIdle_OpacityIsZero()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("running-indicator");
        progressBar.Classes.Add("running-indicator-agent");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(0.0, progressBar.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicatorAgent_WhenSucceeded_OpacityIsOne()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("running-indicator");
        progressBar.Classes.Add("running-indicator-agent");
        progressBar.Classes.Add("succeeded");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(1.0, progressBar.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningIndicatorAgent_WhenFailed_OpacityIsOne()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("running-indicator");
        progressBar.Classes.Add("running-indicator-agent");
        progressBar.Classes.Add("failed");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(1.0, progressBar.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicator_Width_Is22()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar();
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("pulsating-brain");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(22.0, progressBar.Width);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicator_Height_Is22()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar();
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("pulsating-brain");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(22.0, progressBar.Height);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicatorPulsatingBrain_ApplyingClassToProgressBar_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar();
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("pulsating-brain");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicatorPulsatingBrain_WhenIdle_GlyphOpacityIs0Point25()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("pulsating-brain");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var glyph = progressBar.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(glyph);
        Assert.Equal(0.25, glyph.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicatorPulsatingBrain_WhenIndeterminate_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("pulsating-brain");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicatorVibratingAlarmClock_ApplyingClassToProgressBar_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar();
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("vibrating-alarm-clock");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicatorVibratingAlarmClock_WhenIndeterminate_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("vibrating-alarm-clock");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicatorVibratingAlarmClock_WhenPaused_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("vibrating-alarm-clock");
        progressBar.Classes.Add("paused");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicatorVibratingAlarmClock_WhenFailed_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = false };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("vibrating-alarm-clock");
        progressBar.Classes.Add("failed");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicatorSwingingBell_ApplyingClassToProgressBar_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar();
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("swinging-bell");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void GlyphIndicatorSwingingBell_WhenIndeterminate_DoesNotThrow()
    {
        var styles = LoadSharedStyles();

        var progressBar = new ProgressBar { IsIndeterminate = true };
        progressBar.Classes.Add("glyph-indicator");
        progressBar.Classes.Add("swinging-bell");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    private static Avalonia.Styling.Styles LoadSharedStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/SharedStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }
}
