using Microsoft.Agents.AI;

namespace Phantom.Workspaces.Llm;

internal sealed class RuntimeToolSnapshotContextProvider(Func<AIContext> snapshot)
    : AIContextProvider(null, null, null)
{
    private readonly string stateKey = $"runtime-tools:{Guid.NewGuid():n}";
    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(snapshot());
}
