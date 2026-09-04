using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Phantom-owned placeholder <see cref="Tool"/> that stands in for a manifest tool resource that
/// could not be resolved at definition-build time (issue #1417) — for example an
/// <c>mcp-server-entity</c> whose backing entity has been renamed, deleted, or is absent on this
/// machine. Rather than aborting the whole session,
/// <c>AgentFactory.CreateAgentDefinitionAsync</c> keeps the unresolved resource in the tools list as
/// one of these placeholders, preserving ordering/identity and flowing through the single
/// registration pipeline (issue #1395).
/// </summary>
/// <remarks>
/// The placeholder carries no runtime behavior and must never be exposed to the model: it produces
/// no <c>AIContextProvider</c>, so it is never wired into <c>chatOptions.AIContextProviders</c>.
/// It exists only to surface a diagnostic naming the missing <see cref="ResourceId"/>:<see cref="ResourceName"/>.
/// This mirrors the <c>PhantomMcpTool : McpTool</c> subclass pattern introduced in issue #1416.
/// </remarks>
public sealed class UnresolvedToolResourceTool : Tool
{
    /// <summary>The tool <see cref="Tool.Kind"/> discriminator for an unresolved tool resource.</summary>
    public const string KindValue = "unresolved-tool-resource";

    /// <summary>Creates a placeholder tool, stamping the <see cref="KindValue"/> kind.</summary>
    public UnresolvedToolResourceTool()
    {
        this.Kind = KindValue;
    }

    /// <summary>The <c>id</c> of the tool resource that could not be resolved (e.g. <c>mcp-server-entity</c>).</summary>
    public string? ResourceId { get; set; }

    /// <summary>The <c>name</c> of the tool resource that could not be resolved (e.g. <c>IcM</c>).</summary>
    public string? ResourceName { get; set; }
}
