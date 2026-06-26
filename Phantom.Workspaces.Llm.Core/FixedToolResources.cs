namespace Phantom.Workspaces.Llm;

/// <summary>
/// Well-known tool resource identifiers and names for built-in toolsets.
/// </summary>
public static class FixedToolResources
{
    /// <summary>
    /// The tool resource <c>id</c> value used for built-in (fixed) toolsets.
    /// </summary>
    public const string FixedToolResourceId = "fixed";

    /// <summary>The workspace entity toolset name.</summary>
    public const string WorkspaceEntity = "workspace-entity";

    /// <summary>The workspace GUI toolset name (open/close panes and tabs, invoke entity shortcuts).</summary>
    public const string WorkspaceGui = "workspace-gui";

    /// <summary>The filesystem toolset name.</summary>
    public const string Filesystem = "filesystem";

    /// <summary>The web search toolset name.</summary>
    public const string WebSearch = "web_search";

    /// <summary>The web request toolset name.</summary>
    public const string WebRequest = "web_request";

    /// <summary>The combined web toolset name.</summary>
    public const string Web = "web";

    /// <summary>
    /// The default set of built-in toolset names exposed as fixed tool resources.
    /// </summary>
    // NOTE: Update docs/JsonEntities/documentation/agent-configuration.md when built-in tool names change.
    public static IReadOnlyList<string> DefaultNames { get; } =
    [
        WorkspaceEntity,
        WorkspaceGui,
        Filesystem,
        WebSearch,
        WebRequest,
        Web,
    ];
}
