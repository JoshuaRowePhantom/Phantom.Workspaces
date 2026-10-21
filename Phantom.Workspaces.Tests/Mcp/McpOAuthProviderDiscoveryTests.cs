using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Mcp;
using Phantom.Workspaces.Tests.Access;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed class McpOAuthProviderDiscoveryTests
{
    [Fact]
    public void McpOAuthCatalog_StoreBackedFactory_RegistersCanonicalToolsWithoutInventingPrincipalEndpoints()
    {
        var catalog = McpOAuthComposition.CreateCatalog(new MetadataStore());
        var context = StoredSecretAccessExecutorTests.Request();
        var tool = new McpTool
        {
            Connection = new OAuthConnection
            {
                Endpoint = "https://resource.test/mcp",
                ClientId = "public-client",
                Scopes = ["read"],
            },
        };
        Assert.Empty(catalog.GetConfigurations());
        var first = catalog.CreateRequest(tool, context.Requestor, context.Use, context.PresentationScope)!;
        var second = catalog.CreateRequest(tool, context.Requestor, context.Use, context.PresentationScope)!;
        Assert.Equal(first.ProviderConfiguration, second.ProviderConfiguration);
        var configuration = Assert.Single(catalog.GetConfigurations());
        Assert.Null(configuration.AuthorizationServer);
        Assert.Null(configuration.AccountEndpoint);
        Assert.Equal(first.ProviderConfiguration, configuration.Identifier);
    }

    [Fact]
    public async Task OAuthPrincipalDiscovery_VerifiesIssuerAndUsesAuthenticatedSubject_NotMaterial()
    {
        var state = new IdentityServer();
        var operation = new Operation();
        var resolver = new OAuthPrincipalEvidenceResolver();
        var evidence = await resolver.DiscoverAsync(state.Issuer, state.CreateHandler, operation,
            Proof(operation), TestContext.Current.CancellationToken);
        Assert.Equal(state.Issuer, evidence.Issuer);
        Assert.Equal(state.AccountEndpoint, evidence.AccountEndpoint);
        var first = await resolver.ReadAccountAsync(evidence, CredentialProviderKind.McpOAuth,
            "first-material", state.CreateHandler, TestContext.Current.CancellationToken);
        var renewed = await resolver.ReadAccountAsync(evidence, CredentialProviderKind.McpOAuth,
            "rotated-material", state.CreateHandler, TestContext.Current.CancellationToken);
        Assert.Equal(first, renewed);
        state.Subject = "principal-B";
        var switched = await resolver.ReadAccountAsync(evidence, CredentialProviderKind.McpOAuth,
            "rotated-material", state.CreateHandler, TestContext.Current.CancellationToken);
        Assert.NotEqual(first, switched);
        Assert.Equal(1, state.MetadataRequests);
        Assert.Equal(3, state.AccountRequests);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("userinfo")]
    [InlineData("subject")]
    [InlineData("userinfo-issuer")]
    public async Task OAuthPrincipalDiscovery_MissingOrMismatchedEvidence_NeverFabricatesAccount(string failure)
    {
        var state = new IdentityServer();
        var operation = new Operation();
        var resolver = new OAuthPrincipalEvidenceResolver();
        if (failure == "issuer") { state.MetadataIssuer = new Uri("https://other.test/"); }
        if (failure == "userinfo") { state.ExposeAccountEndpoint = false; }
        if (failure is "issuer" or "userinfo")
        {
            await Assert.ThrowsAsync<HttpRequestException>(async () => await resolver.DiscoverAsync(
                state.Issuer, state.CreateHandler, operation, Proof(operation), TestContext.Current.CancellationToken));
            Assert.Equal(0, state.AccountRequests);
            return;
        }
        var evidence = await resolver.DiscoverAsync(state.Issuer, state.CreateHandler, operation,
            Proof(operation), TestContext.Current.CancellationToken);
        if (failure == "subject") { state.Subject = null; }
        else { state.ReturnedIssuer = new Uri("https://other.test/"); }
        await Assert.ThrowsAsync<HttpRequestException>(async () => await resolver.ReadAccountAsync(
            evidence, CredentialProviderKind.McpOAuth, "opaque-material", state.CreateHandler, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OAuthPrincipalDiscovery_UnconsumedOrExpiredProof_DoesNotReturnEvidence()
    {
        var state = new IdentityServer();
        var operation = new Operation();
        var resolver = new OAuthPrincipalEvidenceResolver();
        var admission = new ProviderInteractionPermit(operation.RequestIdentifier, operation.Revision,
            operation.ExecutorKind, operation.AcquisitionGeneration, operation.CompatibilityKey, () => true);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await resolver.DiscoverAsync(
            state.Issuer, state.CreateHandler, operation, admission, TestContext.Current.CancellationToken));
        Assert.Equal(0, state.MetadataRequests);
        var active = true;
        state.OnMetadata = () => active = false;
        await Assert.ThrowsAsync<HttpRequestException>(async () => await resolver.DiscoverAsync(
            state.Issuer, state.CreateHandler, operation, Proof(operation, () => active), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OAuthPrincipalDiscovery_KnownGitHubRoute_UsesVerifiedImmutableUserId()
    {
        var state = new IdentityServer { GitHubIdentifier = 42 };
        var operation = new Operation();
        var resolver = new OAuthPrincipalEvidenceResolver();
        var evidence = await resolver.DiscoverAsync(new Uri("https://github.com/"), state.CreateHandler,
            operation, Proof(operation), TestContext.Current.CancellationToken);
        Assert.Equal(new Uri("https://api.github.com/user"), evidence.AccountEndpoint);
        var account = await resolver.ReadAccountAsync(evidence, CredentialProviderKind.McpOAuth,
            "opaque-material", state.CreateHandler, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialIdentityFactory.CreateAccountIdentifier(
            CredentialProviderKind.McpOAuth, "https://github.com/\n42"), account);
        Assert.Equal(0, state.MetadataRequests);
    }

    private static ProviderInteractionPermit Proof(Operation operation, Func<bool>? active = null) =>
        new ProviderInteractionPermit(operation.RequestIdentifier, operation.Revision, operation.ExecutorKind,
            operation.AcquisitionGeneration, operation.CompatibilityKey, () => true).BindExecution(active ?? (() => true));

    private sealed class Operation : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey { get; } = new()
        {
            Configuration = new() { Digest = new string('a', 64) },
            Source = new() { Digest = new string('b', 64) },
            Protocol = ProviderProtocolKind.OAuth,
        };
        public UserAuthorizationRequestIdentifier RequestIdentifier { get; } = new() { Value = Guid.NewGuid() };
        public long Revision => 1;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.McpOAuth;
        public Guid AcquisitionGeneration { get; } = Guid.NewGuid();
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Metadata evidence tests do not execute provider acquisition.");
    }

    private sealed class IdentityServer
    {
        internal Uri Issuer { get; } = new("https://issuer.test/tenant");
        internal Uri AccountEndpoint { get; } = new("https://identity.test/userinfo");
        internal Uri? MetadataIssuer { get; set; }
        internal Uri? ReturnedIssuer { get; set; }
        internal bool ExposeAccountEndpoint { get; set; } = true;
        internal string? Subject { get; set; } = "principal-A";
        internal long? GitHubIdentifier { get; set; }
        internal int MetadataRequests { get; set; }
        internal int AccountRequests { get; set; }
        internal Action? OnMetadata { get; set; }
        internal HttpMessageHandler CreateHandler() => new IdentityHandler(this);
    }

    private sealed class IdentityHandler(IdentityServer state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.RequestUri!.AbsolutePath.Contains(".well-known", StringComparison.Ordinal))
            {
                state.MetadataRequests++;
                Assert.Null(request.Headers.Authorization);
                state.OnMetadata?.Invoke();
                body = JsonSerializer.Serialize(new
                {
                    issuer = (state.MetadataIssuer ?? state.Issuer).AbsoluteUri,
                    userinfo_endpoint = state.ExposeAccountEndpoint ? state.AccountEndpoint.AbsoluteUri : null,
                });
            }
            else
            {
                state.AccountRequests++;
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                body = state.GitHubIdentifier is { } identifier
                    ? JsonSerializer.Serialize(new { id = identifier })
                    : state.ReturnedIssuer is { } returned
                        ? JsonSerializer.Serialize(new { sub = state.Subject, iss = returned.AbsoluteUri })
                        : JsonSerializer.Serialize(new { sub = state.Subject });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class MetadataStore : IPlatformSecretStore
    {
        public Task<SecureString?> ReadAsync(string name, CancellationToken cancellationToken) => throw new InvalidOperationException("Metadata cannot read material.");
        public Task WriteAsync(string name, SecureString value, CancellationToken cancellationToken) => throw new InvalidOperationException("Metadata cannot write material.");
        public Task DeleteAsync(string name, CancellationToken cancellationToken) => throw new InvalidOperationException("Metadata cannot delete material.");
        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken cancellationToken) => throw new InvalidOperationException("Metadata cannot inspect material.");
    }
}
