using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class QueueComposerNormalModeHintTests
{
    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    [Fact]
    public async Task NormalModeHint_DefaultComposer_WhenNotFormattedMode_ReturnsShortcutString()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.IsFormattedMode = false;

        Assert.NotNull(composer.NormalModeHint);
        Assert.NotEmpty(composer.NormalModeHint);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task NormalModeHint_DefaultComposer_WhenFormattedMode_ReturnsNull()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.IsFormattedMode = true;

        Assert.Null(composer.NormalModeHint);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task NormalModeHint_NonDefaultComposer_WhenNotFormattedMode_ReturnsNull()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var appendComposer = new QueueComposerViewModel(inputQueue, chat.DefaultInputQueue, isDefaultComposer: false);

        appendComposer.IsFormattedMode = false;

        Assert.Null(appendComposer.NormalModeHint);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task ActiveHint_DefaultComposer_WhenNormalMode_ReturnsNormalModeHint()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.IsFormattedMode = false;

        Assert.Equal(composer.NormalModeHint, composer.ActiveHint);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task ActiveHint_DefaultComposer_WhenFormattedMode_ReturnsFormattedModeHint()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.IsFormattedMode = true;

        Assert.Equal(composer.FormattedModeHint, composer.ActiveHint);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task IsFormattedMode_WhenChanged_RaisesNormalModeHintPropertyChanged()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        var changedProperties = new List<string?>();
        composer.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        composer.IsFormattedMode = true;
        composer.IsFormattedMode = false;

        Assert.Contains(nameof(QueueComposerViewModel.NormalModeHint), changedProperties);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task IsFormattedMode_WhenChanged_RaisesActiveHintPropertyChanged()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        var changedProperties = new List<string?>();
        composer.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        composer.IsFormattedMode = true;
        composer.IsFormattedMode = false;

        Assert.Contains(nameof(QueueComposerViewModel.ActiveHint), changedProperties);

        inputQueue.Dispose();
    }
}
