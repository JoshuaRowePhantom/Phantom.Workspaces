using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class AgentChatStatusLineStylesTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatStatusLineBrain_Default_OpacityIsZero()
    {
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-chat-status-line-brain");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(0.0, textBlock.Opacity);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatStatusLineBrain_WhenThinking_OpacityIsOne()
    {
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-chat-status-line-brain");
        textBlock.Classes.Add("thinking");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(1.0, textBlock.Opacity);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatStatusLineBrain_WhenThinking_HasPulsatingAnimation()
    {
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-chat-status-line-brain");
        textBlock.Classes.Add("thinking");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var thinkingStyle = styles.OfType<Style>()
            .First(s => s.Selector?.ToString()?.Contains("thinking") == true);

        Assert.Single(thinkingStyle.Animations);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatStatusLineBrain_WhenThinking_ApplyingClassToTextBlock_DoesNotThrow()
    {
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-chat-status-line-brain");
        textBlock.Classes.Add("thinking");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatStatusLineValue_WhenStyleApplied_HasNonZeroLeftMargin()
    {
        // Issue #401: agent-chat-status-line-value must carry its own left margin
        // so no separator TextBlock is needed before it.
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-chat-status-line-value");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.True(textBlock.Margin.Left > 0);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatStatusLineLabel_WhenStyleApplied_HasNonZeroLeftMargin()
    {
        // Issue #401: agent-chat-status-line-label must carry its own left margin
        // so no separator TextBlock is needed before it.
        var styles = LoadAgentChatStatusLineStyles();

        var textBlock = new TextBlock();
        textBlock.Classes.Add("agent-chat-status-line-label");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(textBlock);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.True(textBlock.Margin.Left > 0);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatAutoscrollToggle_WhenStyleApplied_HasNonZeroLeftMargin()
    {
        // Issue #512: agent-chat-autoscroll-toggle must carry its own left margin.
        var styles = LoadAgentChatStatusLineStyles();

        var checkBox = new CheckBox();
        checkBox.Classes.Add("agent-chat-autoscroll-toggle");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(checkBox);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.True(checkBox.Margin.Left > 0);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatReasoningToggle_WhenStyleApplied_HasNonZeroLeftMargin()
    {
        // Issue #512: agent-chat-status-line-reasoning-toggle must carry its own left margin.
        var styles = LoadAgentChatStatusLineStyles();

        var button = new Button();
        button.Classes.Add("agent-chat-status-line-reasoning-toggle");

        var host = new StackPanel();
        host.Styles.Add(styles);
        host.Children.Add(button);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.True(button.Margin.Left > 0);
    }

    private static Avalonia.Styling.Styles LoadAgentChatStatusLineStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/AgentChatStatusLineStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }
}
