using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class DevTunnelIdentityRouteFactoryTests
{
    [Fact]
    public void Create_DefaultGitHubAndExplicitAnonymous_AreTypedMetadataOnly()
    {
        var fixture = new Fixture();
        var github = Assert.IsType<GitHubDevTunnelIdentityRoute>(fixture.Create(null));
        Assert.Equal(CredentialProviderKind.GitHub, github.Credential.Credential.Provider);
        Assert.Null(github.Credential.Credential.AccountIdentifier);
        Assert.Equal(AccessPresentationScope.Bootstrap, github.Credential.PresentationScope);
        Assert.IsType<AnonymousDevTunnelIdentityRoute>(
            fixture.Create(new RemoteAuthentication(RemoteAuthentication.AnonymousScheme)));
        Assert.Empty(fixture.Registry.SnapshotConfigurations());
        fixture.Store.VerifyNoOtherCalls();
    }

    [Fact]
    public void Create_OAuthRegistersActualExplicitConfigurationInSharedRegistry_WithoutAnyAcquisition()
    {
        var fixture = new Fixture();
        var authentication = OAuth() with { Scopes = ["profile", "openid", "profile"] };
        var route = Assert.IsType<OAuthDevTunnelIdentityRoute>(fixture.Create(authentication));
        var registered = fixture.Registry.FindConfiguration(route.Credential.Credential.LogicalIdentifier);

        Assert.NotNull(registered);
        Assert.Equal(new Uri(authentication.AccountEndpoint!), registered.Provider.AccountEndpoint);
        Assert.Equal(OAuthAccountIdentifierClaim.Subject, registered.Provider.AccountIdentifierClaim);
        Assert.Equal(authentication.Audience, registered.Provider.Audience);
        Assert.Equal(new[] { "openid", "profile" }, route.Credential.Scopes.Select(scope => scope.Value));
        Assert.Equal(route.Credential.Sources[0].Identifier, registered.Source);
        Assert.Null(route.Credential.Credential.AccountIdentifier);
        Assert.Equal(AccessPresentationScope.Bootstrap, route.Credential.PresentationScope);
        Assert.Same(fixture.Use, route.Credential.Use);
        fixture.Store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("authorization")]
    [InlineData("token")]
    [InlineData("account")]
    [InlineData("claim")]
    [InlineData("audience")]
    [InlineData("client")]
    public void Create_IncompleteOAuthExplicitlyUnsupported_DoesNotGuessOrRegister(string missing)
    {
        var fixture = new Fixture();
        var authentication = missing switch
        {
            "authority" => OAuth() with { Authority = null },
            "authorization" => OAuth() with { AuthorizationEndpoint = null },
            "token" => OAuth() with { TokenEndpoint = null },
            "account" => OAuth() with { AccountEndpoint = null },
            "claim" => OAuth() with { AccountIdentifierClaim = null },
            "audience" => OAuth() with { Audience = null },
            _ => OAuth() with { ClientId = null },
        };

        var failure = Assert.Throws<DevTunnelCredentialAccessException>(() => fixture.Create(authentication));
        Assert.Equal(AccessErrorCode.UnsupportedRequest, failure.ErrorCode);
        Assert.Empty(fixture.Registry.SnapshotConfigurations());
        fixture.Store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("http://provider.example/account")]
    [InlineData("https://user:password@provider.example/account")]
    [InlineData("https://provider.example/account#fragment")]
    public void Create_UnsafeOAuthAccountEndpoint_IsUnsupported(string accountEndpoint)
    {
        var fixture = new Fixture();
        Assert.Throws<DevTunnelCredentialAccessException>(() =>
            fixture.Create(OAuth() with { AccountEndpoint = accountEndpoint }));
        Assert.Empty(fixture.Registry.SnapshotConfigurations());
        fixture.Store.VerifyNoOtherCalls();
    }

    [Fact]
    public void Create_ConfidentialOAuthPreservesOnlyExplicitStoredSecretReference()
    {
        var fixture = new Fixture();
        var route = Assert.IsType<OAuthDevTunnelIdentityRoute>(
            fixture.Create(OAuth() with { ClientSecret = "${SECRET:provider-client}" }));
        var client = Assert.IsType<ConfidentialOAuthClient>(route.Credential.ClientAuthentication);
        Assert.Equal("provider-client", client.ClientSecret.Name);
        fixture.Store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("raw-sensitive-value")]
    [InlineData("${ENV_ONLY}")]
    [InlineData("${SECRET:}")]
    public void Create_UnsupportedSecretSyntax_DoesNotReadOrReinterpretIt(string secret)
    {
        var fixture = new Fixture();
        var failure = Assert.Throws<DevTunnelCredentialAccessException>(() =>
            fixture.Create(OAuth() with { ClientSecret = secret }));
        Assert.DoesNotContain(secret, failure.Message);
        Assert.Empty(fixture.Registry.SnapshotConfigurations());
        fixture.Store.VerifyNoOtherCalls();
    }

    [Fact]
    public void Create_EntraUsesActualConfiguredAuthorityClientAndScopes_WithCanonicalReference()
    {
        var fixture = new Fixture();
        var route = Assert.IsType<EntraDevTunnelIdentityRoute>(fixture.Create(new RemoteAuthentication(
            RemoteAuthentication.EntraScheme,
            Authority: "https://login.microsoftonline.com/tenant/v2.0",
            ClientId: "client",
            Scopes: ["scope-b", "scope-a", "scope-b"],
            TokenCacheKey: "configured-partition")));

        Assert.Equal(route.Credential.CreateLogicalIdentifier(), route.Credential.Credential.LogicalIdentifier);
        Assert.Equal(route.Credential.CreateSourceIdentifier(), Assert.Single(route.Credential.Sources).Identifier);
        Assert.Equal("configured-partition", route.Credential.CachePartition.Value);
        Assert.Equal(new[] { "scope-a", "scope-b" }, route.Credential.Scopes.Select(scope => scope.Value));
        Assert.Null(route.Credential.Credential.AccountIdentifier);
        Assert.Equal(AccessPresentationScope.Bootstrap, route.Credential.PresentationScope);
        fixture.Store.VerifyNoOtherCalls();
    }

    private static RemoteAuthentication OAuth() => new(
        RemoteAuthentication.OAuthScheme,
        Authority: "https://provider.example/",
        ClientId: "client",
        AuthorizationEndpoint: "https://provider.example/authorize",
        TokenEndpoint: "https://provider.example/token",
        AccountEndpoint: "https://provider.example/principal",
        AccountIdentifierClaim: OAuthAccountIdentifierClaim.Subject,
        Audience: "configured-resource");

    private sealed class Fixture
    {
        internal Mock<IPlatformSecretStore> Store { get; } = new(MockBehavior.Strict);
        internal OAuthProviderRegistry Registry { get; }
        internal CredentialUseDescriptor Use { get; } = ReleaseAuthorizationContractTests.Request().Use;
        private readonly DevTunnelIdentityRouteFactory factory;

        internal Fixture()
        {
            this.Registry = new OAuthProviderRegistry(this.Store.Object,
                () => throw new InvalidOperationException("Route mapping cannot bind a callback."),
                () => throw new InvalidOperationException("Route mapping cannot perform HTTP."));
            this.factory = new DevTunnelIdentityRouteFactory(this.Registry);
        }

        internal DevTunnelIdentityRoute Create(RemoteAuthentication? authentication) => this.factory.Create(
            authentication,
            new AccessRequestor
            {
                Identifier = ReleaseAuthorizationContractTests.Request().RequestorIdentifier,
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Application",
            },
            this.Use,
            AccessPresentationScope.Bootstrap);
    }
}
