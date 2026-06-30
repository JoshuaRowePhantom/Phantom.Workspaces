using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Supplies the dev-tunnel management identity token from the GitHub authentication token.
/// Resolution chain:
/// <list type="number">
///   <item><term>GITHUB_TOKEN env var</term> — return immediately if set.</item>
///   <item><term>gh auth token</term> — invoke the GitHub CLI and return if successful.</item>
///   <item><term>OS keychain cache</term> — return a previously cached device-flow token if present.</item>
///   <item><term>GitHub Device Flow</term> — initiate OAuth device flow (requires a registered GitHub
///     OAuth app client ID embedded in the binary; throws <see cref="InvalidOperationException"/> until
///     an app is registered — see comment below).</item>
/// </list>
/// Steps 1 and 2 are handled by <see cref="GitHubAuthTokenResolver"/>. Steps 3 and 4 provide a
/// browser-based fallback for machines where neither env var nor gh CLI is available.
/// </summary>
public sealed class GitHubDevTunnelAuthTokenProvider : IDevTunnelAuthTokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Steps 1 + 2: GITHUB_TOKEN env var, then `gh auth token` CLI.
        var token = GitHubAuthTokenResolver.Resolve();
        if (!string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(token);
        }

        // Step 3: OS keychain cache (populated by a previously completed device flow).
        var cachedToken = TryGetCachedDeviceFlowToken();
        if (!string.IsNullOrWhiteSpace(cachedToken))
        {
            return Task.FromResult(cachedToken);
        }

        // Step 4: GitHub Device Flow.
        // TODO: Register a GitHub OAuth app for Phantom.Workspaces and embed the client ID here.
        //       The device flow does not require a client secret for public clients.
        //       See: https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow
        throw new InvalidOperationException(
            "Could not resolve a GitHub token for dev tunnel management. " +
            "Set GITHUB_TOKEN or sign in with 'gh auth login'. " +
            "Browser-based OAuth (Device Flow) requires a registered GitHub OAuth app client ID — " +
            "this has not yet been configured for Phantom.Workspaces.");
    }

    /// <summary>
    /// Tries to retrieve a device-flow token previously cached in the OS keychain (Windows Credential
    /// Manager). Returns <see langword="null"/> when no cached token is found.
    /// </summary>
    private static string? TryGetCachedDeviceFlowToken()
    {
        // TODO: Implement Windows Credential Manager read (CredRead / Windows.Security.Credentials.PasswordVault)
        //       and equivalent on macOS (Keychain) / Linux (libsecret / keyutils).
        //       Returning null here means the device flow is always attempted when steps 1-2 fail,
        //       until a real keychain implementation is added.
        return null;
    }
}
