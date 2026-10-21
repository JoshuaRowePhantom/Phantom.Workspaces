using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.Services.Updates;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Services;

public sealed class ApplicationServices
{
    public ApplicationServices(
        IRunningAgentChatTable runningAgentChats,
        IAgentPersistenceStoreCache agentPersistenceStoreCache,
        ILoggerFactory loggerFactory,
        IAccessBroker accessBroker,
        IUpdateController? updateController = null,
        ILogDirectoryProvider? logDirectoryProvider = null,
        ConfigurationPersistenceService? configurationPersistence = null,
        IMcpOAuthCredentialRequestCatalog? mcpOAuthCredentials = null,
        TransportFactoryRegistryProvider? transportFactoryRegistryProvider = null,
        string? updateUnavailableReason = null,
        ITransportFactory? outboundHttpTransportFactory = null,
        Func<AccessPresentationScope>? accessPresentationScope = null)
    {
        ArgumentNullException.ThrowIfNull(accessBroker);
        this.RunningAgentChats = runningAgentChats;
        this.AgentPersistenceStoreCache = agentPersistenceStoreCache;
        this.UpdateController = updateController;
        this.UpdateUnavailableReason = updateUnavailableReason;
        this.LoggerFactory = loggerFactory;
        this.LogDirectoryProvider = logDirectoryProvider;
        this.ConfigurationPersistence = configurationPersistence;
        this.McpOAuthCredentials = mcpOAuthCredentials;
        this.TransportFactoryRegistryProvider =
            transportFactoryRegistryProvider ?? new TransportFactoryRegistryProvider();
        this.OutboundHttpTransportFactory = outboundHttpTransportFactory;
        this.AccessBroker = accessBroker;
        this.AccessPresentationScope = accessPresentationScope ?? (() => Llm.Interfaces.Access.AccessPresentationScope.Runtime);
    }

    public IRunningAgentChatTable RunningAgentChats { get; }

    public IAccessBroker AccessBroker { get; }
    public Func<AccessPresentationScope> AccessPresentationScope { get; }
    internal Func<RemoteAuthentication?, IAccessBroker, Func<AccessPresentationScope>,
        DevTunnel.IDevTunnelHostService>? DevTunnelHostServiceFactory { get; init; }
    internal Func<RepositorySource, CredentialUseKind, DevTunnelIdentityRoute?>? DevTunnelIdentityRouteFactory { get; init; }

    internal IRemoteCopilotProviderCatalog? RemoteCopilotProviderCatalog { get; init; }

    public IAgentPersistenceStoreCache AgentPersistenceStoreCache { get; }

    public IUpdateController? UpdateController { get; private set; }

    /// <summary>Reason provided by startup when this run cannot use the update controller.</summary>
    public string? UpdateUnavailableReason { get; private set; }

    internal void SetUpdateCapability(IUpdateController? controller, string? unavailableReason)
    {
        this.UpdateController = controller;
        this.UpdateUnavailableReason = unavailableReason;
    }

    /// <summary>
    /// The process logger factory backed by the rolling file provider.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>The single log-directory resolver for this process, or <c>null</c> when unwired.</summary>
    public ILogDirectoryProvider? LogDirectoryProvider { get; }

    /// <summary>
    /// The configuration persistence service used to save runtime user preferences (for example,
    /// the pinned AI-usage metric selection). Null in tests that don't exercise persistence.
    /// </summary>
    public ConfigurationPersistenceService? ConfigurationPersistence { get; }

    public IConfigurationStore? ConfigurationStore => this.ConfigurationPersistence;


    public IMcpOAuthCredentialRequestCatalog? McpOAuthCredentials { get; }

    public Func<AccessPresentationScope> AccessPresentationScopeProvider => this.AccessPresentationScope;

    public TransportFactoryRegistryProvider TransportFactoryRegistryProvider { get; }

    /// <summary>Optional HTTP factory transferred to transport composition for authenticated forwarding.</summary>
    public ITransportFactory? OutboundHttpTransportFactory { get; }

    /// <summary>
    /// The canonical URL-opening service (#1172). Populated post-construction by <c>App.axaml.cs</c>
    /// after <c>MainWindowViewModel</c> exists (the opener depends on the view model as
    /// <see cref="Phantom.Workspaces.ViewModels.IWorkspaceTabService"/>). Null in tests that don't
    /// exercise URL opening.
    /// </summary>
    public IUrlOpener? UrlOpener { get; private set; }

    /// <summary>
    /// Post-construction slot for <see cref="UrlOpener"/>. Breaks the
    /// <c>MainWindowViewModel</c> ↔ <c>UrlOpener</c> construction cycle (the opener is
    /// created after the view model, then registered here).
    /// </summary>
    internal void SetUrlOpener(IUrlOpener opener)
    {
        this.UrlOpener = opener;
    }
}
