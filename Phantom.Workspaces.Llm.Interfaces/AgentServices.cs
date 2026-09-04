using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public sealed record AgentServices : IServiceProvider
{
    public bool LogChat { get; init; }

    public bool LogHttpRequests { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Overrides the agent persistence store used by the agent, bypassing the store
    /// configured in the agent definition. Intended for testing.
    /// </summary>
    public IAgentPersistenceStore? AgentPersistenceStoreOverride { get; init; }

    /// <summary>
    /// Overrides the chat client used by the agent. Intended for deterministic tests.
    /// </summary>
    public IChatClient? ChatClientOverride { get; init; }

    /// <summary>
    /// Factory for acquiring leased references to running agent chat sessions.
    /// </summary>
    public IRunningAgentChatFactory? RunningAgentChatFactory { get; init; }

    /// <summary>
    /// Overrides toolset factory resolution for custom tool kinds.
    /// </summary>
    public IToolsetFactory? ToolsetFactory { get; init; }

    /// <summary>
    /// Factory used to resolve the tool resources referenced by an agent manifest into concrete
    /// tools. Used when a <see cref="CreateAgentChatRequest"/> supplies an agent manifest without
    /// its own tool resource factory.
    /// </summary>
    public IToolResourceFactory? ToolResourceFactory { get; init; }

    /// <summary>
    /// Service that auto-creates and persists a <c>user-account</c> entity the first time a GitHub
    /// Copilot session is established for a token. Threaded into <see cref="CopilotSdkChatClient"/>
    /// by the agent factory so account entities actually materialize during normal Copilot use.
    /// </summary>
    public IGitHubAccountUpsertService? AccountUpsertService { get; init; }

    /// <summary>
    /// Optional slash command registry for component self-registration. Typed as <see langword="object"/>
    /// to avoid a reverse project reference from <c>Phantom.Workspaces.Llm.Interfaces</c> to
    /// <c>Phantom.Workspaces.Llm.Core</c>; consuming code casts to <c>ISlashCommandRegistry</c>.
    /// </summary>
    public object? SlashCommandRegistry { get; init; }

    /// <summary>
    /// An already-resolved host session context (user / computer / profile) the host hands in so the
    /// running-agent / Copilot path can serve <c>get_current_session</c> with populated members instead
    /// of a session-id-only context (issue #1236). Typed as <see langword="object"/> to avoid a reverse
    /// project reference from <c>Phantom.Workspaces.Llm.Interfaces</c> to <c>Phantom.Workspaces.Llm.Core</c>;
    /// consuming code casts to <c>CurrentSessionContext</c>.
    /// </summary>
    public object? CurrentSessionContext { get; init; }

    /// <summary>
    /// Optional secret provider used by the core secret materialization seam. Typed as
    /// <see langword="object"/> to avoid a reverse project reference from
    /// <c>Phantom.Workspaces.Llm.Interfaces</c> to <c>Phantom.Workspaces.Llm.Core</c>; consuming
    /// code casts to <c>ISecretProvider</c>.
    /// </summary>
    public object? SecretProvider { get; init; }

    /// <summary>
    /// Per-materialization secret placeholder resolver. Typed as <see langword="object"/> for the
    /// same layering reason as <see cref="SecretProvider"/>; consuming code casts to
    /// <c>ISecretPlaceholderResolver</c>.
    /// </summary>
    public object? SecretPlaceholderResolver { get; init; }

    /// <summary>
    /// Optional MCP OAuth seam bundle (redirect-delegate provider, optional redirect URI, optional
    /// token-cache provider). Typed as <see langword="object"/> for the same layering reason as
    /// <see cref="SecretProvider"/> — <c>Phantom.Workspaces.Llm.Interfaces</c> must not reference the
    /// MCP SDK or <c>Phantom.Workspaces.Llm.Core</c>; consuming code casts to
    /// <c>Phantom.Workspaces.Llm.Mcp.McpOAuthOptions</c>. When null, the MCP transport factory uses
    /// its safe default (a redirect delegate that throws a clear "interactive OAuth not configured"
    /// error and a null token cache so the SDK uses its in-memory cache). Sub-items #1385 (interactive
    /// redirect delegate) and #1384 (persistent token cache) populate this seam.
    /// </summary>
    public object? McpOAuthOptions { get; init; }

    /// <summary>
    /// Optional Copilot SDK client factory. Typed as object to avoid a reverse project reference
    /// from Phantom.Workspaces.Llm.Interfaces to Phantom.Workspaces.Llm.Core; consuming code casts
    /// to <c>ICopilotClientFactory</c>.
    /// </summary>
    public object? CopilotClientFactory { get; init; }

    /// <summary>
    /// Late-bound reference to the current <see cref="AgentChat"/> being constructed, used by the
    /// named <c>agent-session</c> toolset factory (see <see cref="ToolsetFactory.CreateAgentSessionToolsetFactory(IToolsetFactory?)"/>)
    /// to resolve the parent chat when its <c>agent_session_*</c> tools are invoked. Typed as
    /// <see langword="object"/> for the same layering reason as <see cref="CurrentSessionContext"/>;
    /// consuming code casts to <c>AgentChatRef</c>. Populated by <see cref="AgentChatFactory"/> /
    /// <see cref="AgentFactory"/> during <c>AgentChat.CreateAsync</c> wiring. Introduced by #1306.
    /// </summary>
    public object? CurrentAgentChatRef { get; init; }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ILoggerFactory))             return LoggerFactory;
        if (serviceType == typeof(IAgentPersistenceStore))     return AgentPersistenceStoreOverride;
        if (serviceType == typeof(IRunningAgentChatFactory))   return RunningAgentChatFactory;
        if (serviceType == typeof(IToolsetFactory))            return ToolsetFactory;
        if (serviceType == typeof(IToolResourceFactory))       return ToolResourceFactory;
        if (serviceType == typeof(IGitHubAccountUpsertService)) return AccountUpsertService;
        return null;
    }
}
