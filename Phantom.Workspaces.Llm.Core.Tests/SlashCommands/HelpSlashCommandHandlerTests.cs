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

    [Fact]
    public async Task ExecuteAsync_WithCommandHavingGetHelpAsyncOverride_UsesOverriddenHelp()
    {
        var registry = new SlashCommandRegistry();
        registry.Register(new FakeCommandHandler("alpha", "Does alpha", longDescription: "static help", dynamicHelp: "dynamic help"));
        var handler = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var result = await handler.ExecuteAsync(context, "alpha", CancellationToken.None);

        Assert.Contains("dynamic help", result.StatusMessage);
        Assert.DoesNotContain("static help", result.StatusMessage);
    }

    [Fact]
    public async Task GetCompletionsAsync_WithEmptyPartialArguments_ReturnsAllCommandsAlphabetically()
    {
        var registry = new SlashCommandRegistry();
        registry.Register(new FakeCommandHandler("beta", "Does beta"));
        registry.Register(new FakeCommandHandler("alpha", "Does alpha"));
        var handler = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        Assert.Equal(2, completions.Count);
        Assert.Equal("alpha", completions[0].CompletionText);
        Assert.Equal("beta", completions[1].CompletionText);
    }

    [Fact]
    public async Task GetCompletionsAsync_WithMatchingPrefix_ReturnsOnlyMatchingCommands()
    {
        var registry = new SlashCommandRegistry();
        registry.Register(new FakeCommandHandler("alpha", "Does alpha"));
        registry.Register(new FakeCommandHandler("beta", "Does beta"));
        registry.Register(new FakeCommandHandler("aleph", "Does aleph"));
        var handler = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var completions = await handler.GetCompletionsAsync(context, "al", CancellationToken.None);

        Assert.Equal(2, completions.Count);
        Assert.All(completions, c => Assert.StartsWith("al", c.CompletionText, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetCompletionsAsync_WithNonMatchingPrefix_ReturnsEmpty()
    {
        var registry = new SlashCommandRegistry();
        registry.Register(new FakeCommandHandler("alpha", "Does alpha"));
        var handler = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var completions = await handler.GetCompletionsAsync(context, "zzz", CancellationToken.None);

        Assert.Empty(completions);
    }

    [Fact]
    public async Task GetCompletionsAsync_WithExactMatchAndSpace_DelegatesToSubHandler()
    {
        var subCompletions = new[]
        {
            new SlashCommandCompletion("sub-completion", "/sub", "Sub desc"),
        };
        var registry = new SlashCommandRegistry();
        registry.Register(new FakeCommandHandler("alpha", "Does alpha", subCompletions: subCompletions));
        var handler = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var completions = await handler.GetCompletionsAsync(context, "alpha partial", CancellationToken.None);

        Assert.Single(completions);
        Assert.Equal("sub-completion", completions[0].CompletionText);
    }

    [Fact]
    public async Task GetCompletionsAsync_CompletionItems_HaveCorrectLabelAndDescription()
    {
        var registry = new SlashCommandRegistry();
        registry.Register(new FakeCommandHandler("alpha", "Does alpha"));
        var handler = new HelpSlashCommandHandler(registry);

        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };
        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        Assert.Single(completions);
        Assert.Equal("/alpha", completions[0].Label);
        Assert.Equal("Does alpha", completions[0].Description);
    }

    private sealed class FakeCommandHandler : ISlashCommandHandler
    {
        private readonly string? longDescription;
        private readonly string? dynamicHelp;
        private readonly IReadOnlyList<SlashCommandCompletion>? subCompletions;

        public FakeCommandHandler(
            string name,
            string description,
            string? longDescription = null,
            string? dynamicHelp = null,
            IReadOnlyList<SlashCommandCompletion>? subCompletions = null)
        {
            this.Name = name;
            this.Description = description;
            this.longDescription = longDescription;
            this.dynamicHelp = dynamicHelp;
            this.subCompletions = subCompletions;
        }

        public string Name { get; }
        public string Description { get; }
        public string? Usage => null;
        public string? LongDescription => this.longDescription;

        public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string arguments, CancellationToken cancellationToken)
            => Task.FromResult(new SlashCommandResult { StatusMessage = "executed" });

        public Task<string> GetHelpAsync(SlashCommandContext context, string partialArguments, CancellationToken cancellationToken)
            => Task.FromResult(this.dynamicHelp ?? this.longDescription ?? this.Description);

        public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(SlashCommandContext context, string partialArguments, CancellationToken cancellationToken)
            => Task.FromResult(this.subCompletions ?? (IReadOnlyList<SlashCommandCompletion>)Array.Empty<SlashCommandCompletion>());
    }
}
