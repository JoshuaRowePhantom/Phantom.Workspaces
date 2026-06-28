using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class RootSlashCommandCompletionsHandlerTests
{
    private static FakeRegistry MakeRegistry(params string[] names)
    {
        var registry = new FakeRegistry();
        foreach (var name in names)
        {
            registry.Add(name, $"Description of {name}");
        }

        return registry;
    }

    [Fact]
    public void GetCompletions_WithEmptyString_ReturnsAllCommandsAlphabetically()
    {
        var registry = MakeRegistry("beta", "alpha", "gamma");
        var handler = new RootSlashCommandCompletionsHandler(registry);

        var completions = handler.GetCompletions(string.Empty);

        Assert.Equal(3, completions.Count);
        Assert.Equal("alpha ", completions[0].CompletionText);
        Assert.Equal("beta ", completions[1].CompletionText);
        Assert.Equal("gamma ", completions[2].CompletionText);
    }

    [Fact]
    public void GetCompletions_WithMatchingPrefix_ReturnsOnlyMatchingCommands()
    {
        var registry = MakeRegistry("working-directory", "help", "working-space");
        var handler = new RootSlashCommandCompletionsHandler(registry);

        var completions = handler.GetCompletions("wor");

        Assert.Equal(2, completions.Count);
        Assert.All(completions, c => Assert.StartsWith("wor", c.CompletionText, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCompletions_WithNonMatchingPrefix_ReturnsEmpty()
    {
        var registry = MakeRegistry("help", "working-directory");
        var handler = new RootSlashCommandCompletionsHandler(registry);

        var completions = handler.GetCompletions("zzz");

        Assert.Empty(completions);
    }

    [Fact]
    public void GetCompletions_CompletionText_HasTrailingSpaceAndNoLeadingSlash()
    {
        var registry = MakeRegistry("help");
        var handler = new RootSlashCommandCompletionsHandler(registry);

        var completions = handler.GetCompletions(string.Empty);

        Assert.Single(completions);
        Assert.Equal("help ", completions[0].CompletionText);
        Assert.False(completions[0].CompletionText.StartsWith("/", StringComparison.Ordinal));
    }

    [Fact]
    public void GetCompletions_Label_HasLeadingSlash()
    {
        var registry = MakeRegistry("help");
        var handler = new RootSlashCommandCompletionsHandler(registry);

        var completions = handler.GetCompletions(string.Empty);

        Assert.Single(completions);
        Assert.Equal("/help", completions[0].Label);
    }

    [Fact]
    public void GetCompletions_Description_MatchesHandlerDescription()
    {
        var registry = MakeRegistry("help");
        var handler = new RootSlashCommandCompletionsHandler(registry);

        var completions = handler.GetCompletions(string.Empty);

        Assert.Single(completions);
        Assert.Equal("Description of help", completions[0].Description);
    }

    [Fact]
    public void GetCompletions_IsCaseInsensitive()
    {
        var registry = MakeRegistry("Help", "Working-Directory");
        var handler = new RootSlashCommandCompletionsHandler(registry);

        var completions = handler.GetCompletions("hel");

        Assert.Single(completions);
        Assert.Equal("Help ", completions[0].CompletionText);
    }

    private sealed class FakeRegistry : ISlashCommandRegistry
    {
        private readonly List<ISlashCommandHandler> commands = [];

        public IReadOnlyList<ISlashCommandHandler> Commands => this.commands;

        public void Add(string name, string description)
            => this.commands.Add(new FakeHandler(name, description));

        private sealed class FakeHandler : ISlashCommandHandler
        {
            public FakeHandler(string name, string description)
            {
                this.Name = name;
                this.Description = description;
            }

            public string Name { get; }
            public string Description { get; }
            public string? Usage => null;
            public string? LongDescription => null;

            public System.Threading.Tasks.Task<SlashCommandResult> ExecuteAsync(
                SlashCommandContext context,
                string arguments,
                System.Threading.CancellationToken cancellationToken)
                => System.Threading.Tasks.Task.FromResult(new SlashCommandResult { StatusMessage = string.Empty });
        }
    }
}
