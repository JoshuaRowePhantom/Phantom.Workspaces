using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Mcp;

internal enum OAuthPrincipalClaimKind { Subject, GitHubUserIdentifier }

internal sealed record OAuthPrincipalEvidence
{
    public required Uri Issuer { get; init; }
    public required Uri AccountEndpoint { get; init; }
    public OAuthPrincipalClaimKind Claim { get; init; }
}

internal sealed class OAuthPrincipalEvidenceResolver
{
    internal async ValueTask<OAuthPrincipalEvidence> DiscoverAsync(
        Uri issuer,
        Func<HttpMessageHandler> createHttpHandler,
        IProviderAcquisitionOperation operation,
        ProviderInteractionPermit executionProof,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool IsAuthorized() => executionProof is not null &&
            (operation.ExecutorKind is CredentialExecutorKind.OAuth or CredentialExecutorKind.McpOAuth) &&
            operation.CompatibilityKey.Protocol == ProviderProtocolKind.OAuth &&
            executionProof.AuthorizesProvider(operation.ExecutorKind, operation.CompatibilityKey) &&
            executionProof.Authorizes(operation.RequestIdentifier, operation.Revision, operation.ExecutorKind,
                operation.AcquisitionGeneration, operation.CompatibilityKey);
        if (!IsAuthorized() ||
            !IsSecureEndpoint(issuer) || !string.IsNullOrEmpty(issuer.Query))
        {
            throw new HttpRequestException("OAuth principal discovery is not authorized.");
        }

        if (issuer == new Uri("https://github.com/"))
        {
            return new OAuthPrincipalEvidence
            {
                Issuer = issuer,
                AccountEndpoint = new Uri("https://api.github.com/user"),
                Claim = OAuthPrincipalClaimKind.GitHubUserIdentifier,
            };
        }

        var metadataEndpoint = new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration");
        using var client = new HttpClient(createHttpHandler());
        using var response = await client.GetAsync(metadataEndpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != metadataEndpoint)
        {
            throw new HttpRequestException("OAuth issuer metadata was redirected.");
        }
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!TryUri(document.RootElement, "issuer", out var verifiedIssuer) || verifiedIssuer != issuer ||
            !TryUri(document.RootElement, "userinfo_endpoint", out var accountEndpoint) || !IsAuthorized())
        {
            throw new HttpRequestException("OAuth stable principal evidence is unavailable.");
        }
        return new OAuthPrincipalEvidence { Issuer = verifiedIssuer, AccountEndpoint = accountEndpoint };
    }

    internal async ValueTask<CredentialAccountIdentifier> ReadAccountAsync(
        OAuthPrincipalEvidence evidence,
        CredentialProviderKind provider,
        string accessToken,
        Func<HttpMessageHandler> createHttpHandler,
        CancellationToken cancellationToken)
    {
        if (provider is not (CredentialProviderKind.OAuth or CredentialProviderKind.McpOAuth) ||
            !IsSecureEndpoint(evidence.Issuer) || !IsSecureEndpoint(evidence.AccountEndpoint))
        {
            throw new HttpRequestException("OAuth stable principal evidence is unavailable.");
        }
        using var client = new HttpClient(createHttpHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, evidence.AccountEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Phantom.Workspaces");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != evidence.AccountEndpoint)
        {
            throw new HttpRequestException("OAuth principal evidence was redirected.");
        }
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("iss", out var returnedIssuer) &&
            (returnedIssuer.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(returnedIssuer.GetString(), UriKind.Absolute, out var actualIssuer) || actualIssuer != evidence.Issuer))
        {
            throw new HttpRequestException("OAuth stable principal evidence does not match its issuer.");
        }

        string? principal = null;
        if (evidence.Claim == OAuthPrincipalClaimKind.GitHubUserIdentifier)
        {
            if (root.TryGetProperty("id", out var identifier) && identifier.ValueKind == JsonValueKind.Number &&
                identifier.TryGetInt64(out var value) && value > 0)
            {
                principal = value.ToString(CultureInfo.InvariantCulture);
            }
        }
        else if (root.TryGetProperty("sub", out var subject) && subject.ValueKind == JsonValueKind.String)
        {
            principal = subject.GetString();
        }
        if (string.IsNullOrWhiteSpace(principal))
        {
            throw new HttpRequestException("OAuth stable principal evidence is unavailable.");
        }
        return CredentialIdentityFactory.CreateAccountIdentifier(provider, $"{evidence.Issuer.AbsoluteUri}\n{principal}");
    }

    private static bool TryUri(JsonElement root, string name, out Uri uri)
    {
        uri = null!;
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(value.GetString(), UriKind.Absolute, out uri!) && IsSecureEndpoint(uri);
    }

    private static bool IsSecureEndpoint(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
}
