using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Llm.Interfaces.Access;

[JsonConverter(typeof(JsonStringEnumConverter<OAuthAccountIdentifierClaim>))]
public enum OAuthAccountIdentifierClaim { Subject, Identifier }

public sealed record OAuthProviderDescriptor
{
    public required Uri Authority { get; init; }
    public required Uri AuthorizationEndpoint { get; init; }
    public required Uri TokenEndpoint { get; init; }
    public required Uri AccountEndpoint { get; init; }
    public required string Audience { get; init; }
    public required OAuthAccountIdentifierClaim AccountIdentifierClaim { get; init; }
}

public sealed record OAuthCredentialAccessRequest : CredentialAccessRequest<OAuthCredentialAccessResult>
{
    public required OAuthProviderDescriptor Provider { get; init; }
    public required OAuthClientAuthentication ClientAuthentication { get; init; }
    public required IReadOnlyList<OAuthScope> Scopes { get; init; }
}

public sealed class OAuthCredentialAccessResult : ICredentialAccessResult
{
    public required SensitiveValueLease AccessToken { get; init; }
    public DateTimeOffset? ExpiresOn { get; init; }

    public ValueTask DisposeAsync() => this.AccessToken.DisposeAsync();
}
