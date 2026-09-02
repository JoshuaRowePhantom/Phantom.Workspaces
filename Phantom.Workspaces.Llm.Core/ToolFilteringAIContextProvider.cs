using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Linq;

namespace Phantom.Workspaces.Llm;

public sealed class ToolFilteringAIContextProvider : AIContextProvider
{
    private readonly string stateKey = $"tool-filtering:{Guid.NewGuid():n}";
    private readonly AIContextProvider provider;
    private readonly Func<AITool, bool> isEnabled;

    public ToolFilteringAIContextProvider(
        AIContextProvider provider,
        Func<AITool, bool> isEnabled)
        : base(null, null, null)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
    }

    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        AIContext underlyingContext;
        try
        {
#pragma warning disable MAAI001
            underlyingContext = await this.provider.InvokingAsync(
                new AIContextProvider.InvokingContext(
                    context.Agent,
                    context.Session,
                    new AIContext()),
                cancellationToken);
#pragma warning restore MAAI001
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A context provider that fails to yield tools must not crash the turn — it simply
            // contributes no tools (issue #1395). This matters for an MCP server that failed to
            // open/connect: its provider is registered for exposure but its lazy connection throws
            // at request time. The failure was already surfaced as a diagnostic when the tool tree
            // was built; here we degrade gracefully so the model still gets every other tool.
            return new AIContext { Tools = [] };
        }

        var tools = underlyingContext.Tools?.ToArray() ?? [];
        if (tools.Length == 0)
        {
            underlyingContext.Tools = [];
            return underlyingContext;
        }

        underlyingContext.Tools = tools
            .Where(this.isEnabled)
            .ToArray();
        return underlyingContext;
    }
}
