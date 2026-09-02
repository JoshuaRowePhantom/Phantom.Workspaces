using AgentSchema;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// Single source of truth for turning an MCP <see cref="Connection"/> into an
/// <see cref="IClientTransport"/>. Both <see cref="McpToolContextProvider"/> and <c>AgentChat</c>
/// route through here so the Anonymous/API-key/OAuth switch — and the stdio <c>env</c> handling
/// added by #1379 — lives in exactly one place.
/// </summary>
/// <remarks>
/// The OAuth arm activates the MCP SDK's built-in OAuth client by populating
/// <see cref="HttpClientTransportOptions.OAuth"/> (RFC 9728/8414 discovery, RFC 7591 dynamic
/// registration, authorization-code + PKCE, silent refresh, 401 retry). The host-supplied pieces
/// (interactive redirect delegate, token cache) are injected through <see cref="McpOAuthOptions"/>.
/// </remarks>
internal static class McpTransportFactory
{
    /// <summary>
    /// Neutral loopback URI used only to satisfy the SDK's required, non-nullable
    /// <c>ClientOAuthOptions.RedirectUri</c> when the host injects none. No listener is bound to it;
    /// the real interactive handler (sub-item #1385) injects a concrete loopback URI and binds the
    /// listener, and the default redirect delegate throws before this value is used in headless
    /// contexts.
    /// </summary>
    private static readonly Uri DefaultLoopbackRedirectUri = new("http://localhost/");

    /// <summary>
    /// Builds the transport for <paramref name="tool"/>'s connection. The Anonymous arm is
    /// synchronous; the API-key and OAuth arms are async because credential resolution (the
    /// <c>apiKey</c> and OAuth client-id/secret placeholders) may consult an async secret provider —
    /// both a <c>${SECRET:&lt;handle&gt;}</c> resolver and the <c>${ENV}</c>/<c>${GITHUB_TOKEN}</c>
    /// fallback.
    /// </summary>
    public static async Task<IClientTransport> CreateMcpTransportAsync(
        McpTool tool,
        AgentServices? services,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);

        switch (tool.Connection)
        {
            case AnonymousConnection anonymous:
                return CreateTransportFromEndpoint(
                    anonymous.Endpoint,
                    apiKey: null,
                    tool.ServerName,
                    loggerFactory);

            case ApiKeyConnection apiKey:
                var resolvedKey = await AgentFactory.ResolveRequiredSecretOrEnvAsync(
                    apiKey.ApiKey,
                    services,
                    tool.ServerName,
                    cancellationToken).ConfigureAwait(false);
                return CreateTransportFromEndpoint(
                    apiKey.Endpoint,
                    resolvedKey,
                    tool.ServerName,
                    loggerFactory);

            case OAuthConnection oauth:
                return await CreateOAuthTransportAsync(
                    oauth,
                    tool.ServerName,
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

        if (IsStdioEndpoint(endpointUri))
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("MCP stdio transport does not support API key headers.");
            }

            return CreateStdioTransport(endpointUri, serverName);
        }

        return CreateHttpTransport(endpointUri, apiKey, serverName, loggerFactory);
    }

    private static async Task<IClientTransport> CreateOAuthTransportAsync(
        OAuthConnection oauth,
        string? serverName,
        AgentServices? services,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(oauth.Endpoint))
        {
            throw new InvalidOperationException("MCP tool endpoint is required.");
        }

        if (!Uri.TryCreate(oauth.Endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException($"MCP tool endpoint is not a valid absolute URI: {oauth.Endpoint}");
        }

        var displayName = string.IsNullOrWhiteSpace(serverName) ? "unknown" : serverName;

        if (IsStdioEndpoint(endpointUri))
        {
            throw new InvalidOperationException($"OAuth is not supported for stdio MCP server '{displayName}'.");
        }

        var oauthOptions = ResolveOAuthOptions(services);

        var clientId = await AgentFactory.ResolveOptionalSecretOrEnvAsync(
            oauth.ClientId, services, serverName, cancellationToken).ConfigureAwait(false);
        var clientSecret = await AgentFactory.ResolveOptionalSecretOrEnvAsync(
            oauth.ClientSecret, services, serverName, cancellationToken).ConfigureAwait(false);

        var clientOAuthOptions = new ClientOAuthOptions
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            // RedirectUri is a required, non-nullable member of the SDK's ClientOAuthOptions. When the
            // host has not injected one, fall back to a neutral loopback URI purely to satisfy the SDK
            // contract. This is not a listener — no HttpListener is bound here; the real interactive
            // handler (sub-item #1385) injects a concrete loopback URI and binds the listener. In
            // headless/unit contexts the default AuthorizationRedirectDelegate throws before this URI
            // is ever used.
            RedirectUri = oauthOptions.RedirectUri ?? DefaultLoopbackRedirectUri,
            AuthorizationRedirectDelegate = oauthOptions.ResolveRedirectDelegate(displayName),
        };

        if (oauthOptions.ResolveTokenCache(displayName) is { } tokenCache)
        {
            clientOAuthOptions.TokenCache = tokenCache;
        }

        if (oauth.Scopes is { } scopes)
        {
            clientOAuthOptions.Scopes = scopes;
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = endpointUri,
            OAuth = clientOAuthOptions,
        };

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            transportOptions.Name = serverName;
        }

        return new HttpClientTransport(transportOptions, loggerFactory);
    }

    internal static McpOAuthOptions ResolveOAuthOptions(AgentServices? services)
        => services?.McpOAuthOptions as McpOAuthOptions ?? McpOAuthOptions.Default;

    private static bool IsStdioEndpoint(Uri endpointUri)
        => string.Equals(endpointUri.Scheme, "stdio", StringComparison.OrdinalIgnoreCase);

    private static IClientTransport CreateStdioTransport(Uri endpointUri, string? serverName)
        => new StdioClientTransport(BuildStdioTransportOptions(endpointUri, serverName));

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
