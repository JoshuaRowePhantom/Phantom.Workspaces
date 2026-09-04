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
    /// The public Visual Studio Code OAuth client id, accepted by GitHub- and Microsoft-hosted MCP
    /// servers that do not support OAuth 2.0 Dynamic Client Registration (RFC 7591). It is used as
    /// the static fallback client id when a DCR attempt is rejected and no client id was explicitly
    /// configured (issue #1421). This is a public client id, not a secret.
    /// </summary>
    public const string DefaultDynamicRegistrationFallbackClientId = "aebc6443-996d-45c2-90f0-388ff96faa56";

    /// <summary>
    /// Marker substring present in every MCP SDK Dynamic Client Registration rejection message
    /// (RFC 7591). The SDK's <c>ClientOAuthProvider</c> throws with one of
    /// "Authorization server does not support dynamic client registration",
    /// "Dynamic client registration failed with status …", or
    /// "Dynamic client registration returned empty response". Matching this phrase — and only this
    /// phrase — keeps the fallback narrow so genuine failures (user-cancelled auth, network errors,
    /// <c>invalid_grant</c>, token-endpoint failures) still surface their own diagnostic.
    /// </summary>
    private const string DynamicClientRegistrationFailureMarker = "dynamic client registration";

    /// <summary>
    /// Builds the transport for <paramref name="tool"/>'s connection. The Anonymous arm is
    /// synchronous; the API-key and OAuth arms are async because credential resolution (the
    /// <c>apiKey</c> and OAuth client-id/secret placeholders) may consult an async secret provider —
    /// both a <c>${SECRET:&lt;handle&gt;}</c> resolver and the <c>${ENV}</c>/<c>${GITHUB_TOKEN}</c>
    /// fallback.
    /// </summary>
    /// <param name="clientIdOverride">
    /// When non-null/non-empty and the connection is OAuth, this client id wins over the resolved
    /// <c>oauth.ClientId</c> and no client secret is set (public client on PKCE). Used only by the
    /// #1421 DCR-rejection static fallback build; null on the normal (DCR-first) path.
    /// </param>
    public static async Task<IClientTransport> CreateMcpTransportAsync(
        McpTool tool,
        AgentServices? services,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken,
        string? clientIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(tool);

        // #1416: resolve the Phantom transport mode from the (possibly Phantom-subclassed) tool. A
        // plain McpTool has no 'type' field, so it defaults to Streamable HTTP rather than the SDK's
        // AutoDetect (whose SSE GET probe is rejected 405 by Streamable-HTTP-only servers).
        var transportMode = tool is PhantomMcpTool phantomTool ? phantomTool.Transport : McpHttpTransport.Streamable;

        switch (tool.Connection)
        {
            case AnonymousConnection anonymous:
                return CreateTransportFromEndpoint(
                    anonymous.Endpoint,
                    apiKey: null,
                    tool.ServerName,
                    transportMode,
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
                    transportMode,
                    loggerFactory);

            case OAuthConnection oauth
                when string.Equals(oauth.AuthenticationMode, PhantomAgentSchema.EntraPinnedAuthenticationMode, StringComparison.OrdinalIgnoreCase):
                return await CreateEntraPinnedTransportAsync(
                    oauth,
                    tool.ServerName,
                    transportMode,
                    services,
                    loggerFactory,
                    cancellationToken).ConfigureAwait(false);

            case OAuthConnection oauth:
                return await CreateOAuthTransportAsync(
                    oauth,
                    tool.ServerName,
                    transportMode,
                    services,
                    loggerFactory,
                    clientIdOverride,
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
        McpHttpTransport transportMode,
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

        return CreateHttpTransport(endpointUri, apiKey, serverName, transportMode, loggerFactory);
    }

    private static async Task<IClientTransport> CreateOAuthTransportAsync(
        OAuthConnection oauth,
        string? serverName,
        McpHttpTransport transportMode,
        AgentServices? services,
        ILoggerFactory? loggerFactory,
        string? clientIdOverride,
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

        // Log the OAuth wiring endpoint only. Never log client id/secret, tokens, scopes values, or
        // the redirect URI (issue #1408): only the transport endpoint host/path is safe.
        var logger = loggerFactory?.CreateLogger("Phantom.Workspaces.Llm.Mcp.McpTransportFactory");
        logger?.LogInformation(
            "Wiring interactive OAuth transport for MCP server '{ServerName}' at endpoint {Endpoint}.",
            displayName,
            endpointUri.GetLeftPart(UriPartial.Path));

        string? clientId;
        string? clientSecret;
        if (!string.IsNullOrWhiteSpace(clientIdOverride))
        {
            // #1421 static fallback build: the supplied public client id wins over any configured
            // value, and no client secret is sent (public client relying on PKCE).
            clientId = clientIdOverride;
            clientSecret = null;
        }
        else
        {
            // #1430: surface a "gathering credentials" status so a running item can show progress
            // while secret/env resolution (which may consult an async secret provider and prompt for
            // consent) runs. The report is a no-op when no reporter is wired (headless/unit hosts).
            services?.McpCredentialStatusReporter?.Invoke(
                displayName,
                $"Gathering credentials for {displayName}\u2026");
            clientId = await AgentFactory.ResolveOptionalSecretOrEnvAsync(
                oauth.ClientId, services, serverName, cancellationToken).ConfigureAwait(false);
            clientSecret = await AgentFactory.ResolveOptionalSecretOrEnvAsync(
                oauth.ClientSecret, services, serverName, cancellationToken).ConfigureAwait(false);
        }

        // #1430: wrap the interactive redirect delegate so the running item flips to "waiting for
        // sign-in" the moment the SDK opens the browser for the authorization-code flow. Only wrap
        // when a status reporter is actually wired: hosts (and the #1402 tests) that inject a redirect
        // delegate without a reporter must observe that exact delegate instance unchanged.
        var innerRedirectDelegate = oauthOptions.ResolveRedirectDelegate(displayName);
        AuthorizationRedirectDelegate redirectDelegate = innerRedirectDelegate;
        if (services?.McpCredentialStatusReporter is { } signInReporter)
        {
            redirectDelegate = (authorizationUri, redirectUri, redirectCancellationToken) =>
            {
                signInReporter(displayName, "Waiting for sign-in\u2026");
                return innerRedirectDelegate(authorizationUri, redirectUri, redirectCancellationToken);
            };
        }

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
            AuthorizationRedirectDelegate = redirectDelegate,
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
            TransportMode = ToHttpTransportMode(transportMode),
        };

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            transportOptions.Name = serverName;
        }

        return new HttpClientTransport(transportOptions, loggerFactory);
    }

    /// <summary>
    /// Builds the transport for the host-pinned Entra (<c>entra-pinned</c>) mode (issue #1420,
    /// integration point C). Unlike the <c>system</c> path this deliberately leaves
    /// <see cref="HttpClientTransportOptions.OAuth"/> <b>unset</b>, so the SDK's
    /// <c>ClientOAuthProvider</c> (and its RFC 8707 resource indicator) never runs. Instead it acquires
    /// tokens via a first-party <see cref="EntraPinnedTokenProvider"/> and attaches them through an
    /// origin-pinning <see cref="EntraBearerTokenHandler"/> installed over a redirect-disabled inner
    /// handler. The pinned origin is derived from the endpoint (which must be HTTPS); authority and
    /// scopes come from static config, never from remote metadata.
    /// </summary>
    private static async Task<IClientTransport> CreateEntraPinnedTransportAsync(
        OAuthConnection oauth,
        string? serverName,
        McpHttpTransport transportMode,
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

        // The bearer is pinned to a secure origin only; a non-HTTPS endpoint is rejected so a token can
        // never be attached to a cleartext origin.
        if (!string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Host-pinned Entra authentication requires an https endpoint for MCP server '{displayName}'.");
        }

        var authority = (oauth as PhantomOAuthConnection)?.Authority;
        if (string.IsNullOrWhiteSpace(authority))
        {
            throw new InvalidOperationException(
                $"Host-pinned Entra authentication requires an 'authority' for MCP server '{displayName}'.");
        }

        if (oauth.Scopes is not { Count: > 0 } scopes)
        {
            throw new InvalidOperationException(
                $"Host-pinned Entra authentication requires at least one scope for MCP server '{displayName}'.");
        }

        var logger = loggerFactory?.CreateLogger("Phantom.Workspaces.Llm.Mcp.McpTransportFactory");
        logger?.LogInformation(
            "Wiring host-pinned Entra OAuth transport for MCP server '{ServerName}' at endpoint {Endpoint}.",
            displayName,
            endpointUri.GetLeftPart(UriPartial.Path));

        var clientId = await AgentFactory.ResolveOptionalSecretOrEnvAsync(
            oauth.ClientId, services, serverName, cancellationToken).ConfigureAwait(false);

        var oauthOptions = ResolveOAuthOptions(services);

        // #1427: do NOT reuse the #1425 shared DCR loopback redirect URI (oauthOptions.RedirectUri).
        // That listener is bound and held for the process lifetime, so handing its port to MSAL's
        // InteractiveBrowserCredential — which starts its own HttpListener on the redirect URI — would
        // collide ("conflicts with an existing registration on the machine"). Passing RedirectUri: null
        // lets MSAL bind its own ephemeral localhost loopback port, which Entra matches port-agnostically.
        var credential = oauthOptions.ResolveEntraCredential(
            new McpEntraPinnedTokenRequest(authority, clientId, RedirectUri: null, displayName));

        var tokenProvider = new EntraPinnedTokenProvider(credential, scopes);
        var allowedOrigin = new Uri(endpointUri.GetLeftPart(UriPartial.Authority));

        // AllowAutoRedirect is disabled so a cross-origin redirect never carries the bearer; the handler
        // re-checks the origin on every hop regardless.
        var bearerHandler = new EntraBearerTokenHandler(tokenProvider, allowedOrigin)
        {
            InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false },
        };
        var httpClient = new HttpClient(bearerHandler);

        // OAuth is intentionally left unset: the SDK OAuth provider (and its resource indicator) is
        // bypassed entirely in this mode.
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = endpointUri,
            TransportMode = ToHttpTransportMode(transportMode),
        };

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            transportOptions.Name = serverName;
        }

        return new HttpClientTransport(transportOptions, httpClient, loggerFactory, ownsHttpClient: true);
    }

    internal static McpOAuthOptions ResolveOAuthOptions(AgentServices? services)
        => services?.McpOAuthOptions as McpOAuthOptions ?? McpOAuthOptions.Default;

    /// <summary>
    /// Connects an MCP client with the #1421 "DCR-first, static-fallback-second" strategy. The
    /// <paramref name="connectAsync"/> delegate builds the transport (honoring an optional client-id
    /// override) and connects. It is invoked first with a null override (Dynamic Client Registration
    /// is attempted). If that connect fails specifically because DCR was rejected and no explicit
    /// client id was configured — as classified by <see cref="ShouldFallBackToStaticClientId"/> — it
    /// is invoked exactly once more with <see cref="DefaultDynamicRegistrationFallbackClientId"/>.
    /// Any other failure, and any failure of the single retry, propagates unchanged (no infinite
    /// retry) so the caller's diagnostic (issue #1408) is preserved.
    /// </summary>
    internal static async Task<TClient> ConnectWithDynamicRegistrationFallbackAsync<TClient>(
        McpTool tool,
        Func<string?, CancellationToken, Task<TClient>> connectAsync,
        ILogger? logger,
        string? serverName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(connectAsync);

        try
        {
            return await connectAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldFallBackToStaticClientId(tool, ex))
        {
            logger?.LogWarning(
                "Dynamic client registration was rejected for MCP server {ServerName}; retrying once with the default public client id.",
                serverName ?? "(mcp server)");

            // Single retry with the static public client id. No further fallback is attempted, so a
            // second failure propagates unchanged.
            return await connectAsync(DefaultDynamicRegistrationFallbackClientId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Classifies whether a failed MCP connect should be retried once with the static public client
    /// id (issue #1421). Returns true ONLY when all of the following hold: the connection is OAuth,
    /// no explicit client id was configured on the connection, and the failure is specifically a
    /// Dynamic Client Registration rejection (not a generic auth failure such as user-cancelled auth,
    /// a network error, <c>invalid_grant</c>, or a token-endpoint failure).
    /// </summary>
    internal static bool ShouldFallBackToStaticClientId(McpTool tool, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (exception is null)
        {
            return false;
        }

        // Only OAuth connections perform Dynamic Client Registration.
        if (tool.Connection is not OAuthConnection oauth)
        {
            return false;
        }

        // An explicitly configured client id means the failure is not a DCR rejection we should mask:
        // surface the user's configuration error unchanged.
        if (!string.IsNullOrWhiteSpace(oauth.ClientId))
        {
            return false;
        }

        return IsDynamicClientRegistrationRejection(exception);
    }

    private static bool IsDynamicClientRegistrationRejection(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (IsDynamicClientRegistrationRejection(inner))
                    {
                        return true;
                    }
                }
            }

            if (current.Message is { } message &&
                message.Contains(DynamicClientRegistrationFailureMarker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
