using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using AgentSchema;
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
    private readonly SemaphoreSlim initializeLock = new(1, 1);
    private McpClient? client;

    public McpToolContextProvider(
        McpTool tool,
        ILoggerFactory? loggerFactory,
        ExecutorTarget executorTarget = ExecutorTarget.AgentExecutor)
        : base(null, null, null)
    {
        this.tool = tool;
        this.loggerFactory = loggerFactory;
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
                var transport = CreateMcpTransport(this.tool, this.loggerFactory);
                this.client = await McpClient.CreateAsync(transport, null, this.loggerFactory, cancellationToken);
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

    private static IClientTransport CreateMcpTransport(
        McpTool tool,
        ILoggerFactory? loggerFactory)
    {
        return tool.Connection switch
        {
            AnonymousConnection anonymous => CreateTransportFromEndpoint(
                anonymous.Endpoint,
                apiKey: null,
                tool.ServerName,
                loggerFactory),
            ApiKeyConnection apiKey => CreateTransportFromEndpoint(
                apiKey.Endpoint,
                AgentFactory.ResolveApiKey(apiKey.ApiKey, tool.ServerName),
                tool.ServerName,
                loggerFactory),
            null => throw new InvalidOperationException($"MCP tool '{tool.Name}' must define a connection."),
            _ => throw new InvalidOperationException(
                $"MCP tool '{tool.Name}' has unsupported connection type '{tool.Connection.GetType().Name}'."),
        };
    }

    private static IClientTransport CreateTransportFromEndpoint(
        string? endpoint,
        string? apiKey,
        string? serverName,
        ILoggerFactory? loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("MCP tool endpoint is required.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException($"MCP tool endpoint is not a valid absolute URI: {endpoint}");
        }

        if (string.Equals(endpointUri.Scheme, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("MCP stdio transport does not support API key headers.");
            }

            return CreateStdioTransport(endpointUri, serverName);
        }

        return CreateHttpTransport(endpointUri, apiKey, serverName, loggerFactory);
    }

    private static IClientTransport CreateStdioTransport(Uri endpointUri, string? serverName)
    {
        return new StdioClientTransport(BuildStdioTransportOptions(endpointUri, serverName));
    }

    internal static StdioClientTransportOptions BuildStdioTransportOptions(Uri endpointUri, string? serverName)
    {
        var query = ParseUriQuery(endpointUri.Query);
        var command = GetFirstNonEmptyValue(query, "command")
            ?? (!string.IsNullOrWhiteSpace(endpointUri.Host) ? endpointUri.Host : null);
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException(
                "MCP stdio endpoint requires a command. Use stdio://?command=<process>.");
        }

        var options = new StdioClientTransportOptions
        {
            Command = command,
        };

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            options.Name = serverName;
        }

        var argValues = GetAllValues(query, "arg");
        if (argValues.Count > 0)
        {
            options.Arguments = [.. argValues];
        }

        var workingDirectory = GetFirstNonEmptyValue(query, "cwd");
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            options.WorkingDirectory = workingDirectory;
        }

        var envValues = GetAllValues(query, "env");
        if (envValues.Count > 0)
        {
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var entry in envValues)
            {
                var separatorIndex = entry.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    throw new InvalidOperationException(
                        $"MCP stdio env entry must be in NAME=value form: '{entry}'.");
                }

                var name = entry[..separatorIndex];
                var value = entry[(separatorIndex + 1)..];
                environment[name] = value;
            }

            options.EnvironmentVariables = environment;
        }

        return options;
    }

    private static IClientTransport CreateHttpTransport(
        Uri endpointUri,
        string? apiKey,
        string? serverName,
        ILoggerFactory? loggerFactory)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = endpointUri,
        };

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            transportOptions.Name = serverName;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            transportOptions.AdditionalHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {apiKey}",
            };
        }

        return new HttpClientTransport(transportOptions, loggerFactory);
    }

    private static Dictionary<string, List<string>> ParseUriQuery(string query)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return values;
        }

        var segments = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            var separatorIndex = segment.IndexOf('=');
            var encodedKey = separatorIndex >= 0 ? segment[..separatorIndex] : segment;
            var encodedValue = separatorIndex >= 0 ? segment[(separatorIndex + 1)..] : string.Empty;

            var key = Uri.UnescapeDataString(encodedKey);
            var value = Uri.UnescapeDataString(encodedValue);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!values.TryGetValue(key, out var list))
            {
                list = [];
                values[key] = list;
            }

            list.Add(value);
        }

        return values;
    }

    private static string? GetFirstNonEmptyValue(
        IReadOnlyDictionary<string, List<string>> values,
        string key)
    {
        if (!values.TryGetValue(key, out var candidates))
        {
            return null;
        }

        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static IReadOnlyList<string> GetAllValues(
        IReadOnlyDictionary<string, List<string>> values,
        string key)
    {
        if (!values.TryGetValue(key, out var candidates))
        {
            return [];
        }

        return candidates.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
    }
}
