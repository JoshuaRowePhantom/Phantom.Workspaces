using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Mcp;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Production remote MCP host (issue #1438, per-component-executor-binding). It is the server-side
/// counterpart of a remote-bound <see cref="McpToolContextProvider"/>: when an
/// <c>{"type":"mcp","connection":{...}}</c> channel is opened against this machine's
/// <see cref="McpTransportListener"/>, this handler opens exactly that MCP server locally — via the
/// shared <see cref="McpTransportFactory"/>, so stdio/HTTP construction and secret resolution happen
/// in one place — and bridges its JSON-RPC to the incoming channel with a <see cref="DelegatingMcpServer"/>.
/// </summary>
/// <remarks>
/// Reuse-first: the incoming channel is wrapped as an MCP SDK transport with
/// <see cref="McpChannelClientTransport.CreateServerTransport"/> and relayed to the real server
/// transport by the existing <see cref="DelegatingMcpServer"/> message pumps. An unrecognised
/// connection yields <see langword="null"/> so the listener declines the channel. Secret placeholders
/// in the connection resolve in <b>this host's</b> context. When the request is a stored-tool
/// <c>tool-type-name</c> / <c>tool-entity-id</c> reference (issue #1439), the MCP config is resolved
/// on this host through <see cref="AgentServices.ToolResourceFactory"/> — the machine-prefix-first
/// <c>McpServerEntityToolResourceFactory</c> scoped to THIS (the bound executor's) machine — so the
/// remote machine's profile registration wins; an inline connection descriptor is hosted directly.
/// </remarks>
public sealed class RemoteMcpHostHandler
{
    private readonly AgentServices? services;
    private readonly ILoggerFactory? loggerFactory;

    public RemoteMcpHostHandler(AgentServices? services = null)
    {
        this.services = services;
        this.loggerFactory = services?.LoggerFactory;
    }

    /// <summary>
    /// Opens the requested MCP server on this machine and bridges it to <paramref name="channel"/>.
    /// Returns a teardown handle, or <see langword="null"/> when the request is not a recognised MCP
    /// connection (so the caller declines it).
    /// </summary>
    public async Task<IAsyncDisposable?> OpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        try
        {
            return await this.OpenCoreAsync(request, channel, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException(
                "Remote MCP launch was denied by host policy.");
        }
    }

    private async Task<IAsyncDisposable?> OpenCoreAsync(
        JsonElement request,
        IMessageChannel channel,
        CancellationToken ct)
    {
        // #1477: never accept a caller-supplied compiled policy. Policy compilation is authoritative
        // on this launch host only.
        McpConnectionRequest.RejectCompiledPolicyProperty(request);

        // Parse security-sensitive intent before deciding whether the connection is hostable. A
        // partial or malformed descriptor must never be reinterpreted as an unconstrained request.
        var trustContext = await this.ResolveTrustContextAsync(request, ct).ConfigureAwait(false);

        var tool = await this.ResolveConnectionAsync(request, ct).ConfigureAwait(false);
        if (tool is null)
        {
            return null;
        }

        var serverTransport = await McpTransportFactory.CreateMcpTransportAsync(
            tool,
            this.services,
            this.loggerFactory,
            ct,
            clientIdOverride: null,
            trustContext: trustContext,
            processExecutor: this.services?.ProcessExecutor as IProcessExecutor).ConfigureAwait(false);

        // Process-backed transports launch lazily from ConnectAsync. Connect them inside this
        // sanitized request boundary so wrapper/executor failures are returned safely instead of
        // faulting an unobserved relay after the remote open has already succeeded.
        if (serverTransport is ProcessExecutorBackedClientTransport)
        {
            var connected = await serverTransport.ConnectAsync(ct).ConfigureAwait(false);
            serverTransport = new PreconnectedClientTransport(serverTransport.Name, connected);
        }

        var delegatingServer = new DelegatingMcpServer(serverTransport);
        var incoming = McpChannelClientTransport.CreateServerTransport(channel);
        var cts = new CancellationTokenSource();
        var relay = delegatingServer.RunAsync(incoming, cts.Token);
        return new HostSession(delegatingServer, incoming, cts, relay);
    }

    /// <summary>
    /// Resolves the stored trust profile referenced by a #1477 <c>mcp</c> request on THIS launch
    /// host and compiles it into an <see cref="AgentExecutionTrustContext"/>. When the request has
    /// no reference, returns null so the launch is unconstrained. A revision mismatch fails closed
    /// with <see cref="InvalidOperationException"/> before any process starts.
    /// </summary>
    private Task<AgentExecutionTrustContext?> ResolveTrustContextAsync(
        JsonElement request,
        CancellationToken ct)
    {
        if (!McpConnectionRequest.TryGetTrustProfileReference(
                request,
                out var trustProfileRef,
                out var expectedRevision))
        {
            return Task.FromResult<AgentExecutionTrustContext?>(null);
        }
        if (string.IsNullOrWhiteSpace(expectedRevision))
        {
            throw new InvalidOperationException(
                $"Remote MCP trust profile '{trustProfileRef}' must include an expected revision.");
        }

        var resolver = this.services?.TrustProfileResolver as IRemoteTrustProfileResolver;
        var compiler = this.services?.TrustProfilePolicyCompiler as ITrustProfileProcessPolicyCompiler;
        if (resolver is null || compiler is null)
        {
            throw new InvalidOperationException(
                $"Remote MCP host has no trust profile resolver/compiler for reference '{trustProfileRef}'.");
        }

        return Task.FromResult<AgentExecutionTrustContext?>(
            new AgentExecutionTrustContext(
                new AgentExecutionTrustProfileReference(
                    "trust-profile",
                    trustProfileRef,
                    expectedRevision),
                resolver,
                compiler));
    }

    /// <summary>
    /// Resolves the MCP server this host must open for <paramref name="request"/>. A stored-tool
    /// <c>tool-type-name</c> / <c>tool-entity-id</c> reference (issue #1439) is resolved on THIS host via
    /// the executor-scoped <see cref="AgentServices.ToolResourceFactory"/> (machine-prefix-first), so the
    /// bound executor's profile/user context — not the caller's — determines the config; any other
    /// <c>mcp</c> request is parsed as an inline connection descriptor. Returns <see langword="null"/>
    /// when the request is not a hostable MCP connection (so the caller declines it).
    /// </summary>
    public async Task<McpTool?> ResolveConnectionAsync(JsonElement request, CancellationToken ct = default)
    {
        if (McpConnectionRequest.TryGetToolReference(request, out var toolTypeName, out var toolEntityId))
        {
            if (this.services?.ToolResourceFactory is not { } toolResourceFactory)
            {
                return null;
            }

            var resolved = await toolResourceFactory.ResolveToolResourceAsync(
                new ToolResource
                {
                    Kind = "tool",
                    Id = toolTypeName,
                    Name = toolEntityId,
                },
                ct).ConfigureAwait(false);

            return resolved as McpTool;
        }

        return McpConnectionRequest.ToTool(request);
    }

    private sealed class PreconnectedClientTransport(
        string name,
        ModelContextProtocol.Protocol.ITransport transport)
        : ModelContextProtocol.Client.IClientTransport
    {
        private int connected;

        public string Name { get; } = name;

        public Task<ModelContextProtocol.Protocol.ITransport> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref this.connected, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The preconnected MCP transport has already been consumed.");
            }

            return Task.FromResult(transport);
        }
    }

    private sealed class HostSession(
        DelegatingMcpServer delegatingServer,
        ModelContextProtocol.Protocol.ITransport incoming,
        CancellationTokenSource cts,
        Task relay) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            await cts.CancelAsync().ConfigureAwait(false);
            await incoming.DisposeAsync().ConfigureAwait(false);

            try
            {
                await relay.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort teardown: a relay that faulted because the hosted server connection
                // dropped or was cancelled must not surface from disposal.
            }

            await delegatingServer.DisposeAsync().ConfigureAwait(false);
            cts.Dispose();
        }
    }
}
