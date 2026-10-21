using AgentSchema;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// Single source of truth for turning an MCP <see cref="Connection"/> into an
/// <see cref="IClientTransport"/>. Both <see cref="McpToolContextProvider"/> and <c>AgentChat</c>
/// route through here so the Anonymous/API-key/OAuth switch — and the stdio <c>env</c> handling
/// added by #1379 — lives in exactly one place.
/// </summary>
/// <remarks>
/// OAuth connections acquire one independently authorized fixed-generation use through the access
/// broker. Their transports never receive SDK redirect, refresh or token-cache options.
/// </remarks>
internal static class McpTransportFactory
{
    /// <summary>
    /// Builds the transport for <paramref name="tool"/>'s connection. The Anonymous arm is
    /// synchronous; credential-bearing connections await their credential owner before connecting.
    /// </summary>
    public static async Task<IClientTransport> CreateMcpTransportAsync(
        McpTool tool,
        AgentServices? services,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken,
        AgentExecutionTrustContext? trustContext = null,
        IProcessExecutor? processExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        processExecutor ??= services?.ProcessExecutor as IProcessExecutor;
        if (trustContext is not null)
        {
            await trustContext.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        }

        // #1416: resolve the Phantom transport mode from the (possibly Phantom-subclassed) tool. A
        // plain McpTool has no 'type' field, so it defaults to Streamable HTTP rather than the SDK's
        // AutoDetect (whose SSE GET probe is rejected 405 by Streamable-HTTP-only servers).
        var transportMode = tool is PhantomMcpTool phantomTool ? phantomTool.Transport : McpHttpTransport.Streamable;

        switch (tool.Connection)
        {
            case AnonymousConnection anonymous:
                return CreateTransportFromEndpoint(
                    anonymous.Endpoint,
                    tool.ServerName,
                    transportMode,
                    loggerFactory,
                    trustContext,
                    processExecutor);

            case ApiKeyConnection apiKey:
                EnsureHttpAllowed(trustContext, tool.ServerName);
                return await McpBrokerClientTransport.CreateStoredSecretAsync(
                    tool,
                    apiKey,
                    ToHttpTransportMode(transportMode),
                    services,
                    loggerFactory,
                    cancellationToken).ConfigureAwait(false);

            case OAuthConnection oauth:
                EnsureHttpAllowed(trustContext, tool.ServerName);
                return await McpBrokerClientTransport.CreateAsync(
                    tool,
                    oauth,
                    ToHttpTransportMode(transportMode),
                    services,
                    loggerFactory,
                    cancellationToken).ConfigureAwait(false);

            case null:
                throw new InvalidOperationException($"MCP tool '{tool.Name}' must define a connection.");

            default:
                throw new InvalidOperationException(
                    $"MCP tool '{tool.Name}' has unsupported connection type '{tool.Connection.GetType().Name}'.");
        }
    }

    internal static IClientTransport CreateTransportFromEndpoint(
        string? endpoint,
        string? serverName,
        McpHttpTransport transportMode,
        ILoggerFactory? loggerFactory,
        AgentExecutionTrustContext? trustContext = null,
        IProcessExecutor? processExecutor = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("MCP tool endpoint is required.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException($"MCP tool endpoint is not a valid absolute URI: {endpoint}");
        }

        if (IsStdioEndpoint(endpointUri))
        {
            return CreateStdioTransport(endpointUri, serverName, trustContext, processExecutor, loggerFactory);
        }

        EnsureHttpAllowed(trustContext, serverName);
        return CreateHttpTransport(endpointUri, serverName, transportMode, loggerFactory);
    }

    /// <summary>
    /// Rejects HTTP/SSE MCP endpoints when the session's trust context requires process
    /// containment (issue #1477). Phantom does not own the remote server process, so no child
    /// process can be sandboxed; the caller must fail closed with an unsupported-policy error.
    /// </summary>
    internal static void EnsureHttpAllowed(
        AgentExecutionTrustContext? trustContext,
        string? serverName)
    {
        if (trustContext is null || !trustContext.IsLaunchHost)
            return;

        var compilation = trustContext.Compile();
        if (!compilation.RequiresContainment)
            return;

        var display = string.IsNullOrWhiteSpace(serverName) ? "(mcp server)" : serverName;
        throw new McpUnsupportedPolicyException(
            $"MCP HTTP/SSE server '{display}' cannot be constrained: no child process exists to sandbox.");
    }

    private static bool IsStdioEndpoint(Uri endpointUri)
        => string.Equals(endpointUri.Scheme, "stdio", StringComparison.OrdinalIgnoreCase);

    // #1416: map the Phantom transport enum onto the MCP SDK's HttpTransportMode. Streamable is the
    // default so POST-only (Streamable-HTTP-only) servers are not probed with an SSE GET (405).
    private static HttpTransportMode ToHttpTransportMode(McpHttpTransport transportMode)
        => transportMode switch
        {
            McpHttpTransport.Sse => HttpTransportMode.Sse,
            McpHttpTransport.Auto => HttpTransportMode.AutoDetect,
            _ => HttpTransportMode.StreamableHttp,
        };

    private static IClientTransport CreateStdioTransport(
        Uri endpointUri,
        string? serverName,
        AgentExecutionTrustContext? trustContext = null,
        IProcessExecutor? processExecutor = null,
        ILoggerFactory? loggerFactory = null)
    {
        var options = BuildStdioTransportOptions(endpointUri, serverName);
        var executionRequest = BuildProcessExecutionRequest(options, trustContext);
        return new ProcessExecutorBackedClientTransport(
            string.IsNullOrWhiteSpace(options.Name) ? "mcp-stdio" : options.Name,
            executionRequest,
            processExecutor ?? new ProcessExecutor(),
            loggerFactory);
    }

    /// <summary>
    /// Translates the parsed stdio options into a #1474 <see cref="ProcessExecutionRequest"/> and
    /// attaches the compiled MXC policy from <paramref name="trustContext"/> when — and only when —
    /// containment is required (issue #1477). A null policy selects the executor's
    /// ordinary-process branch; a compiled policy selects MXC. Command resolution honours PATH /
    /// PATHEXT so that <c>.cmd</c>/<c>.bat</c> shims run through <c>%ComSpec%</c>.
    /// </summary>
    internal static ProcessExecutionRequest BuildProcessExecutionRequest(
        StdioClientTransportOptions options,
        AgentExecutionTrustContext? trustContext)
    {
        ArgumentNullException.ThrowIfNull(options);

        var originalArgs = options.Arguments is null
            ? Array.Empty<string>()
            : [.. options.Arguments];
        var resolved = StdioCommandResolver.Resolve(options.Command, originalArgs);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.EnvironmentVariables is not null)
        {
            foreach (var (name, value) in options.EnvironmentVariables)
            {
                if (value is not null)
                    environment[name] = value;
            }
        }

        var request = new ProcessExecutionRequest(
            resolved.Executable,
            resolved.Arguments)
        {
            WorkingDirectory = options.WorkingDirectory,
            Environment = environment,
        };

        if (trustContext?.IsLaunchHost == true)
        {
            var compilation = trustContext.Compile();
            if (compilation.RequiresContainment)
            {
                if (compilation.Policy is null)
                {
                    throw new InvalidOperationException(
                        "Trust profile requires containment but no MXC policy was produced.");
                }

                request = request with { MxcPolicy = compilation.Policy };
            }
        }

        return request;
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
        string? serverName,
        McpHttpTransport transportMode,
        ILoggerFactory? loggerFactory)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = endpointUri,
            TransportMode = ToHttpTransportMode(transportMode),
        };

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            transportOptions.Name = serverName;
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
