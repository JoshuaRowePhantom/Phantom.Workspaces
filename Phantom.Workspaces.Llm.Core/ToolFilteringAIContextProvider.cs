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

#pragma warning disable MAAI001
        var underlyingContext = await this.provider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                context.Agent,
                context.Session,
                new AIContext()),
            cancellationToken);
#pragma warning restore MAAI001

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
