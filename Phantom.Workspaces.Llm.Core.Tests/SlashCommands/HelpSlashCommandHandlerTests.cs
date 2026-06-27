using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

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
        var registry = new SlashCommandRegistry();
        var handler = new HelpSlashCommandHandler(registry);
        Assert.Equal("help", handler.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        var registry = new SlashCommandRegistry();
        var handler = new HelpSlashCommandHandler(registry);
        Assert.False(string.IsNullOrWhiteSpace(handler.Description));
    }

    [Fact]
    public async Task ExecuteAsync_WithNoArgument_ListsAllCommands()
    {
        var registry = new SlashCommandRegistry();
        registry.Register(new FakeCommandHandler("alpha", "Does alpha"));
        registry.Register(new FakeCommandHandler("beta", "Does beta"));
        var handler = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Contains("/alpha", result.StatusMessage);
        Assert.Contains("/beta", result.StatusMessage);
        Assert.Contains("Does alpha", result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithKnownCommandName_ShowsDetailedHelp()
    {
        var registry = new SlashCommandRegistry();
        registry.Register(new FakeCommandHandler("alpha", "Does alpha", longDescription: "Alpha long help"));
        var handler = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await handler.ExecuteAsync(context, "alpha", CancellationToken.None);

        Assert.Contains("Alpha long help", result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownCommandName_ReturnsErrorStatus()
    {
        var registry = new SlashCommandRegistry();
        var handler = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await handler.ExecuteAsync(context, "nonexistent", CancellationToken.None);

        Assert.Contains("Unknown command", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/nonexistent", result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithCommandHavingNoLongDescription_FallsBackToDescription()
    {
        var registry = new SlashCommandRegistry();
        registry.Register(new FakeCommandHandler("alpha", "Does alpha", longDescription: null));
        var handler = new HelpSlashCommandHandler(registry);

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
