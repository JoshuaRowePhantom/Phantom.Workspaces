using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Tools;
using Phantom.Workspaces.ViewModels;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Services;

/// <summary>
/// The single composition root that produces the complete <see cref="AgentServices"/> bundle for a
/// launch. Every replacement session bundle forwards the host-owned broker and typed metadata
/// services rather than retaining caller-owned OAuth configuration.
/// </summary>
public static class AgentServicesComposition
{
    /// <summary>
    /// Composes the process-wide host services that every launch path must carry. Used by
    /// <c>App.axaml.cs</c> for the app-level <c>AgentChatFactory</c> seed and reused by
    /// <see cref="ComposeSessionServicesAsync"/> so the session path shares the same instances.
    /// </summary>
    public static AgentServices ComposeHostServices(
        IAccessBroker? accessBroker,
        ILoggerFactory loggerFactory,
        IMcpOAuthCredentialRequestCatalog? mcpOAuthCredentials = null,
        Func<AccessPresentationScope>? accessPresentationScopeProvider = null)
        => new()
        {
            AccessBroker = accessBroker,
            McpOAuthCredentials = mcpOAuthCredentials,
            AccessPresentationScopeProvider = accessPresentationScopeProvider,
            LoggerFactory = loggerFactory,
            ProcessExecutor = new ProcessExecutor(),
            TrustProfilePolicyCompiler = new MxcTrustProfilePolicyCompiler(),
            RemoteCopilotProviderResolver = new EnvironmentRemoteCopilotProviderResolver(),
        };

    /// <summary>
    /// Composes the complete session-launch bundle: the process-wide host services
    /// taken from <see cref="ApplicationServices"/> plus the toolset-factory
    /// chain, the MCP tool-resource factory, the GitHub account-upsert service, the resolved current
    /// session context, the persistence-store override, and the logger factory.
    /// </summary>
    public static async Task<AgentServices> ComposeSessionServicesAsync(
        MainWindowViewModel mainWindowViewModel,
        IAgentPersistenceStore agentPersistenceStore,
        ILoggerFactory loggerFactory,
        string? userComputerProfileOverride = null,
        CancellationToken cancellationToken = default)
        => await ComposeServicesAsync(
            mainWindowViewModel,
            agentPersistenceStore,
            userComputerProfileOverride,
            loggerFactory,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Composes the complete service bundle used when this process starts a persisted session for
    /// a remote attachment. The running-chat factory already owns the persistence store, so this
    /// variant does not replace it.
    /// </summary>
    public static async Task<AgentServices> ComposeRuntimeHostServicesAsync(
        MainWindowViewModel mainWindowViewModel,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
        => await ComposeServicesAsync(
            mainWindowViewModel,
            agentPersistenceStore: null,
            userComputerProfileOverride: null,
            loggerFactory,
            cancellationToken).ConfigureAwait(false);

    private static async Task<AgentServices> ComposeServicesAsync(
        MainWindowViewModel mainWindowViewModel,
        IAgentPersistenceStore? agentPersistenceStore,
        string? userComputerProfileOverride,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var dataAccessLayer = mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer;
        var executionContext = new CurrentExecutionContextProvider(userComputerProfileOverride);

        // Hand the resolved host context to the running-agent / Copilot path so get_current_session
        // is populated there too (issue #1236).
        var currentSessionContext = await CurrentSessionContextFactory.CreateForHostAsync(
            agentSessionId: "unbound",
            dataAccessLayer: dataAccessLayer,
            userName: executionContext.UserName,
            computerName: executionContext.ComputerName,
            effectiveComputerName: executionContext.EffectiveComputerName,
            owningProfileEntityId: mainWindowViewModel.EntityBroker.EntityRepository
                .WorkspaceEntitySession.UserComputerProfileEntityId.ToString(),
            ownershipGeneration: 0,
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

        return ComposeHostServices(
            applicationServices.AccessBroker,
            loggerFactory,
            mcpOAuthCredentials: applicationServices.McpOAuthCredentials,
            accessPresentationScopeProvider: applicationServices.AccessPresentationScopeProvider) with
        {
            AccessBroker = applicationServices.AccessBroker,
            AgentPersistenceStoreOverride = agentPersistenceStore,
            ToolsetFactory = toolsetFactory,
            ToolResourceFactory = toolResourceFactory,
            AccountUpsertService = accountUpsertService,
            CurrentSessionContext = currentSessionContext,
            TrustProfileResolver = new DataAccessLayerTrustProfileResolver(dataAccessLayer),
        };
    }
}
