using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Supplies the dev-tunnel management identity token from the GitHub authentication token, reusing the
/// single <see cref="GitHubAuthTokenResolver"/> (the <c>GITHUB_TOKEN</c> environment variable, else
/// <c>gh auth token</c>). This is the same identity the web data-access client already uses for the
/// dev tunnel <c>X-Tunnel-Authorization</c> token, so host and client share one sign-in and no raw
/// token is stored in tracked files.
/// </summary>
public sealed class GitHubDevTunnelAuthTokenProvider : IDevTunnelAuthTokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = GitHubAuthTokenResolver.Resolve();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Could not resolve a GitHub token for dev tunnel management. Set GITHUB_TOKEN or sign in with 'gh auth login'.");
        }

        return Task.FromResult(token);
    }
}
