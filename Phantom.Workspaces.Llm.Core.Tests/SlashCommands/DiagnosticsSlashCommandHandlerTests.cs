using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class DiagnosticsSlashCommandHandlerTests
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
    public async Task DiagnosticsSlashCommandHandler_Toggle_FlipsValue()
    {
        var value = false;
        var handler = new DiagnosticsSlashCommandHandler(() => value, v => value = v);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.True(value);

        await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.False(value);
    }

    [Fact]
    public async Task DiagnosticsSlashCommandHandler_OnCommand_SetsTrue()
    {
        var value = false;
        var handler = new DiagnosticsSlashCommandHandler(() => value, v => value = v);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        await handler.ExecuteAsync(context, "on", CancellationToken.None);

        Assert.True(value);
    }

    [Fact]
    public async Task DiagnosticsSlashCommandHandler_OffCommand_SetsFalse()
    {
        var value = true;
        var handler = new DiagnosticsSlashCommandHandler(() => value, v => value = v);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        await handler.ExecuteAsync(context, "off", CancellationToken.None);

        Assert.False(value);
    }

    [Fact]
    public async Task DiagnosticsSlashCommandHandler_AppearsInHelpListing()
    {
        var registry = new SlashCommandRegistry();
        var value = false;
        registry.Register(new DiagnosticsSlashCommandHandler(() => value, v => value = v));
        var help = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await help.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Contains("diagnostics", result.StatusMessage);
    }
}
