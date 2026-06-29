using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class ReasoningSlashCommandHandlerTests
{
    private static readonly AgentDefinition EchoAgentDefinition =
        AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    private static Task<AgentChat> CreateChatAsync() =>
        AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
        });

    [Fact]
    public async Task ReasoningSlashCommandHandler_Toggle_FlipsValue()
    {
        var value = true;
        var handler = new ReasoningSlashCommandHandler(() => value, v => value = v);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.False(value);

        await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.True(value);
    }

    [Fact]
    public async Task ReasoningSlashCommandHandler_ToggleKeyword_FlipsValue()
    {
        var value = false;
        var handler = new ReasoningSlashCommandHandler(() => value, v => value = v);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        await handler.ExecuteAsync(context, "toggle", CancellationToken.None);

        Assert.True(value);
    }

    [Fact]
    public async Task ReasoningSlashCommandHandler_OnCommand_SetsTrue()
    {
        var value = false;
        var handler = new ReasoningSlashCommandHandler(() => value, v => value = v);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        await handler.ExecuteAsync(context, "on", CancellationToken.None);

        Assert.True(value);
    }

    [Fact]
    public async Task ReasoningSlashCommandHandler_OffCommand_SetsFalse()
    {
        var value = true;
        var handler = new ReasoningSlashCommandHandler(() => value, v => value = v);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        await handler.ExecuteAsync(context, "off", CancellationToken.None);

        Assert.False(value);
    }

    [Fact]
    public async Task ReasoningSlashCommandHandler_GetCompletionsAsync_EmptyPrefix_ReturnsAllThree()
    {
        var value = false;
        var handler = new ReasoningSlashCommandHandler(() => value, v => value = v);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        Assert.Equal(3, completions.Count);
        Assert.Contains(completions, c => c.CompletionText == "on");
        Assert.Contains(completions, c => c.CompletionText == "off");
        Assert.Contains(completions, c => c.CompletionText == "toggle");
    }

    [Fact]
    public async Task ReasoningSlashCommandHandler_GetCompletionsAsync_FiltersByPrefix()
    {
        var value = false;
        var handler = new ReasoningSlashCommandHandler(() => value, v => value = v);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var completions = await handler.GetCompletionsAsync(context, "o", CancellationToken.None);

        Assert.Equal(2, completions.Count);
        Assert.Contains(completions, c => c.CompletionText == "on");
        Assert.Contains(completions, c => c.CompletionText == "off");
        Assert.DoesNotContain(completions, c => c.CompletionText == "toggle");
    }

    [Fact]
    public async Task ReasoningSlashCommandHandler_AppearsInHelpListing()
    {
        var registry = new SlashCommandRegistry();
        var value = false;
        registry.Register(new ReasoningSlashCommandHandler(() => value, v => value = v));
        var help = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await help.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Contains("reasoning", result.StatusMessage);
    }
}
