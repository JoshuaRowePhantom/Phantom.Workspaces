using System.Collections.ObjectModel;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Provides the set of tool resources that are currently available for resolution.
/// Implementations expose a reactive collection so that consumers can observe
/// additions and removals of tool resources (for example, as mcp-server entities
/// are created or deleted in the workspace).
/// </summary>
public interface IToolResourceRepository
{
    /// <summary>
    /// The tool resources currently available. The collection updates reactively
    /// as the underlying source of tool resources changes.
    /// </summary>
    ReadOnlyObservableCollection<ToolResource> ToolResources { get; }
}
