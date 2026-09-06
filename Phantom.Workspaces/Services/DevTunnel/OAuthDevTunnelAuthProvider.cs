using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// The <c>oauth</c> dev-tunnel auth scheme (issue #1458): acquires the Management-API identity token via a
/// generic interactive OAuth authorization-code flow driven by the shared redirect handler. The optional
/// client secret is only ever a <c>${SECRET:…}</c> / <c>${ENV}</c> placeholder resolved on demand through
/// the injected <see cref="secretResolver"/>; a raw secret is never held on the configuration.
/// </summary>
/// <remarks>
/// The interactive flow itself (the shared redirect handler) is supplied as the
/// <paramref name="acquireToken"/> seam so this provider stays free of a hard dependency on the browser
/// loopback listener and is unit-testable with a fake acquirer.
/// </remarks>
public sealed class OAuthDevTunnelAuthProvider : IDevTunnelAuthTokenProvider
{
    /// <summary>The bearer scheme used to carry a generic OAuth access token on the Management-API header.</summary>
    private const string BearerScheme = "Bearer";

    private readonly RemoteAuthentication authentication;
    private readonly Func<OAuthTokenRequest, CancellationToken, Task<string>> acquireToken;
    private readonly Func<string?, CancellationToken, ValueTask<string?>> secretResolver;
    private readonly SemaphoreSlim gate = new(1, 1);

    private string? cachedToken;

    /// <summary>
    /// Creates a generic OAuth identity provider from <paramref name="authentication"/>.
    /// <paramref name="acquireToken"/> performs the interactive flow (shared redirect handler);
    /// <paramref name="secretResolver"/> materializes the <c>${SECRET:…}</c> client-secret placeholder
    /// (defaulting to an env/literal resolver).
    /// </summary>
    public OAuthDevTunnelAuthProvider(
        RemoteAuthentication authentication,
        Func<OAuthTokenRequest, CancellationToken, Task<string>> acquireToken,
        Func<string?, CancellationToken, ValueTask<string?>>? secretResolver = null)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(acquireToken);

        this.authentication = authentication;
        this.acquireToken = acquireToken;
        this.secretResolver = secretResolver ?? DefaultResolveSecretAsync;
    }

    /// <summary>Generic OAuth bearer tokens are carried under the <c>Bearer</c> scheme.</summary>
    public string? AuthenticationScheme => BearerScheme;

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var existing = Volatile.Read(ref this.cachedToken);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        return await this.AcquireAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref this.cachedToken, null);
        return await this.AcquireAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> AcquireAsync(CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = Volatile.Read(ref this.cachedToken);
            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }

            var clientSecret = await this.secretResolver(this.authentication.ClientSecret, cancellationToken)
                .ConfigureAwait(false);
            var request = new OAuthTokenRequest(
                this.authentication.ClientId,
                clientSecret,
                this.authentication.AuthorizationEndpoint,
                this.authentication.TokenEndpoint,
                this.authentication.Scopes);
            var token = await this.acquireToken(request, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref this.cachedToken, token);
            return token;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private static ValueTask<string?> DefaultResolveSecretAsync(string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ValueTask<string?>((string?)null);
        }

        // A ${ENV:NAME} / ${NAME} placeholder resolves from the environment; anything else is a literal.
        if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal))
        {
            var inner = value[2..^1];
            var name = inner.StartsWith("ENV:", StringComparison.OrdinalIgnoreCase) ? inner[4..] : inner;
            return new ValueTask<string?>(Environment.GetEnvironmentVariable(name));
        }

        return new ValueTask<string?>(value);
    }
}

/// <summary>
/// The generic-OAuth parameters (resolved client id / secret and endpoints) handed to the interactive
/// acquisition seam of <see cref="OAuthDevTunnelAuthProvider"/>. Holds a materialized client secret only
/// for the lifetime of a single acquisition call.
/// </summary>
public sealed record OAuthTokenRequest(
    string? ClientId,
    string? ClientSecret,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    System.Collections.Generic.IReadOnlyList<string>? Scopes);
