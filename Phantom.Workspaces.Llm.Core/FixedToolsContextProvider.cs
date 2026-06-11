using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public sealed class FixedToolsContextProvider : AIContextProvider
{
    private readonly string stateKey = $"fixed-tools:{Guid.NewGuid():n}";
    private readonly AITool[] tools;

    public FixedToolsContextProvider(params AITool[] tools)
        : base(null, null, null)
    {
        this.tools = tools;
    }

    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(new AIContext { Tools = this.tools });
    }
}
