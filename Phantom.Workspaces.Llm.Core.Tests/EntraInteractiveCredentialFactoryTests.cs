using Phantom.Workspaces.Llm.Mcp;

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
        var request = new McpEntraPinnedTokenRequest(Authority, ClientId: null, RedirectUri: null, "server-a");

        var options = EntraInteractiveCredentialFactory.BuildOptions(request);

        Assert.Null(options.RedirectUri);
    }

    [Fact]
    public void EntraInteractiveCredentialFactory_WithRedirectUri_PinsCredentialRedirect()
    {
        // Sanity: when a redirect URI IS explicitly supplied it is applied (the null path is the #1427
        // change, not a removal of the ability to pin one).
        var redirectUri = new Uri("http://localhost:12345/");
        var request = new McpEntraPinnedTokenRequest(Authority, ClientId: null, redirectUri, "server-a");

        var options = EntraInteractiveCredentialFactory.BuildOptions(request);

        Assert.Equal(redirectUri, options.RedirectUri);
    }

    [Fact]
    public void BuildOptions_EntraPinned_SetsBrowserSuccessMessageWithServerName()
    {
        // #1445 Part B: MSAL's loopback success page must name the authorized server.
        var request = new McpEntraPinnedTokenRequest(Authority, ClientId: null, RedirectUri: null, "GitHub MCP");

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
        var request = new McpEntraPinnedTokenRequest(
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
        var request = new McpEntraPinnedTokenRequest(Authority, ClientId: null, RedirectUri: null, "GitHub MCP");

        var options = EntraInteractiveCredentialFactory.BuildOptions(request);

        Assert.NotNull(options.BrowserCustomization!.ErrorMessage);
        Assert.Contains("GitHub MCP", options.BrowserCustomization.ErrorMessage!, StringComparison.Ordinal);
    }
}
