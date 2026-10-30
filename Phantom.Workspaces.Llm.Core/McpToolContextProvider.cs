using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using AgentSchema;
using System.Text.Json;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Mcp;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// When adding or changing MCP tool connection kinds or options, update the workspace
/// documentation entities: <c>["documentation", "agent-options", "tools"]</c> and
/// <c>["documentation", "agent-options", "connections"]</c>.
/// </summary>
public sealed class McpToolContextProvider : AIContextProvider, IAsyncDisposable
{
    private readonly string stateKey = $"mcp-tool:{Guid.NewGuid():n}";
    private readonly McpTool tool;
    private readonly ILoggerFactory? loggerFactory;
    private readonly AgentServices? services;
    private readonly AgentExecutionTrustContext? trustContext;
    private readonly SemaphoreSlim initializeLock = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();

    // Per-component executor binding (issue #1438): the resolved connection-descriptor this MCP
    // server is bound to, and the production router that connects it. When the descriptor is local
    // (or no router is supplied) the provider connects in-process via <see cref="McpTransportFactory"/>
    // exactly as before; when it is a remote descriptor the provider routes an MCP channel to the
    // bound machine and speaks JSON-RPC over it.
    private readonly JsonElement boundExecutor;
    private readonly ExecutorTargetRouter? router;

    // Test seam: the operation that establishes the MCP connection and lists its tools. Production
    // uses <see cref="ConnectAndListToolsAsync"/> (which connects a live client); unit tests inject a
    // counting stub so the terminal-failure latch and single-initialization caching can be exercised
    // without a live server.
    private readonly Func<CancellationToken, Task<AITool[]>> initializeToolsAsync;

    private McpClient? client;
    private IAsyncDisposable? initializingClientTransport;
    private IAsyncDisposable? connectedClientTransport;
    private AITool[]? cachedTools;
    private IAsyncDisposable? serverCredentialScope;

    // Remote-executor connection resources (issue #1438), disposed with the provider or cleared on
    // <see cref="ResetInitialization"/>. Null for local (in-process) bindings.
    private ITransport? remoteTransport;
    private McpClientOverTransport? remoteChannel;
    private McpChannelClientTransport? remoteClientTransport;

    // Terminal latch: once an initialization attempt fails, further invocations short-circuit and no
    // reconnection (or OAuth browser relaunch) is attempted until an explicit user re-enable calls
    // <see cref="ResetInitialization"/> (issue #1447).
    private bool initializationFailed;
    private bool resetCleanupRequired;

    public McpToolContextProvider(
        McpTool tool,
        ILoggerFactory? loggerFactory,
        ExecutorTarget executorTarget = ExecutorTarget.AgentExecutor,
        AgentServices? services = null,
        JsonElement? boundExecutor = null,
        ExecutorTargetRouter? router = null,
        AgentExecutionTrustContext? trustContext = null)
        : this(tool, loggerFactory, executorTarget, services, boundExecutor, router, initializeOverride: null, trustContext)
    {
    }

    internal McpToolContextProvider(
        McpTool tool,
        ILoggerFactory? loggerFactory,
        ExecutorTarget executorTarget,
        AgentServices? services,
        JsonElement? boundExecutor,
        ExecutorTargetRouter? router,
        Func<CancellationToken, Task<AITool[]>>? initializeOverride,
        AgentExecutionTrustContext? trustContext = null,
        IAsyncDisposable? ownedTransportOverride = null)
        : base(null, null, null)
    {
        this.tool = tool;
        this.loggerFactory = loggerFactory;
        this.services = services;
        this.boundExecutor = boundExecutor ?? ExecutorBindings.LocalDescriptor();
        this.router = router;
        this.trustContext = trustContext;
        this.ExecutorTarget = executorTarget;
        this.initializeToolsAsync = initializeOverride ?? this.ConnectAndListToolsAsync;
        this.connectedClientTransport = ownedTransportOverride;
    }

    /// <summary>
    /// The execution class this MCP server connection is routed to. MCP servers default to
    /// <see cref="ExecutorTarget.AgentExecutor"/> (they run on the executor instance E).
    /// </summary>
    public ExecutorTarget ExecutorTarget { get; }

    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            this.lifetimeCancellation.Token);
        var lifetimeToken = linkedCancellation.Token;
        await this.initializeLock.WaitAsync(lifetimeToken);
        try
        {
            if (this.resetCleanupRequired)
            {
                await this.DisposeConnectionAsync().ConfigureAwait(false);
                this.resetCleanupRequired = false;
            }

            if (this.initializationFailed)
            {
                // Terminal: a previous attempt failed. Do not reconnect (or relaunch the OAuth
                // browser flow) until an explicit user re-enable clears the latch (issue #1447).
                return new AIContext { Tools = [] };
            }

            if (this.cachedTools is null)
            {
                var logger = this.loggerFactory?.CreateLogger<McpToolContextProvider>();
                var serverName = string.IsNullOrWhiteSpace(this.tool.ServerName) ? this.tool.Name : this.tool.ServerName;
                try
                {
                    var tools = await this.InvokeInitializerAsync(lifetimeToken).ConfigureAwait(false);
                    lifetimeToken.ThrowIfCancellationRequested();
                    this.cachedTools = tools;
                }
                catch
                {
                    this.initializationFailed = true;
                    logger?.LogError(
                        "Failed to open MCP server; category open-failed.");
                    throw;
                }
            }

            return new AIContext
            {
                Tools = this.cachedTools,
            };
        }
        finally
        {
            this.initializeLock.Release();
        }
    }

    private async Task<AITool[]> InvokeInitializerAsync(CancellationToken cancellationToken) =>
        await this.initializeToolsAsync(cancellationToken).ConfigureAwait(false);

    private async Task<AITool[]> ConnectAndListToolsAsync(CancellationToken cancellationToken)
    {
        try
        {
            this.client = IsLocalDescriptor(this.boundExecutor) || this.router is null
                ? await this.ConnectLocalAsync(cancellationToken)
                : await this.ConnectRemoteAsync(cancellationToken);

            var mcpTools = await McpClientToolListing.ListToolsAsync(this.client, cancellationToken);
            if (this.tool.AllowedTools is { Count: > 0 })
            {
                var allowedSet = new HashSet<string>(this.tool.AllowedTools, StringComparer.OrdinalIgnoreCase);
                mcpTools = [.. mcpTools.Where(tool => allowedSet.Contains(tool.Name))];
            }

            return mcpTools.Cast<AITool>().ToArray();
        }
        catch
        {
            await this.DisposeConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Local (in-process) binding: connect the MCP client directly through the shared transport
    // factory exactly as before the per-component executor work (behaviour-preserving default).
    private async Task<McpClient> ConnectLocalAsync(CancellationToken cancellationToken)
    {
        var tool = this.tool;
        var services = this.services;
        if (services?.DeferredSecretReferences is { Count: > 0 })
        {
            var serverName = tool.ServerName ?? tool.Name ?? "(mcp server)";
            services.McpCredentialStatusReporter?.Invoke(
                serverName, $"Waiting for authorization for MCP server '{serverName}'.");
            var materialized = await new AgentDefinitionSecretMaterializer().MaterializeServerAsync(
                tool, services, cancellationToken).ConfigureAwait(false);
            this.serverCredentialScope = materialized.Resolver;
            tool = (McpTool)AgentFactory.ExtractTools(materialized.Definition)!.Single();
            services = services with
            {
                SecretPlaceholderResolver = materialized.Resolver,
                DeferredSecretReferences = [],
            };
        }
        var transport = await McpTransportFactory.CreateMcpTransportAsync(
            tool,
            services,
            this.loggerFactory,
            cancellationToken,
            this.trustContext,
            this.services?.ProcessExecutor as IProcessExecutor);
        var disposableTransport = transport as IAsyncDisposable;
        this.initializingClientTransport = disposableTransport;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = await McpClient.CreateAsync(transport, null, this.loggerFactory, cancellationToken);
            this.connectedClientTransport = disposableTransport;
            Interlocked.CompareExchange(ref this.initializingClientTransport, null, disposableTransport);
            return client;
        }
        catch (Exception exception) when ((this.tool.Connection is OAuthConnection or ApiKeyConnection) &&
            exception is McpException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            throw new InvalidOperationException("MCP credential-bound connection failed.");
        }
        finally
        {
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref this.initializingClientTransport, null, disposableTransport),
                disposableTransport) && disposableTransport is not null)
            {
                await disposableTransport.DisposeAsync();
            }
        }
    }

    internal async Task ReconnectAfterCredentialCommitAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, this.lifetimeCancellation.Token);
        await this.initializeLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            await this.DisposeConnectionAsync().ConfigureAwait(false);
            this.initializationFailed = false;
            this.resetCleanupRequired = false;
            this.cachedTools = await this.InvokeInitializerAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch
        {
            this.initializationFailed = true;
            this.cachedTools = null;
            throw;
        }
        finally
        {
            this.initializeLock.Release();
        }
    }

    // Remote binding (issue #1438): route an MCP channel to the bound machine via the production
    // ExecutorTargetRouter, wrap it as an MCP SDK client transport, and complete the JSON-RPC
    // handshake over it. The remote host (RemoteMcpHostHandler) opens the real MCP server there.
    private async Task<McpClient> ConnectRemoteAsync(CancellationToken cancellationToken)
    {
        var reference = this.trustContext?.RemoteReference;
        if (this.trustContext is not null && reference is null)
        {
            throw new InvalidOperationException(
                "Remote MCP execution requires a revisioned trust-profile reference; "
                + "an inline trust profile cannot be verified by the launch host.");
        }
        var request = reference is null
            ? McpConnectionRequest.FromTool(this.tool)
            : McpConnectionRequest.FromToolWithTrustProfileReference(
                this.tool,
                reference.Id,
                reference.ExpectedRevision);
        this.remoteTransport = await this.router!.ConnectToDescriptorAsync(this.boundExecutor, cancellationToken);

        this.remoteChannel = new McpClientOverTransport(this.remoteTransport, request);
        await this.remoteChannel.OpenAsync(cancellationToken);

        var serverName = string.IsNullOrWhiteSpace(this.tool.ServerName) ? this.tool.Name : this.tool.ServerName;
        this.remoteClientTransport = new McpChannelClientTransport(this.remoteChannel.Channel, serverName);
        return await McpClient.CreateAsync(this.remoteClientTransport, null, this.loggerFactory, cancellationToken);
    }

    private static bool IsLocalDescriptor(JsonElement descriptor)
    {
        if (descriptor.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        return !descriptor.TryGetProperty("type", out var type)
            || string.Equals(type.GetString(), ExecutionTargetResolver.LocalDescriptorType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Clears the terminal initialization-failure latch so that exactly one fresh connection attempt
    /// is made on the next invocation. Called when the user explicitly re-enables a server that
    /// previously failed to initialize (issue #1447). A healthy, already-connected provider is left
    /// untouched so its cached client is not needlessly discarded.
    /// </summary>
    public void ResetInitialization()
    {
        if (!this.initializationFailed)
        {
            return;
        }

        this.initializationFailed = false;
        this.cachedTools = null;
        this.resetCleanupRequired = true;
    }

    public async ValueTask DisposeAsync()
    {
        await this.lifetimeCancellation.CancelAsync();
        var initializingClientTransport = Interlocked.Exchange(ref this.initializingClientTransport, null);
        if (initializingClientTransport is not null)
        {
            await initializingClientTransport.DisposeAsync();
        }

        await this.initializeLock.WaitAsync();
        this.initializeLock.Release();

        await this.DisposeConnectionAsync();
        this.initializeLock.Dispose();
        this.lifetimeCancellation.Dispose();
    }

    private async ValueTask DisposeConnectionAsync()
    {
        var client = Interlocked.Exchange(ref this.client, null);
        var transport = Interlocked.Exchange(ref this.connectedClientTransport, null);
        var credentialScope = Interlocked.Exchange(ref this.serverCredentialScope, null);
        try
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (transport is not null)
                    await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (credentialScope is not null)
                        await credentialScope.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await this.DisposeRemoteAsync().ConfigureAwait(false);
                }
            }
        }
    }

    // Tears down the remote-executor connection resources (issue #1438). The MCP SDK client owns and
    // disposes the client transport (and thus the underlying channel); the routed transport is
    // disposed here. All disposals are idempotent so overlap with the SDK client is safe.
    private async ValueTask DisposeRemoteAsync()
    {
        if (this.remoteClientTransport is not null)
        {
            await this.remoteClientTransport.DisposeAsync();
            this.remoteClientTransport = null;
        }

        if (this.remoteChannel is not null)
        {
            await this.remoteChannel.DisposeAsync();
            this.remoteChannel = null;
        }

        if (this.remoteTransport is not null)
        {
            await this.remoteTransport.DisposeAsync();
            this.remoteTransport = null;
        }
    }

    /// <summary>
    /// Preserved API for the #1379 stdio-<c>env</c> tests. Forwards to the shared
    /// <see cref="Mcp.McpTransportFactory"/>, which is the single source of truth for stdio option
    /// construction (command, args, cwd, and env NAME=value handling).
    /// </summary>
    internal static StdioClientTransportOptions BuildStdioTransportOptions(Uri endpointUri, string? serverName)
        => McpTransportFactory.BuildStdioTransportOptions(endpointUri, serverName);
}
