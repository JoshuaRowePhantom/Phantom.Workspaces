using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels.SlashCommands;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class HelpSlashCommandHandlerTests
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
    public void Name_IsHelp()
    {
        var handler = new HelpSlashCommandHandler([]);
        Assert.Equal("help", handler.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        var handler = new HelpSlashCommandHandler([]);
        Assert.False(string.IsNullOrWhiteSpace(handler.Description));
    }

    [AvaloniaFact]
    public async Task ExecuteAsync_WithNoArgument_ListsAllCommands()
    {
        var cmd1 = new FakeCommandHandler("alpha", "Does alpha");
        var cmd2 = new FakeCommandHandler("beta", "Does beta");
        var handler = new HelpSlashCommandHandler([cmd1, cmd2]);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Contains("/alpha", result.StatusMessage);
        Assert.Contains("/beta", result.StatusMessage);
        Assert.Contains("Does alpha", result.StatusMessage);
        Assert.False(result.RequiresAgentRecreation);
    }

    [AvaloniaFact]
    public async Task ExecuteAsync_WithKnownCommandName_ShowsDetailedHelp()
    {
        var cmd = new FakeCommandHandler("alpha", "Does alpha", longDescription: "Alpha long help");
        var handler = new HelpSlashCommandHandler([cmd]);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await handler.ExecuteAsync(context, "alpha", CancellationToken.None);

        Assert.Contains("Alpha long help", result.StatusMessage);
        Assert.False(result.RequiresAgentRecreation);
    }

    [AvaloniaFact]
    public async Task ExecuteAsync_WithUnknownCommandName_ReturnsErrorStatus()
    {
        var handler = new HelpSlashCommandHandler([]);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await handler.ExecuteAsync(context, "nonexistent", CancellationToken.None);

        Assert.Contains("Unknown command", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/nonexistent", result.StatusMessage);
    }

    [AvaloniaFact]
    public async Task ExecuteAsync_WithCommandHavingNoLongDescription_FallsBackToDescription()
    {
        var cmd = new FakeCommandHandler("alpha", "Does alpha", longDescription: null);
        var handler = new HelpSlashCommandHandler([cmd]);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await handler.ExecuteAsync(context, "alpha", CancellationToken.None);

        Assert.Contains("Does alpha", result.StatusMessage);
    }

    private sealed class FakeCommandHandler : ISlashCommandHandler
    {
        private readonly string? longDescription;

        public FakeCommandHandler(string name, string description, string? longDescription = null)
        {
            this.Name = name;
            this.Description = description;
            this.longDescription = longDescription;
        }

        public string Name { get; }
        public string Description { get; }
        public string? Usage => null;
        public string? LongDescription => this.longDescription;

        public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string arguments, CancellationToken cancellationToken)
            => Task.FromResult(new SlashCommandResult { StatusMessage = "executed" });
    }
}
