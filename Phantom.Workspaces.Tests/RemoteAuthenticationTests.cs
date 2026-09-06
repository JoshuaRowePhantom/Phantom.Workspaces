using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Data-model tests for <see cref="RemoteAuthentication"/> (issue #1455): scheme normalization onto the
/// external AgentSchema discriminators and the <see cref="PhantomOAuthConnection"/> adapter round-trip.
/// </summary>
public sealed class RemoteAuthenticationTests
{
    [Theory]
    [InlineData(RemoteAuthentication.EntraScheme, "oauth", PhantomAgentSchema.EntraPinnedAuthenticationMode)]
    [InlineData(RemoteAuthentication.OAuthScheme, "oauth", "system")]
    [InlineData(RemoteAuthentication.GithubScheme, "key", null)]
    [InlineData(RemoteAuthentication.AnonymousScheme, "anonymous", null)]
    public void SchemeNormalizer_AllSchemes_MapToExpectedKindAndAuthenticationMode(
        string scheme,
        string expectedKind,
        string? expectedAuthenticationMode)
    {
        var (kind, authenticationMode) = RemoteAuthentication.NormalizeScheme(scheme);

        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedAuthenticationMode, authenticationMode);

        // Normalize() on an instance must agree with the static mapping.
        Assert.Equal((expectedKind, expectedAuthenticationMode), new RemoteAuthentication(scheme).Normalize());
    }

    [Fact]
    public void SchemeNormalizer_IsCaseAndWhitespaceInsensitive()
    {
        var (kind, authenticationMode) = RemoteAuthentication.NormalizeScheme("  ENTRA  ");

        Assert.Equal("oauth", kind);
        Assert.Equal(PhantomAgentSchema.EntraPinnedAuthenticationMode, authenticationMode);
    }

    [Fact]
    public void RemoteAuthentication_ToAndFromPhantomOAuthConnection_RoundTripsSharedFields()
    {
        var original = new RemoteAuthentication(
            Scheme: RemoteAuthentication.EntraScheme,
            Endpoint: "https://example.devtunnels.ms/",
            Authority: "https://login.microsoftonline.com/tenant/v2.0",
            ClientId: "client-id",
            ClientSecret: "${SECRET:example}",
            TokenEndpoint: "https://login.microsoftonline.com/tenant/oauth2/v2.0/token",
            Scopes: new List<string> { "api://example/.default" });

        var connection = original.ToPhantomOAuthConnection();

        Assert.Equal("oauth", connection.Kind);
        Assert.Equal(PhantomAgentSchema.EntraPinnedAuthenticationMode, connection.AuthenticationMode);
        Assert.Equal(original.Endpoint, connection.Endpoint);
        Assert.Equal(original.Authority, connection.Authority);
        Assert.Equal(original.ClientId, connection.ClientId);
        Assert.Equal(original.ClientSecret, connection.ClientSecret);
        Assert.Equal(original.TokenEndpoint, connection.TokenUrl);
        Assert.Equal(original.Scopes, connection.Scopes);

        var roundTripped = RemoteAuthentication.FromPhantomOAuthConnection(connection, RemoteAuthentication.EntraScheme);

        Assert.Equal(original.Scheme, roundTripped.Scheme);
        Assert.Equal(original.Endpoint, roundTripped.Endpoint);
        Assert.Equal(original.Authority, roundTripped.Authority);
        Assert.Equal(original.ClientId, roundTripped.ClientId);
        Assert.Equal(original.ClientSecret, roundTripped.ClientSecret);
        Assert.Equal(original.TokenEndpoint, roundTripped.TokenEndpoint);
        Assert.True(original.Scopes!.SequenceEqual(roundTripped.Scopes!));
    }

    [Fact]
    public void FromPhantomOAuthConnection_WithoutExplicitScheme_InfersSchemeFromDiscriminators()
    {
        var entra = RemoteAuthentication.FromPhantomOAuthConnection(new PhantomOAuthConnection
        {
            Kind = "oauth",
            AuthenticationMode = PhantomAgentSchema.EntraPinnedAuthenticationMode,
        });
        Assert.Equal(RemoteAuthentication.EntraScheme, entra.Scheme);

        var github = RemoteAuthentication.FromPhantomOAuthConnection(new PhantomOAuthConnection { Kind = "key" });
        Assert.Equal(RemoteAuthentication.GithubScheme, github.Scheme);

        var anonymous = RemoteAuthentication.FromPhantomOAuthConnection(new PhantomOAuthConnection { Kind = "anonymous" });
        Assert.Equal(RemoteAuthentication.AnonymousScheme, anonymous.Scheme);

        var oauth = RemoteAuthentication.FromPhantomOAuthConnection(new PhantomOAuthConnection { Kind = "oauth" });
        Assert.Equal(RemoteAuthentication.OAuthScheme, oauth.Scheme);
    }
}
