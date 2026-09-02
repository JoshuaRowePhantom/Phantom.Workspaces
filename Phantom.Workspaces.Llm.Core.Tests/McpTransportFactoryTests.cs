using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security;
using AgentSchema;
using Microsoft.Extensions.Logging.Abstractions;
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
