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
    private McpClient? client;

    public McpToolContextProvider(
        McpTool tool,
        ILoggerFactory? loggerFactory,
        ExecutorTarget executorTarget = ExecutorTarget.AgentExecutor,
        AgentServices? services = null)
        : base(null, null, null)
    {
        this.tool = tool;
        this.loggerFactory = loggerFactory;
        this.services = services;
        this.ExecutorTarget = executorTarget;
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
            if (this.client is null)
            {
                var logger = this.loggerFactory?.CreateLogger<McpToolContextProvider>();
                var serverName = string.IsNullOrWhiteSpace(this.tool.ServerName) ? this.tool.Name : this.tool.ServerName;
                try
                {
                    var transport = await McpTransportFactory.CreateMcpTransportAsync(
                        this.tool,
                        this.services,
                        this.loggerFactory,
                        cancellationToken);
                    this.client = await McpClient.CreateAsync(transport, null, this.loggerFactory, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Log only the exception type/message (no secrets, tokens, or URIs) and re-throw so
                    // AgentChat's catch surfaces the structured diagnostic unchanged (issue #1408).
                    logger?.LogError(ex, "Failed to open MCP server {ServerName}.", serverName ?? "(mcp server)");
                    throw;
                }
            }

            var mcpTools = await McpClientToolListing.ListToolsAsync(this.client, cancellationToken);
            if (this.tool.AllowedTools is { Count: > 0 })
            {
                var allowedSet = new HashSet<string>(this.tool.AllowedTools, StringComparer.OrdinalIgnoreCase);
                mcpTools = [.. mcpTools.Where(tool => allowedSet.Contains(tool.Name))];
            }

            return new AIContext
            {
                Tools = mcpTools.Cast<AITool>().ToArray(),
            };
        }
        finally
        {
            this.initializeLock.Release();
        }
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
