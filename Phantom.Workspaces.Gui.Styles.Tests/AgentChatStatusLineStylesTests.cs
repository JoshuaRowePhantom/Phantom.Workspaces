using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;

namespace Phantom.Workspaces.Gui.Styles.Tests;

public sealed class AgentChatStatusLineStylesTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void AgentTabHeaderBrain_ApplyingClassToTextBlock_DoesNotThrow()
    {
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-tab-header-brain");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentTabHeaderBrain_Thinking_ApplyingBothClassesToTextBlock_DoesNotThrow()
    {
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-tab-header-brain");
        textBlock.Classes.Add("thinking");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentTabHeaderBrain_DefaultOpacity_Is0Point25()
    {
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-tab-header-brain");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(0.25, textBlock.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatStatusLineBrain_WhenIdle_OpacityIs0Point25()
    {
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-chat-status-line-brain");
        textBlock.Classes.Add("idle");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(0.25, textBlock.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentTabHeaderBrain_WhenThinking_OpacityIsOne()
    {
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-tab-header-brain");
        textBlock.Classes.Add("thinking");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(1.0, textBlock.Opacity);
    }

    private static Avalonia.Styling.Styles LoadAgentChatStatusLineStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Styles/Styles/AgentChatStatusLineStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Styles/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }
}
