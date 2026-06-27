namespace Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

/// <summary>
/// Well-known fields on the agent status line that <see cref="StatusUpdate"/> can modify.
/// </summary>
public enum AgentStatusField
{
    /// <summary>Short description of what the agent is currently doing (e.g. from <c>report_intent</c>).</summary>
    Intent,
}

/// <summary>
/// Returned by <see cref="IToolVisualizerFactory.Visualize"/> to push a live status value to the
/// agent status line without (or in addition to) emitting a chat content block.
/// When <see cref="ChatSummary"/> is non-null a collapsed <c>&lt;details&gt;</c> block is also
/// emitted with that label; when it is null no chat block is produced at all.
/// </summary>
public sealed record StatusUpdate(
    AgentStatusField Field,
    string? Value,
    string? ChatSummary = null);
