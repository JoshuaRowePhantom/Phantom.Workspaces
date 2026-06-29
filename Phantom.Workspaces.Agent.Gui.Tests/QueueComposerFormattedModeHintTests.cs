using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class QueueComposerFormattedModeHintTests
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
    public async Task FormattedModeHint_DefaultComposer_WhenNotFormattedMode_ReturnsNull()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.IsFormattedMode = false;

        Assert.Null(composer.FormattedModeHint);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task FormattedModeHint_DefaultComposer_WhenFormattedMode_ReturnsHintText()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.IsFormattedMode = true;

        Assert.NotNull(composer.FormattedModeHint);
        Assert.NotEmpty(composer.FormattedModeHint);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task FormattedModeHint_NonDefaultComposer_WhenFormattedMode_ReturnsNull()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);

        // Create a non-default (append-to-queue) composer directly.
        var appendComposer = new QueueComposerViewModel(inputQueue, chat.DefaultInputQueue, isDefaultComposer: false);

        appendComposer.IsFormattedMode = true;

        Assert.Null(appendComposer.FormattedModeHint);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task IsFormattedMode_WhenChanged_RaisesFormattedModeHintPropertyChanged()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        var changedProperties = new List<string?>();
        composer.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        composer.IsFormattedMode = true;
        composer.IsFormattedMode = false;

        Assert.Contains(nameof(QueueComposerViewModel.FormattedModeHint), changedProperties);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task PlaceholderText_DefaultComposer_WhenFormattedMode_IsSimplified()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.IsFormattedMode = true;

        // In formatted mode the placeholder should be simplified — shortcut hints
        // are now carried by FormattedModeHint, not embedded in the placeholder.
        Assert.Equal("Multi-line mode", composer.PlaceholderText);

        inputQueue.Dispose();
    }
}
