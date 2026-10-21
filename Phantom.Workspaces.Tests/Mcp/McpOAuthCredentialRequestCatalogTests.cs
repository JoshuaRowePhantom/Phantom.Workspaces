using AgentSchema;
using ModelContextProtocol.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Mcp;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Tests.Access;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed class McpOAuthCredentialRequestCatalogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void McpOAuthCatalog_CreateRequest_IsMetadataOnlyAndPreservesTypedUse(bool confidential)
    {
        var configuration = Configuration(confidential);
        var catalog = new McpOAuthCredentialRequestCatalog([configuration]);
        var context = StoredSecretAccessExecutorTests.Request();
        var tool = Tool(confidential);
        var request = catalog.CreateRequest(tool, context.Requestor, context.Use, AccessPresentationScope.Bootstrap);

        Assert.NotNull(request);
        Assert.Equal(configuration.Identifier, request.ProviderConfiguration);
        Assert.Equal(McpOAuthCredentialAccessExecutor.DescribeCredential(configuration), request.Credential);
        Assert.Equal(configuration.ClientAuthentication, request.ClientAuthentication);
        Assert.Same(context.Requestor, request.Requestor);
        Assert.Equal(context.Use.Lineage.Session, request.Use.Lineage.Session);
        Assert.Equal(context.Use.Lineage.Manifest, request.Use.Lineage.Manifest);
        Assert.Equal(CredentialUseKind.Mcp, request.Use.Kind);
        Assert.NotNull(request.Use.Lineage.ProviderConfiguration);
        Assert.Equal(AccessPresentationScope.Bootstrap, request.PresentationScope);
        Assert.Equal(context.Use.MemoryScopes, request.Use.MemoryScopes);
        Assert.DoesNotContain("private", request.Sources[0].SafeDisplayName, StringComparison.Ordinal);
        if (confidential)
        {
            Assert.Equal("private-client-secret", Assert.IsType<ConfidentialOAuthClient>(request.ClientAuthentication).ClientSecret.Name);
        }
    }

    [Fact]
    public void McpOAuthCatalog_DifferentUseAndProviderConfiguration_HaveDistinctUseFingerprints()
    {
        var configuration = Configuration(false);
        var context = StoredSecretAccessExecutorTests.Request();
        var catalog = new McpOAuthCredentialRequestCatalog([configuration]);
        var first = catalog.CreateRequest(Tool(false), context.Requestor, context.Use, context.PresentationScope)!;
        var second = catalog.CreateRequest(Tool(false), context.Requestor, context.Use with
        {
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                context.Use.Kind, context.Use.Lineage, context.Requestor.Identifier, "other-purpose"),
        }, context.PresentationScope)!;
        Assert.NotEqual(first.Use.Fingerprint, second.Use.Fingerprint);

        var otherConfiguration = configuration with
        {
            Identifier = new() { Digest = new string('c', 64) },
            AccountEndpoint = new Uri("https://authority.test/other-account"),
        };
        var other = new McpOAuthCredentialRequestCatalog([otherConfiguration])
            .CreateRequest(Tool(false), context.Requestor, context.Use, context.PresentationScope)!;
        Assert.NotEqual(first.Credential.LogicalIdentifier, other.Credential.LogicalIdentifier);
        Assert.NotEqual(first.Use.Fingerprint, other.Use.Fingerprint);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("client")]
    [InlineData("secret")]
    [InlineData("scope")]
    [InlineData("mode")]
    [InlineData("token-endpoint")]
    [InlineData("authority")]
    [InlineData("transport")]
    public void McpOAuthCatalog_UnsupportedMetadata_NeverSubstitutesConfiguredProvider(string changed)
    {
        var catalog = new McpOAuthCredentialRequestCatalog([Configuration(true)]);
        var tool = Tool(true);
        var connection = Assert.IsType<OAuthConnection>(tool.Connection);
        switch (changed)
        {
            case "endpoint": connection.Endpoint = "https://other.test/mcp"; break;
            case "client": connection.ClientId = "other-client"; break;
            case "secret": connection.ClientSecret = "raw-client-secret"; break;
            case "scope": connection.Scopes = ["other-scope"]; break;
            case "mode": connection.AuthenticationMode = PhantomAgentSchema.EntraPinnedAuthenticationMode; break;
            case "token-endpoint": connection.TokenUrl = "https://other.test/token"; break;
            case "authority": tool.Connection = PhantomOAuthConnection.From(connection, "https://other.test/"); break;
            case "transport": tool = new PhantomMcpTool { Connection = connection, Transport = McpHttpTransport.Sse }; break;
        }
        var context = StoredSecretAccessExecutorTests.Request();
        Assert.Null(catalog.CreateRequest(tool, context.Requestor, context.Use, context.PresentationScope));
    }

    [Fact]
    public void McpOAuthCatalog_ConfiguredSseMode_IsBoundInCredentialIdentity()
    {
        var streamable = Configuration(false);
        var configuration = streamable with { TransportMode = HttpTransportMode.Sse };
        var catalog = new McpOAuthCredentialRequestCatalog([configuration]);
        var context = StoredSecretAccessExecutorTests.Request();
        var request = catalog.CreateRequest(new PhantomMcpTool
        {
            Connection = Tool(false).Connection,
            Transport = McpHttpTransport.Sse,
        }, context.Requestor, context.Use, context.PresentationScope);
        Assert.NotNull(request);
        Assert.NotEqual(McpOAuthCredentialAccessExecutor.DescribeCredential(streamable).LogicalIdentifier,
            request.Credential.LogicalIdentifier);
    }

    [Fact]
    public async Task McpOAuthComposition_CanRegisterRealExecutorBeforeAnyProviderConfiguration()
    {
        var configuration = Configuration(false);
        var context = StoredSecretAccessExecutorTests.Request();
        var request = new McpOAuthCredentialRequestCatalog([configuration])
            .CreateRequest(Tool(false), context.Requestor, context.Use, context.PresentationScope)!;
        var catalog = McpOAuthComposition.CreateCatalog();
        await using var registrations = new CredentialAccessExecutorRegistry();
        var executor = McpOAuthComposition.RegisterExecutor(registrations, catalog, new MetadataOnlyBrowser());
        Assert.Empty((await executor.InspectAsync(request, TestContext.Current.CancellationToken)).Sources);
        catalog.RegisterConfiguration(configuration);
        Assert.Single((await executor.InspectAsync(request, TestContext.Current.CancellationToken)).Sources);
        Assert.Same(configuration.Cache, Assert.Single(catalog.GetConfigurations()).Cache);
    }

    [Fact]
    public void McpOAuthCatalog_DuplicateConfiguration_CannotRebindItsIdentityOrPhysicalPartition()
    {
        var store = new MetadataOnlyStore();
        var configuration = Configuration(false) with { Cache = new CredentialManagerTokenCache(store, "mcp-oauth:", "partition") };
        var catalog = new McpOAuthCredentialRequestCatalog([configuration]);
        catalog.RegisterConfiguration(configuration with
        {
            Scopes = configuration.Scopes.ToArray(),
            Cache = new CredentialManagerTokenCache(store, "mcp-oauth:", "partition"),
        });
        Assert.Single(catalog.GetConfigurations());
        Assert.Throws<InvalidOperationException>(() => catalog.RegisterConfiguration(configuration with
        {
            AccountEndpoint = new Uri("https://authority.test/different-account"),
        }));
        Assert.Throws<InvalidOperationException>(() => catalog.RegisterConfiguration(configuration with
        {
            Cache = new CredentialManagerTokenCache(store, "mcp-oauth:", "other-partition"),
        }));
        Assert.Same(configuration.Cache, catalog.FindConfiguration(configuration.Identifier)!.Cache);
    }

    [Fact]
    public void McpOAuthCatalog_CopiesConfiguredAndReturnedScopes()
    {
        var scopes = new List<OAuthScope> { new() { Value = "read" } };
        var catalog = new McpOAuthCredentialRequestCatalog([Configuration(false) with { Scopes = scopes }]);
        scopes.Add(new() { Value = "other" });
        var context = StoredSecretAccessExecutorTests.Request();
        var request = catalog.CreateRequest(Tool(false), context.Requestor, context.Use, context.PresentationScope)!;
        Assert.Equal("read", Assert.Single(request.Scopes).Value);
        ((OAuthScope[])request.Scopes)[0] = new() { Value = "changed" };
        var later = catalog.CreateRequest(Tool(false), context.Requestor, context.Use, context.PresentationScope)!;
        Assert.Equal("read", Assert.Single(later.Scopes).Value);
    }

    private static McpTool Tool(bool confidential) => new()
    {
        ServerName = "private-server",
        Connection = new OAuthConnection
        {
            Endpoint = "https://resource.test/mcp",
            ClientId = "private-client",
            ClientSecret = confidential ? "${SECRET:private-client-secret}" : null!,
            Scopes = ["read"],
        },
    };

    private static McpOAuthProviderConfiguration Configuration(bool confidential) => new()
    {
        Identifier = new() { Digest = new string('a', 64) },
        Endpoint = new Uri("https://resource.test/mcp"),
        AuthorizationServer = new Uri("https://authority.test/"),
        AccountEndpoint = new Uri("https://authority.test/account"),
        ClientAuthentication = confidential
            ? new ConfidentialOAuthClient
            {
                Client = new() { Value = "private-client" },
                ClientSecret = new() { Name = "private-client-secret" },
            }
            : new PublicOAuthClient { Client = new() { Value = "private-client" } },
        Scopes = [new() { Value = "read" }],
        Source = new() { Digest = new string('b', 64) },
        Cache = new CredentialManagerTokenCache(new MetadataOnlyStore(), "mcp-oauth:", "private-partition"),
        CreateHttpHandler = () => throw new InvalidOperationException("Metadata cannot create HTTP sessions."),
    };

    private sealed class MetadataOnlyStore : IPlatformSecretStore
    {
        public Task<System.Security.SecureString?> ReadAsync(string name, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Metadata cannot read material.");
        public Task WriteAsync(string name, System.Security.SecureString value, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Metadata cannot write material.");
        public Task DeleteAsync(string name, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Metadata cannot delete material.");
        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Metadata catalog cannot inspect private storage.");
    }

    private sealed class MetadataOnlyBrowser : ISystemBrowserLauncher
    {
        public void Open(Uri uri) => throw new InvalidOperationException("Metadata composition cannot open a browser.");
    }
}
