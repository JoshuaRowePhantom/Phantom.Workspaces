using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Host-provided seams for building per-scheme <see cref="IDevTunnelAuthTokenProvider"/>s (issue #1458)
/// without <see cref="DevTunnelAuthTokenProviderFactory"/> taking a hard dependency on MSAL (the Entra
/// credential), the interactive OAuth redirect handler, or the GitHub account service. Unit contexts
/// inject fakes; <see cref="Default"/> supplies safe fallbacks.
/// </summary>
public sealed record DevTunnelAuthProviderDependencies
{
    /// <summary>The default dependency set: no injected seams, GitHub/anonymous work without any host wiring.</summary>
    public static DevTunnelAuthProviderDependencies Default { get; } = new();

    /// <summary>Builds the host-pinned Entra <see cref="TokenCredential"/> (defaults to the shared factory).</summary>
    public Func<EntraPinnedTokenRequest, TokenCredential>? EntraCredentialFactory { get; init; }

    /// <summary>Performs the interactive generic-OAuth flow (shared redirect handler). Required for the oauth scheme.</summary>
    public Func<OAuthTokenRequest, CancellationToken, Task<string>>? OAuthTokenAcquirer { get; init; }

    /// <summary>Resolves a <c>${SECRET:…}</c> client-secret placeholder for the oauth scheme.</summary>
    public Func<string?, CancellationToken, ValueTask<string?>>? SecretResolver { get; init; }

    /// <summary>Optional GitHub account upsert service used by the github scheme.</summary>
    public IGitHubAccountUpsertService? GitHubAccountUpsertService { get; init; }

    /// <summary>Clock used for Entra token-cache expiry decisions.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Optional logger factory threaded into the providers.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Optional per-user platform secret store backing the persistent Connect-token cache
    /// (<see cref="CredentialManagerTokenCache"/>, key <c>devtunnel:&lt;name&gt;</c>).
    /// </summary>
    public IPlatformSecretStore? PlatformSecretStore { get; init; }

    /// <summary>Optional "remember" consent store gating silent persistent Connect-token reuse.</summary>
    public IAllowedSecretsStore? ConsentStore { get; init; }
}

/// <summary>
/// Selects the concrete <see cref="IDevTunnelAuthTokenProvider"/> for a data/persistence source from its
/// <see cref="RemoteAuthentication.Scheme"/> (issue #1458): <c>github</c> → <see cref="GitHubDevTunnelAuthTokenProvider"/>,
/// <c>entra</c> → <see cref="EntraDevTunnelAuthProvider"/>, <c>oauth</c> → <see cref="OAuthDevTunnelAuthProvider"/>,
/// <c>anonymous</c> → <see cref="AnonymousDevTunnelAuthProvider"/>. A missing/unknown scheme falls back to
/// GitHub (the legacy behaviour) so existing GitHub-only sources keep working unchanged.
/// </summary>
public static class DevTunnelAuthTokenProviderFactory
{
    /// <summary>
    /// Creates the identity provider for <paramref name="authentication"/> using the supplied
    /// <paramref name="dependencies"/> seams (or <see cref="DevTunnelAuthProviderDependencies.Default"/>).
    /// </summary>
    public static IDevTunnelAuthTokenProvider Create(
        RemoteAuthentication? authentication,
        DevTunnelAuthProviderDependencies? dependencies = null)
    {
        dependencies ??= DevTunnelAuthProviderDependencies.Default;

        var scheme = (authentication?.Scheme ?? RemoteAuthentication.GithubScheme)
            .Trim()
            .ToLowerInvariant();

        return scheme switch
        {
            RemoteAuthentication.EntraScheme => new EntraDevTunnelAuthProvider(
                authentication!,
                dependencies.EntraCredentialFactory,
                dependencies.TimeProvider,
                dependencies.LoggerFactory),
            RemoteAuthentication.OAuthScheme => new OAuthDevTunnelAuthProvider(
                authentication!,
                dependencies.OAuthTokenAcquirer
                    ?? throw new InvalidOperationException(
                        "The oauth dev-tunnel scheme requires an interactive OAuth token acquirer to be configured."),
                dependencies.SecretResolver),
            RemoteAuthentication.AnonymousScheme => new AnonymousDevTunnelAuthProvider(),
            _ => new GitHubDevTunnelAuthTokenProvider(dependencies.GitHubAccountUpsertService),
        };
    }
}
