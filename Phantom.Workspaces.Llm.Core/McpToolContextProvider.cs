using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Core.Transport;

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
    private readonly SemaphoreSlim initializeLock = new(1, 1);

    // Test seam: the operation that establishes the MCP connection and lists its tools. Production
    // uses <see cref="ConnectAndListToolsAsync"/> (which connects a live client); unit tests inject a
    // counting stub so the terminal-failure latch and single-initialization caching can be exercised
    // without a live server.
    private readonly Func<CancellationToken, Task<AITool[]>> initializeToolsAsync;

    private McpClient? client;
    private AITool[]? cachedTools;

    // Terminal latch: once an initialization attempt fails, further invocations short-circuit and no
    // reconnection (or OAuth browser relaunch) is attempted until an explicit user re-enable calls
    // <see cref="ResetInitialization"/> (issue #1447).
    private bool initializationFailed;

    public McpToolContextProvider(
        McpTool tool,
        ILoggerFactory? loggerFactory,
        ExecutorTarget executorTarget = ExecutorTarget.AgentExecutor,
        AgentServices? services = null)
        : this(tool, loggerFactory, executorTarget, services, initializeOverride: null)
    {
    }

    internal McpToolContextProvider(
        McpTool tool,
        ILoggerFactory? loggerFactory,
        ExecutorTarget executorTarget,
        AgentServices? services,
        Func<CancellationToken, Task<AITool[]>>? initializeOverride)
        : base(null, null, null)
    {
        this.tool = tool;
        this.loggerFactory = loggerFactory;
        this.services = services;
        this.ExecutorTarget = executorTarget;
        this.initializeToolsAsync = initializeOverride ?? this.ConnectAndListToolsAsync;
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
        await this.initializeLock.WaitAsync(cancellationToken);
        try
        {
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
                    this.cachedTools = await this.initializeToolsAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    // Latch the failure so the next invocation short-circuits instead of retrying
                    // forever. Log only the exception type/message (no secrets, tokens, or URIs) and
                    // re-throw so AgentChat's catch surfaces the structured diagnostic unchanged and
                    // marks the server node disabled (issues #1408, #1447).
                    this.initializationFailed = true;
                    logger?.LogError(ex, "Failed to open MCP server {ServerName}.", serverName ?? "(mcp server)");
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

    private async Task<AITool[]> ConnectAndListToolsAsync(CancellationToken cancellationToken)
    {
        var logger = this.loggerFactory?.CreateLogger<McpToolContextProvider>();
        var serverName = string.IsNullOrWhiteSpace(this.tool.ServerName) ? this.tool.Name : this.tool.ServerName;

        this.client = await McpTransportFactory.ConnectWithDynamicRegistrationFallbackAsync(
            this.tool,
            async (clientIdOverride, ct) =>
            {
                var transport = await McpTransportFactory.CreateMcpTransportAsync(
                    this.tool,
                    this.services,
                    this.loggerFactory,
                    ct,
                    clientIdOverride);
                return await McpClient.CreateAsync(transport, null, this.loggerFactory, ct);
            },
            logger,
            serverName,
            cancellationToken);

        var mcpTools = await McpClientToolListing.ListToolsAsync(this.client, cancellationToken);
        if (this.tool.AllowedTools is { Count: > 0 })
        {
            var allowedSet = new HashSet<string>(this.tool.AllowedTools, StringComparer.OrdinalIgnoreCase);
            mcpTools = [.. mcpTools.Where(tool => allowedSet.Contains(tool.Name))];
        }

        return mcpTools.Cast<AITool>().ToArray();
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
        this.client = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.client is not null)
        {
            await this.client.DisposeAsync();
            this.client = null;
        }

        this.initializeLock.Dispose();
    }

    /// <summary>
    /// Preserved API for the #1379 stdio-<c>env</c> tests. Forwards to the shared
    /// <see cref="Mcp.McpTransportFactory"/>, which is the single source of truth for stdio option
    /// construction (command, args, cwd, and env NAME=value handling).
    /// </summary>
    internal static StdioClientTransportOptions BuildStdioTransportOptions(Uri endpointUri, string? serverName)
        => McpTransportFactory.BuildStdioTransportOptions(endpointUri, serverName);
}
