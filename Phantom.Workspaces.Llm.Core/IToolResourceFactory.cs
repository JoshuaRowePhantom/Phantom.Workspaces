using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Resolves a <see cref="ToolResource"/> reference into a concrete
/// <see cref="AgentSchema.Tool"/> that can be added to an agent definition.
/// Resolution is context-dependent: the same tool resource may resolve to
/// different tools depending on the current user, machine, or workspace.
/// </summary>
public interface IToolResourceFactory
{
    /// <summary>
    /// Resolves a tool resource into a concrete tool.
    /// </summary>
    /// <param name="toolResource">The tool resource reference to resolve.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>
    /// The resolved tool, or <see langword="null"/> if this factory cannot resolve
    /// the supplied tool resource.
    /// </returns>
    Task<Tool?> ResolveToolResourceAsync(
        ToolResource toolResource,
        CancellationToken cancellationToken = default);
}
