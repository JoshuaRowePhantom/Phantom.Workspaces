namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// A request to execute a scheduled workspace tool on a target client instance via the trusted
/// executor. Carries the tool type name, the tool entity id, and the target client instance so the
/// appropriate executor can route execution to the machine that owns the data.
/// </summary>
public sealed record TrustedToolRequest
{
    /// <summary>The tool type name (e.g. <c>"git-workspace-scan"</c>).</summary>
    public required string ToolTypeName { get; init; }

    /// <summary>The entity id of the tool entity (a GUID string).</summary>
    public required string ToolEntityId { get; init; }

    /// <summary>
    /// The client instance to run the tool on; <c>"."</c> denotes the local instance.
    /// </summary>
    public string TargetClientInstance { get; init; } = TrustProfile.LocalClientInstance;
}
