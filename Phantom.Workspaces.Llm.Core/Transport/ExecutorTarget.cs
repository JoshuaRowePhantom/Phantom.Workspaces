namespace Phantom.Workspaces.Llm.Core.Transport;

/// <summary>
/// The execution class a tool is tagged with at construction time. Each class resolves, via an
/// <see cref="ExecutorTopology"/>, to the client instance (machine) the tool must run on.
/// In the common single-machine topology (G == H == E) all three classes resolve to the same
/// local machine, so no additional transport round-trips are introduced.
/// </summary>
public enum ExecutorTarget
{
    /// <summary>
    /// Runs on the executor instance E (resolved from the agent's execution target). The default for
    /// <c>mcp</c> and <c>function</c> tools.
    /// </summary>
    AgentExecutor = 0,

    /// <summary>
    /// Runs on the GUI / initiating machine G. Used by <c>workspace-gui</c> and <c>workspace-entity</c>
    /// tools, which manipulate the initiating user's workspace and must therefore route back to it.
    /// </summary>
    GuiLocal = 1,

    /// <summary>
    /// Runs on the hosting PW instance H that owns the workspace agent session. Used by
    /// <c>agent-session</c> / <c>workspace-agent-session</c> tools and workspace-backend scheduled tools.
    /// </summary>
    HostingInstance = 2,
}
