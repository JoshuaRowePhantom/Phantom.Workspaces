using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Production composition that resolves and builds the unified-transport surfaces so consumers can
/// obtain executor / chat / mcp / shell through the transport layer. It registers and builds
/// <see cref="LocalTransportFactory"/> and <see cref="UserComputerProfileTransportFactory"/>
/// (alongside <see cref="HttpClientTransportFactory"/> and
/// <see cref="ReverseHttpForwardingTransportFactory"/>) into an <see cref="ITransportFactoryRegistry"/>,
/// and exposes the resolved registry plus <see cref="Llm.Core.Transport.TransportTrustedExecutor"/>,
/// <see cref="Services.WorkspacesTransportHost"/> and
/// <see cref="ReverseConnectionStatusRegistry"/> for later resolution by the production consumers.
/// Additive: no existing consumer is switched onto these surfaces yet — the old
/// <c>ReverseExecutionRegistry</c> / <c>CreateSelector</c> stack remains wired.
/// </summary>
public sealed class WorkspacesTransportComposition : IAsyncDisposable
{
    private readonly HttpClientTransportFactory httpClientTransportFactory;
    private readonly ReverseHttpForwardingTransportFactory reverseHttpForwardingTransportFactory;
    private readonly LocalTransportFactory localTransportFactory;
    private readonly UserComputerProfileTransportFactory userComputerProfileTransportFactory;

    public WorkspacesTransportComposition(
        IDataAccessLayer dataAccessLayer,
        WorkspaceEntitySession workspaceEntitySession,
        IReadOnlyList<ReverseHttpClientTransportFactory>? hubFactories = null,
        AgentServices? agentServices = null)
    {
        ArgumentNullException.ThrowIfNull(dataAccessLayer);
        ArgumentNullException.ThrowIfNull(workspaceEntitySession);

        this.ConnectionStatusRegistry = new ReverseConnectionStatusRegistry();
        this.LocalListeners = new TransportRegistry();

        // Issue #1314: register the server-side chat-client transport listener in the production
        // composition so that an incoming `chat-client` channel carrying an `agent-definition`
        // is served by building an executor IChatClient per channel via AgentFactory. Mirrors the
        // production listener registration pattern from docs/design/unified-transport-production-cutover.md.
        // The listener owns the per-channel client lifetime (see ChatClientTransportListener).
        this.LocalListeners.Register(new ChatClientTransportListener(
            async (definition, ct) =>
            {
                var result = await AgentFactory.CreateChatClientAsync(
                    definition,
                    agentServices,
                    queueManager: null,
                    cancellationToken: ct).ConfigureAwait(false);
                return result.ChatClient;
            }));

        // Issue #1438 (per-component-executor-binding): register this machine's production remote MCP
        // host so that an incoming `{"type":"mcp","connection":{...}}` channel — opened by a
        // remote-bound McpToolContextProvider on another machine — is served by opening the requested
        // MCP server here and bridging its JSON-RPC back over the channel. Unrecognised connections
        // return null so the listener declines them.
        this.RemoteMcpHostHandler = new RemoteMcpHostHandler(agentServices);
        this.LocalListeners.Register(new Phantom.Workspaces.Transport.Mcp.McpTransportListener(
            this.RemoteMcpHostHandler.OpenAsync));

        // Issue #1443 (per-component-executor-binding): register this machine's client-only Copilot
        // SDK model host so that an incoming `{"type":"copilot-sdk-session"}` channel — opened by a
        // model-bound CopilotSdkChatClient on another machine — is served by building a LOCAL
        // ICopilotClient here and bridging only its SDK session back over the channel. This is
        // distinct from ChatClientTransportListener above, which remotes the whole AgentChat.
        this.LocalListeners.Register(new Phantom.Workspaces.Llm.Core.Transport.Chat.CopilotClientTransportListener(agentServices));

        var registry = new TransportFactoryRegistry();
        this.localTransportFactory = new LocalTransportFactory(this.LocalListeners);
        this.userComputerProfileTransportFactory =
            new UserComputerProfileTransportFactory(dataAccessLayer, workspaceEntitySession, registry);
        this.httpClientTransportFactory = new HttpClientTransportFactory();
        this.reverseHttpForwardingTransportFactory = new ReverseHttpForwardingTransportFactory();

        registry.Register(this.localTransportFactory);
        registry.Register(this.userComputerProfileTransportFactory);
        registry.Register(this.httpClientTransportFactory);
        registry.Register(this.reverseHttpForwardingTransportFactory);
        this.TransportFactoryRegistry = registry;

        this.TrustedExecutor = new TransportTrustedExecutor(registry, new ExecutionTargetResolver());

        this.HubFactories = hubFactories ?? [];
        this.TransportHost = new WorkspacesTransportHost(this.LocalListeners, this.HubFactories);
    }

    /// <summary>The resolved registry that builds transports for every registered descriptor type.</summary>
    public ITransportFactoryRegistry TransportFactoryRegistry { get; }

    /// <summary>The production remote MCP host that serves inbound `mcp` connection channels (issue #1438).</summary>
    public RemoteMcpHostHandler RemoteMcpHostHandler { get; }

    /// <summary>The local listener registry (chat/mcp/shell) that this machine hosts as an executor.</summary>
    public TransportRegistry LocalListeners { get; }

    /// <summary>Transport-layer connection-status surface for inbound reverse-HTTP registrations.</summary>
    public ReverseConnectionStatusRegistry ConnectionStatusRegistry { get; }

    /// <summary>The transport-backed <see cref="Llm.Interfaces.ITrustedExecutor"/> over the registry.</summary>
    public TransportTrustedExecutor TrustedExecutor { get; }

    /// <summary>GUI-side host that registers with hubs and dispatches relayed frames to local listeners.</summary>
    public WorkspacesTransportHost TransportHost { get; }

    /// <summary>The configured reverse-HTTP hub client factories the host registers with.</summary>
    public IReadOnlyList<ReverseHttpClientTransportFactory> HubFactories { get; }

    /// <summary>Starts the GUI-side transport host (hub registration + dispatcher hosting).</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
        => this.TransportHost.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await this.TransportHost.DisposeAsync().ConfigureAwait(false);
        await this.TrustedExecutor.DisposeAsync().ConfigureAwait(false);
        await this.userComputerProfileTransportFactory.DisposeAsync().ConfigureAwait(false);
        await this.localTransportFactory.DisposeAsync().ConfigureAwait(false);
        await this.reverseHttpForwardingTransportFactory.DisposeAsync().ConfigureAwait(false);
        await this.httpClientTransportFactory.DisposeAsync().ConfigureAwait(false);
    }
}
