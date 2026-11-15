using Microsoft.Agents.AI;

namespace Phantom.Workspaces.Llm;

internal static class AgentResponseUpdateCoalescer
{
    public static AgentChatHistoryItem[] Coalesce(
        AgentResponseUpdate[] updates,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (updates.Length == 0) return [];
        var accumulator = new StreamingResponseAccumulator(timeProvider);
        foreach (var update in updates) accumulator.Add(update);
        return accumulator.Snapshot();
    }
}
