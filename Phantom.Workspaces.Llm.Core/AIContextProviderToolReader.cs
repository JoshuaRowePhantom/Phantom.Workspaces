using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public static class AIContextProviderToolReader
{
    public static async Task<AITool[]> GetToolsAsync(
        AIContextProvider provider,
        AIAgent agent,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable MAAI001
        var context = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                agent,
                session,
                new AIContext()),
            cancellationToken);
#pragma warning restore MAAI001

        return context.Tools?.ToArray() ?? [];
    }
}
