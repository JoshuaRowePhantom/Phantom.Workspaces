using System.Text.Json;
using AgentSchema;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces.Tests.Access;

public sealed class OAuthPrincipalConfigurationTests
{
    [Theory]
    [InlineData(OAuthAccountIdentifierClaim.Subject)]
    [InlineData(OAuthAccountIdentifierClaim.Identifier)]
    public void RemoteAuthentication_AccountEvidenceRoundTripsThroughActualJsonSchema(OAuthAccountIdentifierClaim claim)
    {
        var original = Configuration() with { AccountIdentifierClaim = claim };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(original, options);
        Assert.Contains("\"accountEndpoint\"", json);
        Assert.Contains($"\"accountIdentifierClaim\":\"{claim}\"", json);
        var loaded = JsonSerializer.Deserialize<RemoteAuthentication>(json, options)!;
        Assert.Equal(original.AccountEndpoint, loaded.AccountEndpoint);
        Assert.Equal(claim, loaded.AccountIdentifierClaim);
        Assert.Equal(original.Authority, loaded.Authority);
        var projected = RemoteAuthentication.FromPhantomOAuthConnection(original.ToPhantomOAuthConnection(), original.Scheme);
        Assert.Equal(original.AccountEndpoint, projected.AccountEndpoint);
        Assert.Equal(claim, projected.AccountIdentifierClaim);
    }

    [Theory]
    [InlineData(OAuthAccountIdentifierClaim.Subject)]
    [InlineData(OAuthAccountIdentifierClaim.Identifier)]
    public void McpSystemOAuth_SanctionedLoadSavePreservesActualConfiguredAccountEvidence(OAuthAccountIdentifierClaim claim)
    {
        var configuration = Configuration() with { AccountIdentifierClaim = claim };
        var tool = new McpTool
        {
            ServerName = "configured-provider",
            Connection = configuration.ToPhantomOAuthConnection(),
        };
        var first = PhantomAgentSchema.McpToolFromJson(tool.ToJson());
        var loaded = Assert.IsType<PhantomOAuthConnection>(first.Connection);
        Assert.Equal("system", loaded.AuthenticationMode);
        Assert.Equal(configuration.AccountEndpoint, loaded.AccountEndpoint);
        Assert.Equal(configuration.Authority, loaded.Authority);
        Assert.Equal(claim, loaded.AccountIdentifierClaim);
        var second = PhantomAgentSchema.McpToolFromJson(first.ToJson());
        var cloned = Assert.IsType<PhantomOAuthConnection>(second.Connection);
        Assert.Equal(loaded.AccountEndpoint, cloned.AccountEndpoint);
        Assert.Equal(loaded.AccountIdentifierClaim, cloned.AccountIdentifierClaim);
    }

    [Fact]
    public void UnconfiguredSystemOAuth_DoesNotAcquireInventedPrincipalMetadata()
    {
        var tool = new McpTool
        {
            ServerName = "unconfigured",
            Connection = new OAuthConnection { Kind = "oauth", AuthenticationMode = "system", Endpoint = "https://resource.test/" },
        };
        Assert.IsNotType<PhantomOAuthConnection>(PhantomAgentSchema.McpToolFromJson(tool.ToJson()).Connection);
    }

    [Fact]
    public void ActualConfigurationFactory_RegistersMetadataWithoutMaterialOrProviderCalls()
    {
        var store = new Mock<IPlatformSecretStore>(MockBehavior.Strict);
        var registry = new OAuthProviderRegistry(store.Object,
            createHttpHandler: () => throw new InvalidOperationException("Metadata must not create provider HTTP."));
        var configuration = Configuration();
        var context = Context();
        var request = new RemoteOAuthCredentialRequestFactory(registry).CreateRequest(
            configuration, new PublicOAuthClient { Client = new() { Value = configuration.ClientId! } },
            new Uri(configuration.Endpoint!), context.Requestor, context.Use, AccessPresentationScope.Bootstrap);
        Assert.Equal(configuration.AccountEndpoint, request.Provider.AccountEndpoint.AbsoluteUri);
        Assert.Equal(configuration.AccountIdentifierClaim, request.Provider.AccountIdentifierClaim);
        Assert.Equal(AccessPresentationScope.Bootstrap, request.PresentationScope);
        Assert.Same(context.Requestor, request.Requestor);
        Assert.Same(context.Use, request.Use);
        Assert.Null(request.Credential.AccountIdentifier);
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("account")]
    [InlineData("claim")]
    [InlineData("token")]
    [InlineData("client")]
    [InlineData("insecure-account")]
    [InlineData("unknown-claim")]
    public void ActualConfigurationFactory_MissingEvidenceFailsExplicitlyWithoutInventedEndpoints(string missing)
    {
        var store = new Mock<IPlatformSecretStore>(MockBehavior.Strict);
        var configuration = Configuration();
        configuration = missing switch
        {
            "authority" => configuration with { Authority = null },
            "account" => configuration with { AccountEndpoint = null },
            "claim" => configuration with { AccountIdentifierClaim = null },
            "token" => configuration with { TokenEndpoint = null },
            "client" => configuration with { ClientId = null },
            "insecure-account" => configuration with { AccountEndpoint = "http://private-marker.test/account" },
            _ => configuration with { AccountIdentifierClaim = (OAuthAccountIdentifierClaim)99 },
        };
        var context = Context();
        var failure = Assert.Throws<OAuthCredentialConfigurationException>(() =>
            new RemoteOAuthCredentialRequestFactory(new OAuthProviderRegistry(store.Object)).CreateRequest(
                configuration, new PublicOAuthClient { Client = new() { Value = "client-id" } },
                new Uri("https://resource.test/"), context.Requestor, context.Use, AccessPresentationScope.Runtime));
        Assert.Equal(AccessErrorCode.UnsupportedRequest, failure.ErrorCode);
        Assert.DoesNotContain("private-marker", failure.Message);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void ConfigurationViewModel_PreservesEvidenceAndRequiresExplicitClaimSelection()
    {
        var original = Configuration() with { Remember = true, TokenCacheKey = "configured-cache" };
        var viewModel = new OAuthAuthSchemeViewModel(original);
        Assert.True(viewModel.IsValid);
        var saved = viewModel.ToRemoteAuthentication();
        Assert.Equal(original.Authority, saved.Authority);
        Assert.Equal(original.Endpoint, saved.Endpoint);
        Assert.Equal(original.AccountEndpoint, saved.AccountEndpoint);
        Assert.Equal(original.AccountIdentifierClaim, saved.AccountIdentifierClaim);
        Assert.Equal(original.Remember, saved.Remember);
        Assert.Equal(original.TokenCacheKey, saved.TokenCacheKey);
        viewModel.AccountIdentifierClaim = null;
        Assert.False(viewModel.IsValid);
        viewModel.AccountIdentifierClaim = OAuthAccountIdentifierClaim.Identifier;
        Assert.True(viewModel.IsValid);
        Assert.Equal(OAuthAccountIdentifierClaim.Identifier, viewModel.ToRemoteAuthentication().AccountIdentifierClaim);
    }

    internal static RemoteAuthentication Configuration() => new(
        RemoteAuthentication.OAuthScheme,
        Endpoint: "https://resource.test/",
        Authority: "https://authority.test/",
        ClientId: "client-id",
        AuthorizationEndpoint: "https://authority.test/authorize",
        TokenEndpoint: "https://authority.test/token",
        Scopes: ["read"])
    {
        AccountEndpoint = "https://authority.test/account",
        AccountIdentifierClaim = OAuthAccountIdentifierClaim.Subject,
    };

    private static (AccessRequestor Requestor, CredentialUseDescriptor Use) Context()
    {
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Tool, "configured-caller"),
            Kind = AccessRequestorKind.Tool,
            SafeDisplayName = "Configured caller",
        };
        var lineage = new CredentialUseLineage();
        return (requestor, new CredentialUseDescriptor
        {
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Tunnel, lineage, requestor.Identifier, "configured-use"),
            Kind = CredentialUseKind.Tunnel,
            Lineage = lineage,
            SafeDisplayName = "Use configured credential",
            MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
        });
    }
}
