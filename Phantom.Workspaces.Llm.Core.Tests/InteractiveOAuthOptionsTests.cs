using Phantom.Workspaces.Llm.Auth;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the neutral, auth-generic <see cref="InteractiveOAuthOptions"/> seam (issue #1454, formerly
/// <c>McpOAuthOptions</c>): the "not configured" behaviour must be preserved after the rename/move so
/// an <c>entra-pinned</c> connection with no injected credential provider still fails clearly.
/// </summary>
public sealed class InteractiveOAuthOptionsTests
{
    [Fact]
    public void InteractiveOAuthOptions_ResolveEntraCredential_ThrowsWhenNoProvider()
    {
        // With no EntraCredentialProvider registered (headless/unit context), resolving a host-pinned
        // Entra credential must throw a clear, actionable error rather than returning null.
        var options = new InteractiveOAuthOptions();
        var request = new EntraPinnedTokenRequest(
            "https://login.microsoftonline.com/contoso/v2.0", ClientId: null, RedirectUri: null, "caller-a");

        var exception = Assert.Throws<InvalidOperationException>(() => options.ResolveEntraCredential(request));

        Assert.Contains("Host-pinned Entra authentication is not configured", exception.Message, StringComparison.Ordinal);
        Assert.Contains("caller-a", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveOAuthOptions_ResolveEntraCredential_ReturnsInjectedCredential()
    {
        // When a provider IS registered the resolved credential is the injected one (regression guard
        // that the rename preserved the provider seam).
        var expected = new StubCredential();
        var options = new InteractiveOAuthOptions { EntraCredentialProvider = _ => expected };
        var request = new EntraPinnedTokenRequest(
            "https://login.microsoftonline.com/contoso/v2.0", ClientId: null, RedirectUri: null, "caller-a");

        Assert.Same(expected, options.ResolveEntraCredential(request));
    }

    private sealed class StubCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(this.GetToken(requestContext, cancellationToken));
    }
}
