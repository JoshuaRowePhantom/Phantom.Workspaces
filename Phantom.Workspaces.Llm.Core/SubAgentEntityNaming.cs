using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Derives sub-agent entity names from a dispatcher session's entity name.
/// Sub-agent entity names are hierarchical: the sub-agent's name array is the dispatcher's
/// name array with the sub-agent's slug ID appended as the terminal component. This groups all
/// sub-agents under the dispatcher entity in the entity tree, making namespace collisions across
/// sibling dispatchers impossible.
/// </summary>
public static class SubAgentEntityNaming
{
    /// <summary>
    /// Appends <paramref name="subAgentId"/> as the terminal component of
    /// <paramref name="dispatcherName"/>.
    /// </summary>
    public static EntityName AppendSubAgentId(EntityName dispatcherName, string subAgentId)
    {
        if (string.IsNullOrWhiteSpace(subAgentId))
        {
            throw new ArgumentException("Sub-agent id is required.", nameof(subAgentId));
        }

        return new EntityName([.. dispatcherName.Components, subAgentId]);
    }

    /// <summary>
    /// Expands every dispatcher name form (for example the <c>users/username/...</c> and
    /// <c>users/id/...</c> prefixed forms declared by the agent-session schema) by appending the
    /// sub-agent slug as the terminal component of each.
    /// </summary>
    public static IReadOnlyList<EntityName> ExpandSubAgentNames(
        IEnumerable<EntityName> dispatcherNames,
        string subAgentId)
    {
        ArgumentNullException.ThrowIfNull(dispatcherNames);

        if (string.IsNullOrWhiteSpace(subAgentId))
        {
            throw new ArgumentException("Sub-agent id is required.", nameof(subAgentId));
        }

        return dispatcherNames
            .Select(dispatcherName => AppendSubAgentId(dispatcherName, subAgentId))
            .ToArray();
    }
}
