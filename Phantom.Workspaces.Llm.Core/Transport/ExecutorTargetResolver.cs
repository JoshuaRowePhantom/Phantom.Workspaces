using AgentSchema;

namespace Phantom.Workspaces.Llm.Core.Transport;

/// <summary>
/// Maps a tool's <see cref="Tool.Kind"/> to the <see cref="ExecutorTarget"/> execution class it must
/// run in. Tagging happens at tool construction time (from the static tool <c>kind</c>), never at
/// call time. Unknown kinds — including <c>mcp</c> and <c>function</c> — default to
/// <see cref="ExecutorTarget.AgentExecutor"/>.
/// </summary>
public static class ExecutorTargetResolver
{
    /// <summary>The tool kind for workspace GUI tools (open/close panes and tabs, invoke shortcuts).</summary>
    public const string WorkspaceGuiKind = "workspace-gui";

    /// <summary>The tool kind for workspace entity tools.</summary>
    public const string WorkspaceEntityKind = "workspace-entity";

    /// <summary>The tool kind for the agent-session toolset (as landed).</summary>
    public const string AgentSessionKind = "agent-session";

    /// <summary>The pre-cutover alias for the agent-session toolset used in the original design.</summary>
    public const string WorkspaceAgentSessionKind = "workspace-agent-session";

    /// <summary>The MCP tool kind.</summary>
    public const string McpKind = "mcp";

    /// <summary>The function tool kind.</summary>
    public const string FunctionKind = "function";

    /// <summary>Resolves the execution class for a tool <paramref name="kind"/> discriminator.</summary>
    public static ExecutorTarget ForKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return ExecutorTarget.AgentExecutor;
        }

        if (Matches(kind, WorkspaceGuiKind) || Matches(kind, WorkspaceEntityKind))
        {
            return ExecutorTarget.GuiLocal;
        }

        if (Matches(kind, AgentSessionKind) || Matches(kind, WorkspaceAgentSessionKind))
        {
            return ExecutorTarget.HostingInstance;
        }

        // mcp, function, and every other/unknown kind default to the agent executor.
        return ExecutorTarget.AgentExecutor;
    }

    /// <summary>Resolves the execution class for a constructed <paramref name="tool"/>.</summary>
    public static ExecutorTarget ForTool(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return ForKind(tool.Kind);
    }

    private static bool Matches(string kind, string expected)
        => string.Equals(kind, expected, StringComparison.OrdinalIgnoreCase);
}
