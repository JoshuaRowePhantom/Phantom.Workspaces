namespace Phantom.Workspaces.Llm;

/// <summary>
/// Tuning knobs for the sub-agent dispatcher chat client, along with the set of
/// available sub-agent templates it can instantiate.
/// </summary>
public sealed class SubAgentDispatcherOptions
{
    /// <summary>
    /// The list of agent-definition tool entries extracted from the dispatcher's
    /// AgentDefinition or AgentManifest. Defines the available sub-agent templates.
    /// One entry may be named "default"; if none is, the first entry is the default.
    /// </summary>
    public required IReadOnlyList<AgentDefinitionTool> AgentDefinitionTools { get; init; }

    /// <summary>
    /// Sub-agents not updated within this window are considered stale for fuzzy routing
    /// and will trigger disambiguation instead of silent re-routing. Default: 48 hours.
    /// </summary>
    public TimeSpan RecencyThreshold { get; init; } = TimeSpan.FromHours(48);

    /// <summary>
    /// Minimum cosine-similarity delta between the best and second-best candidate for a
    /// clear-winner determination. Values closer together than this trigger disambiguation.
    /// Default: 0.05.
    /// </summary>
    public double AmbiguityThreshold { get; init; } = 0.05;
}
