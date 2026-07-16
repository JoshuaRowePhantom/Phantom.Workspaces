using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class ReplaceableSlashCommandHandlerRegistryTests
{
    [Fact]
    public void Current_WhenReplaced_CommandsReflectsNewRegistry()
    {
        var replaceable = new ReplaceableSlashCommandHandlerRegistry();
        var newInner = new SlashCommandRegistry();
        newInner.Register(new StubHandler("new-cmd"));

        replaceable.Current = newInner;

        Assert.Contains(replaceable.Commands, c => c.Name == "new-cmd");
    }

    [Fact]
    public void Current_WhenReplaced_OldHandlersNoLongerInCommands()
    {
        var replaceable = new ReplaceableSlashCommandHandlerRegistry();
        var oldInner = new SlashCommandRegistry();
        oldInner.Register(new StubHandler("old-cmd"));
        replaceable.Current = oldInner;

        var newInner = new SlashCommandRegistry();
        newInner.Register(new StubHandler("new-cmd"));
        replaceable.Current = newInner;

        Assert.DoesNotContain(replaceable.Commands, c => c.Name == "old-cmd");
        Assert.Contains(replaceable.Commands, c => c.Name == "new-cmd");
    }

    [Fact]
    public void Register_DelegatesToCurrent()
    {
        var replaceable = new ReplaceableSlashCommandHandlerRegistry();
        var handler = new StubHandler("delegated");

        replaceable.Register(handler);

        Assert.Contains(replaceable.Current.Commands, c => c.Name == "delegated");
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
