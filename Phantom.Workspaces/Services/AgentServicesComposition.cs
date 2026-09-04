using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Tools;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services;

/// <summary>
/// The single composition root that produces the complete <see cref="AgentServices"/> bundle for a
/// launch (issue #1403). Previously each launch site (the app-level <c>App.axaml.cs</c> and the GUI
/// session-launch <c>AgentSessionShortcutContext</c>) hand-assembled its own bundle, so services
/// silently drifted between paths — the direct root cause of #1401 (dropped <c>SecretProvider</c>
/// materialization) and #1402 (dropped <c>McpOAuthOptions</c>). Routing every path through this one
/// method makes those omissions structurally impossible: there is exactly one place that can forget
/// a service, and it forgets it for everyone (caught by one test) rather than silently for one path.
/// </summary>
public static class AgentServicesComposition
{
    /// <summary>
    /// Composes the process-wide host services (<see cref="AgentServices.SecretProvider"/> and
    /// <see cref="AgentServices.McpOAuthOptions"/>) that every launch path must carry. Used by
    /// <c>App.axaml.cs</c> for the app-level <c>AgentChatFactory</c> seed and reused by
    /// <see cref="ComposeSessionServicesAsync"/> so the session path shares the same instances.
    /// </summary>
    public static AgentServices ComposeHostServices(object? secretProvider, object? mcpOAuthOptions)
        => new()
        {
            SecretProvider = secretProvider,
            McpOAuthOptions = mcpOAuthOptions,
        };

    /// <summary>
    /// Composes the complete session-launch bundle: the process-wide host services (secret provider
    /// + MCP OAuth options taken from <see cref="ApplicationServices"/>) plus the toolset-factory
    /// chain, the MCP tool-resource factory, the GitHub account-upsert service, the resolved current
    /// session context, the persistence-store override, and the logger factory.
    /// </summary>
    public static async Task<AgentServices> ComposeSessionServicesAsync(
        MainWindowViewModel mainWindowViewModel,
        IAgentPersistenceStore agentPersistenceStore,
        string? userComputerProfileOverride = null,
        ObservableLoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        var dataAccessLayer = mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer;
        var executionContext = new CurrentExecutionContextProvider(userComputerProfileOverride);

        // Hand the resolved host context to the running-agent / Copilot path so get_current_session
        // is populated there too (issue #1236).
        var currentSessionContext = await CurrentSessionContextFactory.CreateForHostAsync(
            agentSessionId: string.Empty,
            dataAccessLayer: dataAccessLayer,
            userName: executionContext.UserName,
            computerName: executionContext.ComputerName,
            effectiveComputerName: executionContext.EffectiveComputerName,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var workspaceGuiContextProvider = new WorkspaceGuiContextProvider(
            new WorkspaceGuiContext
            {
                MainWindowViewModel = mainWindowViewModel,
                ShortcutManager = mainWindowViewModel.ShortcutManager,
            });

        // #1306: Register agent-session as a first-class named toolset factory in the root chain
        // so a manifest declaring { "kind": "agent-session" } on a root chat resolves the
        // agent_session_* tools by name — symmetric with web_search / workspace-entity /
        // workspace-gui / current-session.
        var toolsetFactory = ToolsetFactory.CreateWorkspaceEntityToolsetFactory(
            dataAccessLayer,
            ToolsetFactory.CreateWorkspaceGuiToolsetFactory(
                workspaceGuiContextProvider,
                ToolsetFactory.CreateCurrentSessionToolsetFactory(
                    dataAccessLayer,
                    currentSessionContext,
                    ToolsetFactory.CreateAgentSessionToolsetFactory(
                        ToolsetFactory.CreateDefaultToolsetFactory()))));

        // Materialize a user-account entity the first time a Copilot session resolves a GitHub token
        // (issue #1047), which also keeps the AI usage indicator populated (issue #1041).
        var accountUpsertService = new GitHubAccountUpsertService(
            dataAccessLayer,
            new GitHubIdentityResolver());

        var toolResourceFactory = ToolResourceFactory.CreateMcpServerResolution(
            dataAccessLayer,
            executionContext.UserName,
            executionContext.EffectiveComputerName);

        var applicationServices = mainWindowViewModel.ApplicationServices;

        // Reuse ComposeHostServices so SecretProvider (#1401) and McpOAuthOptions (#1402) come from
        // exactly one place, guaranteeing the session path carries the same instances the app-level
        // path does.
        return ComposeHostServices(
            applicationServices.SecretProvider,
            applicationServices.McpOAuthOptions) with
        {
            AgentPersistenceStoreOverride = agentPersistenceStore,
            LoggerFactory = loggerFactory,
            ToolsetFactory = toolsetFactory,
            ToolResourceFactory = toolResourceFactory,
            AccountUpsertService = accountUpsertService,
            CurrentSessionContext = currentSessionContext,
        };
    }
}
