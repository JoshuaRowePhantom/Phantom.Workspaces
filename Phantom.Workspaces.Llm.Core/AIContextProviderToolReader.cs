using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public static class AIContextProviderToolReader
{
    public static async Task<AIContext> GetContextAsync(
        AIContextProvider provider,
        AIAgent agent,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable MAAI001
        return await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                agent,
                session,
                new AIContext()),
            cancellationToken);
#pragma warning restore MAAI001
    }

    public static async Task<AITool[]> GetToolsAsync(
        AIContextProvider provider,
        AIAgent agent,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        var context = await GetContextAsync(provider, agent, session, cancellationToken);
        return context.Tools?.ToArray() ?? [];
    }
}
