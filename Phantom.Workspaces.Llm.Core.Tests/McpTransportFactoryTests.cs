using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security;
using AgentSchema;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the consolidated MCP transport factory (#1382): the OAuth arm that maps
/// <see cref="OAuthConnection"/> onto <c>HttpClientTransportOptions.OAuth</c>, the injection seams
/// for the interactive redirect delegate (#1385) and token cache (#1384), and the guarantee that
/// Anonymous/API-key behavior is unchanged after consolidation.
/// </summary>
public sealed class McpTransportFactoryTests
{
    private const string HttpEndpoint = "https://example.test/mcp";

    private static Task<IClientTransport> CreateAsync(McpTool tool, AgentServices? services = null)
        => McpTransportFactory.CreateMcpTransportAsync(tool, services, NullLoggerFactory.Instance, CancellationToken.None);

    private static HttpClientTransportOptions GetHttpOptions(IClientTransport transport)
    {
        var field = transport.GetType().GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("HttpClientTransport._options field not found.");
        return (HttpClientTransportOptions)field.GetValue(transport)!;
    }

    private static McpTool OAuthTool(
        string endpoint = HttpEndpoint,
        string? clientId = null,
        string? clientSecret = null,
        IList<string>? scopes = null,
        string serverName = "oauth-server")
        => new()
        {
            ServerName = serverName,
            Connection = new OAuthConnection
            {
                Endpoint = endpoint,
                ClientId = clientId!,
                ClientSecret = clientSecret!,
                Scopes = scopes!,
            },
        };

    private static SecureString ToSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var c in value)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }

    [Fact]
    public async Task CreateMcpTransport_WithOAuthConnection_DoesNotThrow()
    {
        var transport = await CreateAsync(OAuthTool(clientId: "client-123"));

        Assert.NotNull(transport);
        Assert.IsType<HttpClientTransport>(transport);
    }

    [Fact]
    public async Task CreateMcpTransport_WithOAuthConnection_SetsHttpOAuthOptions()
    {
        var transport = await CreateAsync(OAuthTool(clientId: "client-123", clientSecret: "secret-abc"));

        var options = GetHttpOptions(transport);
        Assert.NotNull(options.OAuth);
        Assert.Equal("client-123", options.OAuth!.ClientId);
        Assert.Equal("secret-abc", options.OAuth.ClientSecret);
        Assert.Equal(new Uri(HttpEndpoint), options.Endpoint);
    }

    [Fact]
    public async Task CreateMcpTransport_WithOAuthConnection_DoesNotSetStaticAuthorizationHeader()
    {
        var transport = await CreateAsync(OAuthTool(clientId: "client-123"));

        var options = GetHttpOptions(transport);
        Assert.True(
            options.AdditionalHeaders is null || !options.AdditionalHeaders.ContainsKey("Authorization"),
            "OAuth connections must not add a static Authorization header; auth is delegated to the SDK provider.");
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthScopes_MappedToClientOAuthOptions()
    {
        var transport = await CreateAsync(OAuthTool(scopes: new List<string> { "read", "write" }));

        var options = GetHttpOptions(transport);
        Assert.NotNull(options.OAuth);
        Assert.NotNull(options.OAuth!.Scopes);
        Assert.Equal(new[] { "read", "write" }, options.OAuth.Scopes!.ToArray());
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthClientId_ResolvesEnvPlaceholder()
    {
        const string envVar = "PHANTOM_TEST_OAUTH_CLIENT_ID";
        Environment.SetEnvironmentVariable(envVar, "resolved-client-id");
        try
        {
            var transport = await CreateAsync(OAuthTool(clientId: $"${{{envVar}}}"));

            var options = GetHttpOptions(transport);
            Assert.Equal("resolved-client-id", options.OAuth!.ClientId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthClientSecret_ResolvesSecretPlaceholder()
    {
        const string placeholder = "${SECRET:MyClientSecret}";
        var services = new AgentServices
        {
            SecretPlaceholderResolver = new FakeSecretPlaceholderResolver(placeholder, "resolved-secret-value"),
        };

        var transport = await CreateAsync(OAuthTool(clientSecret: placeholder), services);

        var options = GetHttpOptions(transport);
        Assert.Equal("resolved-secret-value", options.OAuth!.ClientSecret);
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthWithNullClientId_LeavesClientIdNullForDynamicRegistration()
    {
        var transport = await CreateAsync(OAuthTool(clientId: null));

        var options = GetHttpOptions(transport);
        Assert.NotNull(options.OAuth);
        Assert.Null(options.OAuth!.ClientId);
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthOverStdioEndpoint_Throws()
    {
        var tool = OAuthTool(endpoint: "stdio://?command=my-server", serverName: "stdio-oauth");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(tool));
        Assert.Contains("OAuth is not supported for stdio MCP server 'stdio-oauth'", exception.Message);
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthWithoutConfiguredDelegate_UsesFailingDefaultDelegate()
    {
        var transport = await CreateAsync(OAuthTool(serverName: "no-delegate"));

        var options = GetHttpOptions(transport);
        Assert.NotNull(options.OAuth!.AuthorizationRedirectDelegate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => options.OAuth.AuthorizationRedirectDelegate!(
                new Uri("https://auth.test/authorize"),
                new Uri("http://localhost/"),
                CancellationToken.None));
        Assert.Contains("Interactive OAuth is not configured for MCP server 'no-delegate'", exception.Message);
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthWithoutTokenCache_LeavesTokenCacheNull()
    {
        var transport = await CreateAsync(OAuthTool());

        var options = GetHttpOptions(transport);
        Assert.NotNull(options.OAuth);
        Assert.Null(options.OAuth!.TokenCache);
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthWithInjectedDelegate_UsesInjectedDelegate()
    {
        AuthorizationRedirectDelegate injected = (_, _, _) => Task.FromResult<string?>("auth-code");
        var services = new AgentServices
        {
            McpOAuthOptions = new McpOAuthOptions
            {
                RedirectDelegateProvider = _ => injected,
            },
        };

        var transport = await CreateAsync(OAuthTool(), services);

        var options = GetHttpOptions(transport);
        Assert.Same(injected, options.OAuth!.AuthorizationRedirectDelegate);
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthWithConfiguredDelegate_DoesNotUseFailingDefault()
    {
        // #1402: when AgentServices.McpOAuthOptions carries a RedirectDelegateProvider (as it now
        // does on the GUI session-launch path), the resolved AuthorizationRedirectDelegate must be
        // the injected one and must NOT be the throwing "interactive OAuth is not configured" stub.
        AuthorizationRedirectDelegate injected = (_, _, _) => Task.FromResult<string?>("auth-code");
        var services = new AgentServices
        {
            McpOAuthOptions = new McpOAuthOptions
            {
                RedirectDelegateProvider = _ => injected,
            },
        };

        var transport = await CreateAsync(OAuthTool(serverName: "configured-delegate"), services);

        var options = GetHttpOptions(transport);
        Assert.Same(injected, options.OAuth!.AuthorizationRedirectDelegate);

        // Prove it is not the failing default by exercising it: the injected delegate returns a code
        // rather than throwing InvalidOperationException.
        var code = await options.OAuth.AuthorizationRedirectDelegate!(
            new Uri("https://auth.test/authorize"),
            new Uri("http://localhost/"),
            CancellationToken.None);
        Assert.Equal("auth-code", code);
    }

    [Fact]
    public async Task CreateMcpTransport_AnonymousAndKey_StillProduceSameTransportAfterConsolidation()
    {
        var anonymousTransport = await CreateAsync(new McpTool
        {
            ServerName = "anon",
            Connection = new AnonymousConnection { Endpoint = HttpEndpoint },
        });
        var anonymousOptions = GetHttpOptions(anonymousTransport);
        Assert.Null(anonymousOptions.OAuth);
        Assert.True(
            anonymousOptions.AdditionalHeaders is null || !anonymousOptions.AdditionalHeaders.ContainsKey("Authorization"),
            "Anonymous connections must not carry an Authorization header.");

        var keyTransport = await CreateAsync(new McpTool
        {
            ServerName = "keyed",
            Connection = new ApiKeyConnection { Endpoint = HttpEndpoint, ApiKey = "my-api-key" },
        });
        var keyOptions = GetHttpOptions(keyTransport);
        Assert.Null(keyOptions.OAuth);
        Assert.NotNull(keyOptions.AdditionalHeaders);
        Assert.Equal("Bearer my-api-key", keyOptions.AdditionalHeaders!["Authorization"]);
    }

    [Fact]
    public async Task AgentChatAndProvider_UseSameSharedTransportFactory_ForIdenticalConnection()
    {
        // Both McpToolContextProvider (provider path) and AgentChat (which builds transports via the
        // provider) now funnel through the single McpTransportFactory. Calling it for identical
        // connections must yield equivalent transports — the single-source-of-truth guarantee.
        var tool = OAuthTool(clientId: "shared-client", scopes: new List<string> { "read" });

        var first = GetHttpOptions(await CreateAsync(tool));
        var second = GetHttpOptions(await CreateAsync(tool));

        Assert.Equal(first.Endpoint, second.Endpoint);
        Assert.NotNull(first.OAuth);
        Assert.NotNull(second.OAuth);
        Assert.Equal(first.OAuth!.ClientId, second.OAuth!.ClientId);
        Assert.Equal(first.OAuth.Scopes!.ToArray(), second.OAuth.Scopes!.ToArray());
    }

    [Fact]
    public async Task McpTransportFactory_ApiKeyConnection_SecretPlaceholder_ResolvesViaSecretResolver()
    {
        // #1398: the ApiKeyConnection ("key") arm must consult the secret resolver first, exactly
        // like the OAuth arm, so a ${SECRET:...} apiKey resolves without any environment variable.
        const string placeholder = "${SECRET:GitHubToken}";
        var services = new AgentServices
        {
            SecretPlaceholderResolver = new FakeSecretPlaceholderResolver(placeholder, "resolved-secret-token"),
        };

        var transport = await CreateAsync(
            new McpTool
            {
                ServerName = "github-secret-gated",
                Connection = new ApiKeyConnection { Endpoint = HttpEndpoint, ApiKey = placeholder },
            },
            services);

        var options = GetHttpOptions(transport);
        Assert.NotNull(options.AdditionalHeaders);
        Assert.Equal("Bearer resolved-secret-token", options.AdditionalHeaders!["Authorization"]);
    }

    [Fact]
    public async Task McpTransportFactory_ApiKeyConnection_EnvVarStillResolves()
    {
        // #1398 regression: with no resolver entry, a ${ENV_VAR} apiKey still resolves through the
        // ${ENV}/${GITHUB_TOKEN}->gh/literal fallback path.
        const string envVar = "PHANTOM_TEST_MCP_API_KEY";
        Environment.SetEnvironmentVariable(envVar, "env-resolved-key");
        try
        {
            var transport = await CreateAsync(new McpTool
            {
                ServerName = "env-keyed",
                Connection = new ApiKeyConnection { Endpoint = HttpEndpoint, ApiKey = $"${{{envVar}}}" },
            });

            var options = GetHttpOptions(transport);
            Assert.NotNull(options.AdditionalHeaders);
            Assert.Equal("Bearer env-resolved-key", options.AdditionalHeaders!["Authorization"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task CreateMcpTransport_WithoutType_DefaultsToStreamableHttp()
    {
        // #1416: a plain McpTool carries no 'type', so the transport must default to Streamable HTTP
        // (never AutoDetect, whose SSE GET probe is 405-rejected by Streamable-HTTP-only servers).
        var transport = await CreateAsync(new McpTool
        {
            ServerName = "no-type",
            Connection = new AnonymousConnection { Endpoint = HttpEndpoint },
        });

        var options = GetHttpOptions(transport);
        Assert.Equal(HttpTransportMode.StreamableHttp, options.TransportMode);
    }

    [Fact]
    public async Task CreateMcpTransport_WithTypeSse_UsesSse()
    {
        var transport = await CreateAsync(new PhantomMcpTool
        {
            ServerName = "sse-server",
            Connection = new AnonymousConnection { Endpoint = HttpEndpoint },
            Transport = McpHttpTransport.Sse,
        });

        var options = GetHttpOptions(transport);
        Assert.Equal(HttpTransportMode.Sse, options.TransportMode);
    }

    [Fact]
    public async Task CreateMcpTransport_WithTypeAuto_UsesAutoDetect()
    {
        var transport = await CreateAsync(new PhantomMcpTool
        {
            ServerName = "auto-server",
            Connection = new AnonymousConnection { Endpoint = HttpEndpoint },
            Transport = McpHttpTransport.Auto,
        });

        var options = GetHttpOptions(transport);
        Assert.Equal(HttpTransportMode.AutoDetect, options.TransportMode);
    }

    [Fact]
    public async Task CreateMcpTransport_OAuthWithTypeStreamable_UsesStreamableHttp()
    {
        // The OAuth construction site must honor Transport too — both HTTP sites set TransportMode.
        var transport = await CreateAsync(new PhantomMcpTool
        {
            ServerName = "oauth-streamable",
            Connection = new OAuthConnection { Endpoint = HttpEndpoint, ClientId = "client-123" },
            Transport = McpHttpTransport.Streamable,
        });

        var options = GetHttpOptions(transport);
        Assert.NotNull(options.OAuth);
        Assert.Equal(HttpTransportMode.StreamableHttp, options.TransportMode);
    }

    [Fact]
    public async Task CreateMcpTransport_WithClientIdOverride_UsesOverrideInsteadOfConfiguredClientId()
    {
        // #1421: when a clientIdOverride is supplied (the static-fallback build), it wins over the
        // resolved oauth.ClientId even when that is null (the DCR case).
        var transport = await McpTransportFactory.CreateMcpTransportAsync(
            OAuthTool(clientId: null),
            services: null,
            NullLoggerFactory.Instance,
            CancellationToken.None,
            clientIdOverride: "override-client-id");

        var options = GetHttpOptions(transport);
        Assert.NotNull(options.OAuth);
        Assert.Equal("override-client-id", options.OAuth!.ClientId);
    }

    [Fact]
    public async Task CreateMcpTransport_FallbackClientId_IsPublicVsCodeClientId()
    {
        // #1421: the fallback constant is the public VS Code client id, and the fallback build sets
        // no client secret (public client relying on PKCE).
        Assert.Equal(
            "aebc6443-996d-45c2-90f0-388ff96faa56",
            McpTransportFactory.DefaultDynamicRegistrationFallbackClientId);

        var transport = await McpTransportFactory.CreateMcpTransportAsync(
            OAuthTool(clientId: null, clientSecret: "configured-secret"),
            services: null,
            NullLoggerFactory.Instance,
            CancellationToken.None,
            clientIdOverride: McpTransportFactory.DefaultDynamicRegistrationFallbackClientId);

        var options = GetHttpOptions(transport);
        Assert.NotNull(options.OAuth);
        Assert.Equal(McpTransportFactory.DefaultDynamicRegistrationFallbackClientId, options.OAuth!.ClientId);
        Assert.True(
            string.IsNullOrEmpty(options.OAuth.ClientSecret),
            "The static fallback build must not send a client secret (public client on PKCE).");
    }

    [Fact]
    public void ShouldFallBackToStaticClientId_WhenExplicitClientIdConfigured_ReturnsFalse()
    {
        // #1421: an explicitly configured client id means a failure is the user's configuration
        // error, not a DCR rejection — never mask it with the static fallback.
        var tool = OAuthTool(clientId: "explicit-client-id");
        var dcrRejection = new McpException(
            "Failed to handle unauthorized response with 'Bearer' scheme. Authorization server does not support dynamic client registration");

        Assert.False(McpTransportFactory.ShouldFallBackToStaticClientId(tool, dcrRejection));
    }

    [Theory]
    [InlineData("Failed to handle unauthorized response with 'Bearer' scheme. Authorization server does not support dynamic client registration")]
    [InlineData("Failed to handle unauthorized response with 'Bearer' scheme. Dynamic client registration failed with status BadRequest: {\"error\":\"invalid_client_metadata\"}")]
    [InlineData("Failed to handle unauthorized response with 'Bearer' scheme. Dynamic client registration returned empty response")]
    public void ShouldFallBackToStaticClientId_WhenDcrRejected_ReturnsTrue(string message)
    {
        // #1421: with no configured client id, each of the SDK's DCR-rejection messages classifies
        // as a fallback candidate.
        var tool = OAuthTool(clientId: null);

        Assert.True(McpTransportFactory.ShouldFallBackToStaticClientId(tool, new McpException(message)));
    }

    [Fact]
    public void ShouldFallBackToStaticClientId_WhenDcrRejectionIsInnerException_ReturnsTrue()
    {
        // The DCR rejection can be wrapped (e.g. by the connect pipeline); the classifier walks the
        // inner-exception and AggregateException chain.
        var tool = OAuthTool(clientId: null);
        var inner = new McpException(
            "Failed to handle unauthorized response with 'Bearer' scheme. Dynamic client registration failed with status BadRequest");
        var wrapped = new AggregateException(new InvalidOperationException("connect failed", inner));

        Assert.True(McpTransportFactory.ShouldFallBackToStaticClientId(tool, wrapped));
    }

    [Theory]
    [InlineData("The AuthorizationRedirectDelegate returned a null or empty token.")]
    [InlineData("The remote name could not be resolved: 'auth.example.test'")]
    [InlineData("The token endpoint 'https://auth.test/token' returned an empty response.")]
    [InlineData("Response status code does not indicate success: 400 (Bad Request). error=invalid_grant")]
    public void ShouldFallBackToStaticClientId_WhenAuthCancelledOrNetworkError_ReturnsFalse(string message)
    {
        // #1421: non-DCR failures (user-cancelled auth, network error, token-endpoint failure,
        // invalid_grant) must NOT trigger the static fallback so their diagnostic still surfaces.
        var tool = OAuthTool(clientId: null);

        Assert.False(McpTransportFactory.ShouldFallBackToStaticClientId(tool, new McpException(message)));
    }

    [Fact]
    public void ShouldFallBackToStaticClientId_WhenConnectionNotOAuth_ReturnsFalse()
    {
        // Only OAuth connections perform DCR; a DCR-shaped message on a non-OAuth tool never falls
        // back (defensive — such a message cannot originate from a non-OAuth connection).
        var tool = new McpTool
        {
            ServerName = "keyed",
            Connection = new ApiKeyConnection { Endpoint = HttpEndpoint, ApiKey = "key" },
        };
        var dcrRejection = new McpException("Authorization server does not support dynamic client registration");

        Assert.False(McpTransportFactory.ShouldFallBackToStaticClientId(tool, dcrRejection));
    }

    private sealed class FakeSecretPlaceholderResolver : ISecretPlaceholderResolver
    {
        private readonly string placeholder;
        private readonly string value;

        public FakeSecretPlaceholderResolver(string placeholder, string value)
        {
            this.placeholder = placeholder;
            this.value = value;
        }

        public bool TryResolve(string candidate, [NotNullWhen(true)] out SecretRetriever? retriever)
        {
            if (string.Equals(candidate, this.placeholder, StringComparison.Ordinal))
            {
                retriever = new SecretRetriever
                {
                    SecretName = "MyClientSecret",
                    Secret = _ => Task.FromResult(ToSecureString(this.value)),
                };
                return true;
            }

            retriever = null;
            return false;
        }
    }
}
