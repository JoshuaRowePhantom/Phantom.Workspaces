using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Llm.Interfaces.Access;

public sealed record EntraAuthority
{
    public required Uri Value { get; init; }
}

public sealed record EntraClientIdentifier
{
    public required string Value { get; init; }
}

public sealed record EntraScope
{
    public required string Value { get; init; }
}

public sealed record EntraCachePartition
{
    public required string Value { get; init; }
}

public sealed record EntraCredentialAccessRequest : CredentialAccessRequest<EntraCredentialAccessResult>
{
    public required EntraAuthority Authority { get; init; }
    public required EntraClientIdentifier Client { get; init; }
    public required IReadOnlyList<EntraScope> Scopes { get; init; }
    public required EntraCachePartition CachePartition { get; init; }

    public LogicalCredentialIdentifier CreateLogicalIdentifier() =>
        CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
        {
            Provider = CredentialProviderKind.Entra,
            Kind = CredentialKind.AccessToken,
            CredentialPartition = this.CachePartition.Value,
            SourceCompatibility = "entra-sdk-cache-v1",
            Authority = this.Authority.Value.AbsoluteUri,
            Audience = string.Empty,
            ClientPartition = this.Client.Value,
            Scopes = this.Scopes.Select(scope => scope.Value).ToArray(),
        });

    public CredentialSourceIdentifier CreateSourceIdentifier() =>
        CredentialIdentityFactory.CreateSourceIdentifier(
            CredentialProviderKind.Entra,
            this.CreateLogicalIdentifier().Digest);
}

public sealed class EntraCredentialAccessResult : ICredentialAccessResult
{
    public required SensitiveValueLease AccessToken { get; init; }
    public required DateTimeOffset ExpiresOn { get; init; }
    internal Func<CancellationToken, ValueTask<CredentialGenerationInvalidationOutcome>>? GenerationRejection { private get; init; }

    internal ValueTask<CredentialGenerationInvalidationOutcome> RejectGenerationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return this.GenerationRejection is { } rejection
            ? rejection(cancellationToken)
            : throw new InvalidOperationException("The credential result has no provider generation binding.");
    }

    public ValueTask DisposeAsync() => this.AccessToken.DisposeAsync();
}
