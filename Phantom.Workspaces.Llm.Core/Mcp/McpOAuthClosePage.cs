using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// Builds the small HTML "you can close this window" page shown in the browser after an interactive
/// MCP OAuth sign-in (#1445). A single builder is shared by both loopback paths — the Phantom-owned
/// DCR/interactive listener (<see cref="Phantom.Workspaces.Services.Mcp"/>'s <c>McpOAuthRedirectHandler</c>)
/// and the Entra-pinned MSAL page (<see cref="EntraInteractiveCredentialFactory"/> via
/// <c>BrowserCustomization</c>) — so the two pages stay visually consistent and the HTML-encoding rules
/// live in one place.
/// </summary>
/// <remarks>
/// Every interpolated value (server name, identity, scopes) is HTML-encoded before it is embedded:
/// MSAL renders these strings as raw HTML and the Phantom listener writes them directly, so an
/// un-encoded value would break the markup or inject content.
/// </remarks>
internal static class McpOAuthClosePage
{
    /// <summary>The base instruction line, present on every variant so callers/tests can rely on it.</summary>
    internal const string CloseInstruction = "You can close this window and return to Phantom.Workspaces.";

    /// <summary>
    /// The context-free fallback page, used when the authorized server is unknown (e.g. an
    /// unmatched/absent OAuth <c>state</c>) so those paths still return a valid page.
    /// </summary>
    public static string Generic() => Document("Sign-in complete", $"<p>{Escape(CloseInstruction)}</p>");

    /// <summary>
    /// A success page naming the authorized <paramref name="serverName"/>, with optional identity and
    /// scope lines rendered only when present. Falls back to <see cref="Generic"/> when no server name
    /// is available.
    /// </summary>
    public static string Success(string? serverName, string? identity = null, IEnumerable<string>? scopes = null)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return Generic();
        }

        var body = new StringBuilder();
        body.Append("<p><strong>Signed in to \u201c").Append(Escape(serverName)).Append("\u201d.</strong> ")
            .Append(Escape(CloseInstruction)).Append("</p>");

        if (!string.IsNullOrWhiteSpace(identity))
        {
            body.Append("<p><em>Signed in as ").Append(Escape(identity)).Append("</em></p>");
        }

        var scopeList = scopes?.Where(scope => !string.IsNullOrWhiteSpace(scope)).ToArray();
        if (scopeList is { Length: > 0 })
        {
            body.Append("<p><em>Scopes: ").Append(Escape(string.Join(" ", scopeList))).Append("</em></p>");
        }

        return Document("Sign-in complete", body.ToString());
    }

    /// <summary>
    /// An error page naming <paramref name="serverName"/> so a failed sign-in is identifiable. Falls back
    /// to <see cref="Generic"/> when no server name is available.
    /// </summary>
    public static string Error(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return Generic();
        }

        var body = $"<p><strong>Sign-in to \u201c{Escape(serverName)}\u201d failed.</strong> {Escape(CloseInstruction)}</p>";
        return Document("Sign-in failed", body);
    }

    private static string Document(string title, string body)
        => "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + Escape(title) + "</title></head>" +
           "<body>" + body + "</body></html>";

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
