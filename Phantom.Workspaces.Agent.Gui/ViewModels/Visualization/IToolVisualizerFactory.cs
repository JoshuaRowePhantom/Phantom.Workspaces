namespace Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

/// <summary>
/// Produces a visualization for a single <see cref="ToolVisualizationContext"/>. Return values are
/// interpreted by <see cref="ToolVisualizationInterpreter"/>:
/// <list type="bullet">
///   <item><see cref="Summary"/> — rendered as an expanded <c>&lt;details&gt;</c> block.</item>
///   <item><see cref="StatusUpdate"/> — updates a named field on the agent status line; optionally
///     also emits a collapsed chat block when <see cref="StatusUpdate.ChatSummary"/> is non-null.</item>
///   <item><see langword="null"/> — use the generic collapsible fallback.</item>
/// </list>
/// </summary>
public interface IToolVisualizerFactory
{
    object? Visualize(ToolVisualizationContext context);
}
