using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Access;

internal sealed class RemoteOAuthCredentialRequestFactory(OAuthProviderRegistry registry)
{
    internal OAuthCredentialAccessRequest CreateRequest(
        RemoteAuthentication authentication,
        OAuthClientAuthentication clientAuthentication,
        Uri audience,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope)
    {
        var clientIdentifier = clientAuthentication switch
        {
            PublicOAuthClient client => client.Client?.Value,
            ConfidentialOAuthClient client => client.Client.Value,
            _ => null,
        };
        if (!string.Equals(authentication.Scheme?.Trim(), RemoteAuthentication.OAuthScheme, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(clientIdentifier) || clientIdentifier != authentication.ClientId ||
            !MatchesConfiguredClientSecret(authentication.ClientSecret, clientAuthentication) ||
            !TryHttps(authentication.Authority, out var authority) ||
            !TryHttps(authentication.AuthorizationEndpoint, out var authorizationEndpoint) ||
            !TryHttps(authentication.TokenEndpoint, out var tokenEndpoint) ||
            !TryHttps(authentication.AccountEndpoint, out var accountEndpoint) ||
            authentication.AccountIdentifierClaim is not { } claim || !Enum.IsDefined(claim) ||
            !Uri.TryCreate(authentication.Audience, UriKind.Absolute, out var configuredAudience) ||
            audience is null || !audience.IsAbsoluteUri ||
            configuredAudience != audience)
        {
            throw new OAuthCredentialConfigurationException();
        }

        return registry.CreateRequest(new OAuthProviderDescriptor
        {
            Authority = authority!,
            AuthorizationEndpoint = authorizationEndpoint!,
            TokenEndpoint = tokenEndpoint!,
            AccountEndpoint = accountEndpoint!,
            AccountIdentifierClaim = claim,
            Audience = configuredAudience.AbsoluteUri,
        }, clientAuthentication, (authentication.Scopes ?? []).Select(scope => new OAuthScope { Value = scope }).ToArray(),
            requestor, use, presentationScope);
    }

    private static bool TryHttps(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    private static bool MatchesConfiguredClientSecret(string? configuredSecret, OAuthClientAuthentication clientAuthentication)
    {
        if (clientAuthentication is not ConfidentialOAuthClient confidential)
        {
            return string.IsNullOrWhiteSpace(configuredSecret);
        }

        var name = confidential.ClientSecret?.Name;
        return !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['{', '}']) < 0 &&
            string.Equals(configuredSecret, "${SECRET:" + name + "}", StringComparison.Ordinal);
    }
}

internal sealed class OAuthCredentialConfigurationException()
    : InvalidOperationException("OAuth credential configuration requires explicit trusted provider and account metadata.")
{
    internal AccessErrorCode ErrorCode => AccessErrorCode.UnsupportedRequest;
}
