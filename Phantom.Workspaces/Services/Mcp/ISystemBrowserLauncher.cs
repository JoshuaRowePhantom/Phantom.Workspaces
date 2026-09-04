namespace Phantom.Workspaces.Services.Mcp;

/// <summary>
/// Abstraction over launching the user's system browser at a given URI. Introduced by sub-item #1385
/// so the interactive MCP OAuth redirect handler (<see cref="McpOAuthRedirectHandler"/>) can be unit
/// tested without spawning a real browser process.
/// </summary>
public interface ISystemBrowserLauncher
{
    /// <summary>Opens <paramref name="uri"/> in the user's default system browser.</summary>
    void Open(Uri uri);
}
