using Phantom.Workspaces.Llm.Auth;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers <see cref="EntraInteractiveCredentialFactory"/> option building (#1420, #1427): the
/// entra-pinned credential must not pin a redirect URI when none is supplied, so MSAL binds its own
/// ephemeral loopback listener rather than colliding with the #1425 shared DCR listener's port.
/// </summary>
public sealed class EntraInteractiveCredentialFactoryTests
{
    private const string Authority = "https://login.microsoftonline.com/contoso/v2.0";

    [Fact]
    public void EntraInteractiveCredentialFactory_NullRedirectUri_DoesNotSetCredentialRedirect()
    {
        // #1427: with a null request RedirectUri the built options leave RedirectUri unset, so MSAL uses
        // its default ephemeral localhost loopback listener.
        var request = new EntraPinnedTokenRequest(Authority, ClientId: null, RedirectUri: null, "server-a");

        var options = EntraInteractiveCredentialFactory.BuildOptions(request);

        Assert.Null(options.RedirectUri);
    }

    [Fact]
    public void EntraInteractiveCredentialFactory_WithRedirectUri_PinsCredentialRedirect()
    {
        // Sanity: when a redirect URI IS explicitly supplied it is applied (the null path is the #1427
        // change, not a removal of the ability to pin one).
        var redirectUri = new Uri("http://localhost:12345/");
        var request = new EntraPinnedTokenRequest(Authority, ClientId: null, redirectUri, "server-a");

        var options = EntraInteractiveCredentialFactory.BuildOptions(request);

        Assert.Equal(redirectUri, options.RedirectUri);
    }

    [Fact]
    public void BuildOptions_EntraPinned_SetsBrowserSuccessMessageWithServerName()
    {
        // #1445 Part B: MSAL's loopback success page must name the authorized server.
        var request = new EntraPinnedTokenRequest(Authority, ClientId: null, RedirectUri: null, "GitHub MCP");

        var options = EntraInteractiveCredentialFactory.BuildOptions(request);

        Assert.NotNull(options.BrowserCustomization);
        Assert.NotNull(options.BrowserCustomization!.SuccessMessage);
        Assert.Contains("GitHub MCP", options.BrowserCustomization.SuccessMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOptions_EntraPinned_ServerName_IsHtmlEncodedInBrowserMessage()
    {
        // #1445 Part B: MSAL renders SuccessMessage/ErrorMessage as raw HTML, so a server name with
        // metacharacters must be HTML-encoded to avoid injection.
        var request = new EntraPinnedTokenRequest(
            Authority, ClientId: null, RedirectUri: null, "<script>alert(1)</script>");

        var options = EntraInteractiveCredentialFactory.BuildOptions(request);

        Assert.DoesNotContain(
            "<script>alert(1)</script>", options.BrowserCustomization!.SuccessMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<script>alert(1)</script>", options.BrowserCustomization.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", options.BrowserCustomization.SuccessMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOptions_EntraPinned_SetsBrowserErrorMessageNamingServer()
    {
        // #1445 Part B: a failed Entra sign-in page must also identify the server.
        var request = new EntraPinnedTokenRequest(Authority, ClientId: null, RedirectUri: null, "GitHub MCP");

        var options = EntraInteractiveCredentialFactory.BuildOptions(request);

        Assert.NotNull(options.BrowserCustomization!.ErrorMessage);
        Assert.Contains("GitHub MCP", options.BrowserCustomization.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void EntraInteractiveCredentialFactory_UnderSharedNamespace_StillBuildsCredential()
    {
        // #1454: after the move to the neutral Phantom.Workspaces.Llm.Auth namespace, the factory must
        // still build a credential from a request carrying an authority, client id and caller name —
        // behaviour identical to the pre-move MCP path.
        var request = new EntraPinnedTokenRequest(
            Authority, ClientId: "client-123", RedirectUri: null, "shared-caller");

        var credential = EntraInteractiveCredentialFactory.Create(request);
        var options = EntraInteractiveCredentialFactory.BuildOptions(request);

        Assert.NotNull(credential);
        Assert.IsType<Azure.Identity.InteractiveBrowserCredential>(credential);
        Assert.Equal("client-123", options.ClientId);
        Assert.Equal("contoso", options.TenantId);
        Assert.Equal(new Uri("https://login.microsoftonline.com/"), options.AuthorityHost);
    }
}
