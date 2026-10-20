using AgentSchema;

namespace Phantom.Workspaces.Llm.Interfaces.Access;

public sealed record McpProviderConfigurationIdentifier : CredentialDigest;

public sealed record OAuthClientIdentifier
{
    public required string Value { get; init; }
}

public sealed record OAuthScope
{
    public required string Value { get; init; }
}

public abstract record OAuthClientAuthentication
{
    private protected OAuthClientAuthentication() { }
}

public sealed record PublicOAuthClient : OAuthClientAuthentication
{
    public OAuthClientIdentifier? Client { get; init; }
}

public sealed record ConfidentialOAuthClient : OAuthClientAuthentication
{
    public required OAuthClientIdentifier Client { get; init; }
    public required SecretReference ClientSecret { get; init; }
}

public sealed record McpOAuthCredentialUseRequest : CredentialAccessRequest<McpCredentialUseResult>
{
    public required McpProviderConfigurationIdentifier ProviderConfiguration { get; init; }
    public required OAuthClientAuthentication ClientAuthentication { get; init; }
    public required IReadOnlyList<OAuthScope> Scopes { get; init; }
}

public interface IMcpOAuthCredentialRequestCatalog
{
    McpOAuthCredentialUseRequest? CreateRequest(
        McpTool tool,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope);
}

public sealed class McpCredentialUseResult : ICredentialAccessResult
{
    public required CredentialUseLease Use { get; init; }
    internal IMcpCredentialUseSession Session { get; init; } = null!;

    public ValueTask DisposeAsync() => this.Use.DisposeAsync();
}

internal interface IMcpCredentialUseSession : ICredentialUseSession
{
    ValueTask<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken);
}
