using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class SlashCommandRegistryTests
{
    [Fact]
    public void Register_Handler_AppearsInCommands()
    {
        var registry = new SlashCommandRegistry();
        var handler = new StubHandler("test");

        registry.Register(handler);

        Assert.Contains(registry.Commands, c => c.Name == "test");
    }

    [Fact]
    public void Register_SubRegistry_CommandsIncludesSubRegistryCommands()
    {
        var parent = new SlashCommandRegistry();
        var child = new SlashCommandRegistry();
        child.Register(new StubHandler("child-cmd"));

        parent.Register(child);

        Assert.Contains(parent.Commands, c => c.Name == "child-cmd");
    }

    [Fact]
    public void Register_MultipleSubRegistries_AllCommandsEnumerated()
    {
        var parent = new SlashCommandRegistry();
        var child1 = new SlashCommandRegistry();
        var child2 = new SlashCommandRegistry();
        child1.Register(new StubHandler("cmd-a"));
        child2.Register(new StubHandler("cmd-b"));

        parent.Register(child1);
        parent.Register(child2);

        Assert.Contains(parent.Commands, c => c.Name == "cmd-a");
        Assert.Contains(parent.Commands, c => c.Name == "cmd-b");
    }

    [Fact]
    public void Register_Handler_NullThrows()
    {
        var registry = new SlashCommandRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Register((ISlashCommandHandler)null!));
    }

    [Fact]
    public void Register_SubRegistry_NullThrows()
    {
        var registry = new SlashCommandRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Register((ISlashCommandRegistry)null!));
    }

    private sealed class StubHandler : ISlashCommandHandler
    {
        public StubHandler(string name) => this.Name = name;
        public string Name { get; }
        public string Description => "stub";
        public string? Usage => null;
        public string? LongDescription => null;

        public Task<SlashCommandResult> ExecuteAsync(
            SlashCommandContext context, string arguments, CancellationToken cancellationToken)
            => Task.FromResult(new SlashCommandResult { StatusMessage = string.Empty });

        public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
            SlashCommandContext context, string partialArguments, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>([]);
    }
}
